using System.Text.Json.Nodes;
using AIDesignStudio.Api.Identity;
using AIDesignStudio.Application.DesignContracts;
using AIDesignStudio.Application.Identity;
using AIDesignStudio.Application.Organizations;
using AIDesignStudio.Application.Payloads;
using AIDesignStudio.Domain.DesignContracts;
using AIDesignStudio.Infrastructure.Persistence;
using AIDesignStudio.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace AIDesignStudio.Api.Endpoints;

/// <summary>
/// M01-05 — the design contract of a project: read it, draft it, edit the
/// draft, discard it, approve it, and read its history.
///
/// <para><b>A project has one contract.</b> The resource is singular and always
/// addressable; before the first draft it is simply empty. Every route is under
/// a project, so tenancy is the project's: a contract in another organization is
/// the same <c>404 project.not_found</c> as a project that does not exist.</para>
///
/// <para><b>The body of a draft is the contract document itself</b>, validated
/// before anything else happens (AC-2.6, AC-2.7): not JSON or not schema-valid
/// is 400 with the failing pointers, a flow step naming an unlisted screen is
/// 422. What is stored is the validator's re-serialization of the one parse it
/// checked. PostgreSQL holds it as <c>jsonb</c>, so it comes back as the same
/// JSON value, not the same bytes — and the approval hash is over the stored
/// text.</para>
///
/// <para><b>Roles are policies</b>, so they are visible in the route table and
/// <c>ContractEndpointsTests</c> can prove none is missing: authoring needs
/// Member, approval needs Approver, reading needs any membership. The domain's
/// refusals (<c>contract.draft_already_open</c>, <c>contract.version_immutable</c>,
/// <c>contract.no_draft</c>) surface as 409 through the host's handler.</para>
///
/// <para><b>Writes are serialized per project</b> — see
/// <see cref="ProjectWriteLock"/> for the corruption that prevents.</para>
/// </summary>
internal static class ContractEndpoints
{
    /// <summary>
    /// The largest body accepted. A contract with hundreds of screens is tens of
    /// kilobytes; the body is buffered to be validated, so it needs a ceiling.
    /// Provisional — payload limits proper are M02 (SPIKE-05).
    /// </summary>
    internal const int MaxPayloadBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapContractEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/projects/{projectId:guid}/contract").WithTags("Design contract");

        // --- Read -------------------------------------------------------------
        group.MapGet(string.Empty, async (
                Guid projectId,
                IOrganizationContext organizations,
                AppDbContext db,
                CancellationToken cancellationToken) =>
            {
                var (refusal, contract) = await LoadForReadAsync(projectId, organizations, db, cancellationToken);

                return refusal ?? Results.Ok(new
                {
                    projectId,
                    approved = contract?.ApprovedVersion is { } approved ? Describe(approved) : null,
                    draft = contract?.DraftVersion is { } draft ? Describe(draft) : null,
                });
            })
            .WithSummary("The approved version and any open draft. Both null before the first draft. Any member.");

        group.MapGet("/versions", async (
                Guid projectId,
                IOrganizationContext organizations,
                AppDbContext db,
                CancellationToken cancellationToken) =>
            {
                var (refusal, contract) = await LoadForReadAsync(projectId, organizations, db, cancellationToken);

                return refusal ?? Results.Ok(new
                {
                    projectId,
                    versions = (contract?.Versions ?? [])
                        .OrderByDescending(version => version.VersionNumber)
                        .Select(Describe),
                });
            })
            .WithSummary("Every version, newest first, with its payload — superseded ones stay readable. Any member.");

