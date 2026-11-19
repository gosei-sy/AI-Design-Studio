using System.ComponentModel.DataAnnotations;
using AIDesignStudio.Api.Identity;
using AIDesignStudio.Application.Organizations;
using AIDesignStudio.Domain.Projects;
using AIDesignStudio.Infrastructure.Persistence;
using AIDesignStudio.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace AIDesignStudio.Api.Endpoints;

/// <summary>
/// M01-04 — projects. Create, list, and read one; nothing else.
///
/// <para><b>Tenancy is the caller's membership, and nothing the caller
/// sends.</b> A project is created in <see cref="OrganizationAccess.OrganizationId"/>;
/// the request body has no organization field, and one supplied anyway is
/// ignored. Reads carry no organization predicate at all: the global query filter
/// on <see cref="DesignProject"/> supplies it, once
/// <see cref="IOrganizationContext.GetAccessAsync"/> has opened the request's
/// scope — which is why every handler calls it before it queries. A project in
/// another organization is therefore not found, with the same 404 as an id that
/// never existed (M01-03).</para>
///
/// <para><b>Roles.</b> Reading needs any active membership. Creating needs the
/// Member role, as a policy: <see cref="Domain.Organizations.OrganizationRoles.Member"/>
/// is "may read and author", and an Approver-only membership is legal. That
/// reading is assumption A-16. Every refusal is a coded 403 — the policy's
/// through <see cref="OrganizationForbiddenResultHandler"/> — so the studio can
/// say what to do next.</para>
///
/// <para><b><c>kind</c> is chosen once.</b> There is no update endpoint, and
/// <c>ProjectEndpointsTests.Only_project_creation_binds_a_kind</c> fails if any
/// later non-GET endpoint under <c>/api/v1/projects</c> binds a request type
/// with a <c>Kind</c> property.</para>
/// </summary>
internal static class ProjectEndpoints
{
    private const string Route = "/api/v1/projects";

    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Route)
            .WithTags("Projects")
            .AddEndpointFilter<RequestValidationEndpointFilter>();

        // --- Create -----------------------------------------------------------
        group.MapPost(string.Empty, async (
                CreateProjectRequest request,
                IOrganizationContext organizations,
                AppDbContext db,
                IClock clock,
                CancellationToken cancellationToken) =>
            {
                // The Member policy has already proved the membership and the
                // role; this reads the same resolved answer.
                var access = await organizations.GetAccessAsync(cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The Member policy succeeded without a resolvable membership.");

                // Validation has already proved both fields present and the kind
                // a defined name, so the null-forgiving operators state a fact.
                var project = DesignProject.Create(
                    access.OrganizationId, request.Name!, request.ParsedKind!.Value, clock);

                db.Projects.Add(project);
                await db.SaveChangesAsync(cancellationToken);

                return Results.Created($"{Route}/{project.Id}", Describe(project));
            })
            .RequireAuthorization(OrganizationPolicies.Member)
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .WithSummary("Creates a project in the caller's organization. Member role. Kind is immutable.");

        // --- List -------------------------------------------------------------
        group.MapGet(string.Empty, async (
                IOrganizationContext organizations,
                AppDbContext db,
                CancellationToken cancellationToken) =>
            {
                if (await organizations.GetAccessAsync(cancellationToken) is null)
                {
                    return OrganizationProblems.NoMembership();
                }

                // No organization predicate, deliberately: the query filter is
                // the mechanism (ADR-0009 A1), and the scope it reads was opened
                // by the call above. A per-query Where would be a second,
                // forgettable copy of the same rule.
                var projects = await db.Projects.AsNoTracking()
                    .OrderByDescending(p => p.CreatedAt)
                    .ThenByDescending(p => p.Id)
                    .ToListAsync(cancellationToken);

                return Results.Ok(new { projects = projects.Select(Describe) });
            })
            .WithSummary("Lists the caller's organization's projects, newest first. Any member.");

        // --- Read one ---------------------------------------------------------
        group.MapGet("/{projectId:guid}", async (
                Guid projectId,
                IOrganizationContext organizations,
                AppDbContext db,
                CancellationToken cancellationToken) =>
            {
                if (await organizations.GetAccessAsync(cancellationToken) is null)
                {
                    return OrganizationProblems.NoMembership();
                }

                var project = await db.Projects.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

                return project is null ? NotFound() : Results.Ok(Describe(project));
            })
            .WithSummary("Reads one project. Another organization's project is 404, never 403.");

        return app;
    }

    private static object Describe(DesignProject project) => new
    {
        id = project.Id,
        name = project.Name,

        // By name, never by number: the enum's declaration order is not a
        // contract, and the column stores the name too.
        kind = project.Kind.ToString(),
        createdAt = project.CreatedAt,
        updatedAt = project.UpdatedAt,
    };

    /// <summary>
    /// The same body for "in another organization" and "does not exist", so the
    /// response cannot be used to learn that someone else's project exists.
    /// </summary>
    internal static IResult NotFound() => Results.Problem(
        type: "https://aidesignstudio.dev/problems/project.not_found",
        title: "Project not found",
        statusCode: StatusCodes.Status404NotFound,
        detail: "No such project.",
        extensions: new Dictionary<string, object?> { ["code"] = "project.not_found" });

}

/// <summary>
/// Body of <c>POST /api/v1/projects</c>: <c>{ name, kind }</c>, and nothing
/// else is read — an <c>organizationId</c> in the body is ignored by design.
///
/// <para><c>[property: …]</c> because the attributes must land on the
/// properties the validator reads, not on the constructor parameters (see
/// CLAUDE.md, "where things will trip you up").</para>
/// </summary>
internal sealed record CreateProjectRequest(
    [property: Required, MaxLength(DesignProject.MaxNameLength)] string? Name,
    [property: Required] string? Kind) : IValidatableObject
{
    /// <summary>The kinds by their exact names. Nothing else is accepted.</summary>
    private static readonly Dictionary<string, ProjectKind> KindsByName =
        Enum.GetValues<ProjectKind>().ToDictionary(k => k.ToString(), StringComparer.Ordinal);

    /// <summary>The kind, when <see cref="Kind"/> names one exactly; otherwise null.</summary>
    public ProjectKind? ParsedKind =>
        Kind is not null && KindsByName.TryGetValue(Kind, out var kind) ? kind : null;

    /// <summary>
    /// Accepts a kind only by its exact name. <c>Enum.TryParse</c> would also
    /// accept <c>"1"</c> and <c>"newproduct"</c>, binding clients to the enum's
    /// declaration order and to a casing nobody documented.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Kind is not null && ParsedKind is null)
        {
            yield return new ValidationResult(
                $"'{Kind}' is not a project kind. Use one of: {string.Join(", ", KindsByName.Keys)}.",
                [nameof(Kind)]);
        }
    }
}