        // --- Draft ------------------------------------------------------------
        group.MapPost("/drafts", async (
                Guid projectId,
                HttpRequest request,
                IDesignContractPayloadValidator validator,
                IOrganizationContext organizations,
                AppDbContext db,
                IClock clock,
                CancellationToken cancellationToken) =>
            {
                var (refusal, payload) = await ReadPayloadAsync(request, validator, cancellationToken);

                return refusal ?? await ChangeAsync(
                    projectId, organizations, db, clock, cancellationToken,
                    contract =>
                    {
                        var draft = contract.StartDraft(payload!, clock);
                        return Results.Created($"/api/v1/projects/{projectId}/contract/drafts/current", Describe(draft));
                    },
                    createsContract: true);
            })
            .RequireAuthorization(OrganizationPolicies.Member)
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .WithSummary("Opens a draft. 409 contract.draft_already_open if one is open. Member role.");

        group.MapPut("/drafts/current", async (
                Guid projectId,
                HttpRequest request,
                IDesignContractPayloadValidator validator,
                IOrganizationContext organizations,
                AppDbContext db,
                IClock clock,
                CancellationToken cancellationToken) =>
            {
                var (refusal, payload) = await ReadPayloadAsync(request, validator, cancellationToken);

                return refusal ?? await ChangeAsync(
                    projectId, organizations, db, clock, cancellationToken,
                    contract => Results.Ok(Describe(contract.UpdateDraft(payload!, clock))));
            })
            .RequireAuthorization(OrganizationPolicies.Member)
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .WithSummary("Replaces the open draft. 409 contract.version_immutable if the contract is approved and no draft is open. Member role.");

        group.MapDelete("/drafts/current", (
                Guid projectId,
                IOrganizationContext organizations,
                AppDbContext db,
                IClock clock,
                CancellationToken cancellationToken) =>
            ChangeAsync(
                projectId, organizations, db, clock, cancellationToken,
                contract =>
                {
                    contract.DiscardDraft(clock);
                    return Results.NoContent();
                }))
            .RequireAuthorization(OrganizationPolicies.Member)
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .WithSummary("Discards the open draft. 409 contract.no_draft if none. Member role.");

        // --- Approve ----------------------------------------------------------
        group.MapPost("/drafts/current/approve", (
                Guid projectId,
                ICurrentUser currentUser,
                IOrganizationContext organizations,
                AppDbContext db,
                IClock clock,
                CancellationToken cancellationToken) =>
            ChangeAsync(
                projectId, organizations, db, clock, cancellationToken,

                // The approver is the session's immutable user id — never a
                // request field, and never an email address (W2-005).
                contract => Results.Ok(Describe(contract.ApproveDraft(currentUser.RequireAuditIdentity(), clock)))))
            .RequireAuthorization(OrganizationPolicies.Approver)
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .WithSummary("Approves the open draft, superseding the approved version and recording its hash. Approver role.");

        return app;
    }

    private static async Task<(IResult? Refusal, DesignContract? Contract)> LoadForReadAsync(
        Guid projectId, IOrganizationContext organizations, AppDbContext db, CancellationToken cancellationToken)
    {
        if (await organizations.GetAccessAsync(cancellationToken) is null)
        {
            return (OrganizationProblems.NoMembership(), null);
        }

        // Both queries rely on the organization query filter, opened by the
        // call above. The project is checked first so that "no contract yet"
        // and "no such project" are different answers.
        if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            return (ProjectEndpoints.NotFound(), null);
        }

        return (null, await db.DesignContracts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProjectId == projectId, cancellationToken));
    }

    /// <summary>
    /// Applies one domain operation to the project's contract, alone.
    ///
    /// <para>Everything from the lock to the commit is one transaction, so the
    /// aggregate the operation judges is the one it then writes. A
    /// <see cref="DomainException"/> leaves through the host's handler as a 409
    /// and the transaction rolls back on disposal.</para>
    /// </summary>
    /// <param name="createsContract">
    /// True only for opening a draft. Every other operation on a project that
    /// has no contract row runs against an empty aggregate, which refuses with
    /// <c>contract.no_draft</c> — the right answer — and is never saved.
    /// </param>
    private static async Task<IResult> ChangeAsync(
        Guid projectId,
        IOrganizationContext organizations,
        AppDbContext db,
        IClock clock,
        CancellationToken cancellationToken,
        Func<DesignContract, IResult> change,
        bool createsContract = false)
    {
        // The role policy has already proved a membership. This call is what
        // opens the data scope for the queries below.
        _ = await organizations.GetAccessAsync(cancellationToken)
            ?? throw new InvalidOperationException("A role policy succeeded without a resolvable membership.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

        if (project is null)
        {
            return ProjectEndpoints.NotFound();
        }

        await db.LockProjectForWriteAsync(project.Id, cancellationToken);

        // Loaded after the lock, so this is the state the previous writer left.
        var contract = await db.DesignContracts
            .FirstOrDefaultAsync(c => c.ProjectId == project.Id, cancellationToken);

        var isNew = contract is null;
        contract ??= DesignContract.Create(project.Ownership, clock);

        var result = change(contract);

        if (isNew && createsContract)
        {
            db.DesignContracts.Add(contract);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return result;
    }

    /// <summary>
    /// Reads and validates a draft body. Returns either a refusal or the text
    /// to store — the validator's own serialization, not the caller's bytes.
    /// </summary>
    private static async Task<(IResult? Refusal, string? Payload)> ReadPayloadAsync(
        HttpRequest request, IDesignContractPayloadValidator validator, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
        {
            return (Results.Problem(
                type: "https://aidesignstudio.dev/problems/request.unsupported_media_type",
                title: "Unsupported media type",
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                detail: "Send the design contract as application/json.",
                extensions: new Dictionary<string, object?> { ["code"] = "request.unsupported_media_type" }), null);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];

        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, cancellationToken);

            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxPayloadBytes)
            {
                return (Results.Problem(
                    type: "https://aidesignstudio.dev/problems/contract.payload_too_large",
                    title: "Design contract too large",
                    statusCode: StatusCodes.Status413PayloadTooLarge,
                    detail: $"A design contract may be at most {MaxPayloadBytes} bytes.",
                    extensions: new Dictionary<string, object?> { ["code"] = "contract.payload_too_large" }), null);
            }

            buffer.Write(chunk, 0, read);
        }

        var result = validator.Validate(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));

        return result.Outcome switch
        {
            PayloadValidationOutcome.Valid => (null, result.CanonicalJson),

            PayloadValidationOutcome.ReferenceInvalid => (Invalid(
                StatusCodes.Status422UnprocessableEntity,
                "contract.unknown_screen_reference",
                "Unknown screen reference",
                "A flow step refers to a screen key that /screens does not list.",
                result), null),

            PayloadValidationOutcome.NotJson => (Invalid(
                StatusCodes.Status400BadRequest,
                "contract.payload_invalid",
                "Invalid design contract",
                "The body is not a JSON document.",
                result), null),

            _ => (Invalid(
                StatusCodes.Status400BadRequest,
                "contract.payload_invalid",
                "Invalid design contract",
                "The body does not satisfy design-contract.schema.json. Each error gives a JSON " +
                "Pointer to the value that failed and the schema keyword it broke.",
                result), null),
        };
    }

    private static IResult Invalid(
        int status, string code, string title, string detail, PayloadValidationResult result) => Results.Problem(
        type: $"https://aidesignstudio.dev/problems/{code}",
        title: title,
        statusCode: status,
        detail: detail,
        extensions: new Dictionary<string, object?>
        {
            ["code"] = code,
            ["errors"] = result.Violations
                .Select(v => new { pointer = v.Pointer, keyword = v.Keyword, message = v.Message })
                .ToArray(),
            ["truncated"] = result.Truncated,
        });

    private static object Describe(DesignContractVersion version) => new
    {
        id = version.Id,
        versionNumber = version.VersionNumber,
        status = version.Status.ToString(),

        // As JSON, not as a string of JSON.
        payload = JsonNode.Parse(version.PayloadJson),
        payloadHash = version.PayloadHash,
        approvedBy = version.ApprovedBy,
        approvedAt = version.ApprovedAt,
        createdAt = version.CreatedAt,
    };
}
