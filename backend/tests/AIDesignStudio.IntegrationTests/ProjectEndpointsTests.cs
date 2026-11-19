using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AIDesignStudio.Domain.Organizations;
using AIDesignStudio.Domain.Projects;
using AIDesignStudio.Infrastructure.Organizations;
using AIDesignStudio.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AIDesignStudio.IntegrationTests;

/// <summary>
/// M01-04 — project endpoints, against the real host and a real PostgreSQL
/// database.
///
/// <para>The acceptance criteria are: <c>POST /api/v1/projects</c> returns 201;
/// list and by-id are organization-scoped; <c>kind</c> cannot be changed because
/// no endpoint accepts it after creation; domain errors surface as problem
/// details with their codes. Each test asks what a caller would observe — a
/// status, a body, a row — rather than whether a mechanism is registered.</para>
///
/// <para><b>Roles.</b> Reading needs any active membership; creating needs the
/// Member role. An Approver-only membership is legal (roles 1..3), and
/// <c>OrganizationRoles.Member</c> is documented as "may read and author", so
/// authoring is Member's. That reading is recorded as assumption A-16 for the
/// Product Owner to confirm.</para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ProjectEndpointsTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private const string Password = "Correct-Horse-9-Battery!";
    private const string Operator = "ops@example.test";
    private const string Projects = "/api/v1/projects";

    private StudioApiFactory? _factory;

    private StudioApiFactory Factory => _factory
        ?? throw new InvalidOperationException("The test host was not initialised.");

    public ValueTask InitializeAsync()
    {
        if (fixture.IsAvailable)
        {
            _factory = new StudioApiFactory(fixture.ConnectionString);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    private void RequireDatabase() => Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}";

    // -----------------------------------------------------------------------
    // Harness — the same shape as AuthorizationTests: provisioning goes
    // through the real OperatorProvisioningService, never a hand-written row.
    // -----------------------------------------------------------------------

    private async Task<ProvisionResult> ProvisionAsync(
        string email, string organization, OrganizationRoles roles)
    {
        using var scope = Factory.Services.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<OperatorProvisioningService>();

        return await provisioning.ProvisionAsync(
            new ProvisionRequest(email, organization, roles, Operator),
            _ => Task.FromResult(Password),
            Ct);
    }

    private async Task<StudioClient> SignInAsync(string email)
    {
        var client = new StudioClient(Factory.CreateBrowserClient());
        (await client.LoginAsync(email, Password, null, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    private async Task<(StudioClient Client, ProvisionResult Provisioned)> SignedInAsync(
        OrganizationRoles roles)
    {
        var email = $"{Unique("user")}@example.test";
        var provisioned = await ProvisionAsync(email, Unique("org"), roles);
        return (await SignInAsync(email), provisioned);
    }

    private async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> CreateAsync(StudioClient client, string name, string kind)
    {
        var response = await client.PostAsync(Projects, new { name, kind }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await JsonAsync(response);
    }

    /// <summary>Sends any method with a fresh antiforgery token, as the studio will.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        StudioClient client, HttpMethod method, string path, object? payload)
    {
        var token = await client.GetAntiforgeryTokenAsync(Ct);
        using var request = new HttpRequestMessage(method, path);

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        request.Headers.Add(StudioClient.AntiforgeryHeader, token);
        return await client.Http.SendAsync(request, Ct);
    }

    private static string Code(JsonElement problem) => problem.GetProperty("code").GetString()!;

    // =======================================================================
    // 1. Create
    // =======================================================================

    [Fact]
    public async Task Creating_a_project_returns_201_with_its_location_and_body()
    {
        RequireDatabase();
        var (client, _) = await SignedInAsync(OrganizationRoles.Member);

        var response = await client.PostAsync(
            Projects, new { name = "  Storefront  ", kind = "NewProduct" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await JsonAsync(response);

        var id = body.GetProperty("id").GetGuid();
        response.Headers.Location!.OriginalString.ShouldBe($"{Projects}/{id}");
        body.GetProperty("name").GetString().ShouldBe("Storefront");
        body.GetProperty("kind").GetString().ShouldBe("NewProduct");
        body.TryGetProperty("organizationId", out _).ShouldBeFalse(
            "the caller's organization is implied by the session and is not echoed");
    }

    [Theory]
    [InlineData("NewProduct")]
    [InlineData("Redesign")]
    public async Task Both_workflow_kinds_can_be_created(string kind)
    {
        RequireDatabase();
        var (client, _) = await SignedInAsync(OrganizationRoles.Member);

        (await CreateAsync(client, "Project", kind)).GetProperty("kind").GetString().ShouldBe(kind);
    }

    /// <summary>
    /// The organization is the caller's membership, never a body field. A body
    /// naming another tenant must neither be honoured nor be an error that
    /// reveals the tenant exists.
    /// </summary>
    [Fact]
    public async Task A_created_project_belongs_to_the_callers_organization_whatever_the_body_says()
    {
        RequireDatabase();
        var (client, mine) = await SignedInAsync(OrganizationRoles.Member);
        var victim = await ProvisionAsync(
            $"{Unique("victim")}@example.test", Unique("victim"), OrganizationRoles.Member);

        var response = await client.PostAsync(Projects, new
        {
            name = "Planted",
            kind = "NewProduct",
            organizationId = victim.OrganizationId,
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await JsonAsync(response)).GetProperty("id").GetGuid();

        await using var db = fixture.CreateContext();
        var stored = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == id, Ct);
        stored.OrganizationId.ShouldBe(mine.OrganizationId);
        stored.OrganizationId.ShouldNotBe(victim.OrganizationId);
    }

    // =======================================================================
    // 2. Read — organization-scoped, and cross-tenant is 404
    // =======================================================================

    [Fact]
    public async Task Listing_returns_only_the_callers_organizations_projects()
    {
        RequireDatabase();
        var (alice, _) = await SignedInAsync(OrganizationRoles.Member);
        var (bob, _) = await SignedInAsync(OrganizationRoles.Member);

        var first = await CreateAsync(alice, "Alice one", "NewProduct");
        var second = await CreateAsync(alice, "Alice two", "Redesign");
        var bobs = await CreateAsync(bob, "Bob only", "NewProduct");

        var response = await alice.Http.GetAsync(Projects, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var ids = (await JsonAsync(response)).GetProperty("projects").EnumerateArray()
            .Select(p => p.GetProperty("id").GetGuid())
            .ToArray();

        ids.ShouldBe(
            [second.GetProperty("id").GetGuid(), first.GetProperty("id").GetGuid()],
            "newest first, and only Alice's organization");
        ids.ShouldNotContain(bobs.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_new_organization_lists_no_projects()
    {
        RequireDatabase();
        var (client, _) = await SignedInAsync(OrganizationRoles.Member);

        var body = await JsonAsync(await client.Http.GetAsync(Projects, Ct));

        body.GetProperty("projects").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Reading_your_own_project_by_id_works()
    {
        RequireDatabase();
        var (client, _) = await SignedInAsync(OrganizationRoles.Member);
        var created = await CreateAsync(client, "Mine", "Redesign");
        var id = created.GetProperty("id").GetGuid();

        var response = await client.Http.GetAsync($"{Projects}/{id}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body.GetProperty("id").GetGuid().ShouldBe(id);
        body.GetProperty("name").GetString().ShouldBe("Mine");
        body.GetProperty("kind").GetString().ShouldBe("Redesign");
    }

    /// <summary>
    /// 404, and the same 404 a nonexistent id gets. A 403 here, or any
    /// difference between the two bodies, would tell a caller that another
    /// tenant's project exists (M01-03).
    /// </summary>
    [Fact]
    public async Task Another_organizations_project_is_indistinguishable_from_a_missing_one()
    {
        RequireDatabase();
        var (owner, _) = await SignedInAsync(OrganizationRoles.Member);
        var (stranger, _) = await SignedInAsync(OrganizationRoles.Member | OrganizationRoles.Approver);
        var id = (await CreateAsync(owner, "Private", "NewProduct")).GetProperty("id").GetGuid();

        var theirs = await stranger.Http.GetAsync($"{Projects}/{id}", Ct);
        var missing = await stranger.Http.GetAsync($"{Projects}/{Guid.CreateVersion7()}", Ct);

        theirs.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var a = await JsonAsync(theirs);
        var b = await JsonAsync(missing);
        Code(a).ShouldBe("project.not_found");
        Code(b).ShouldBe("project.not_found");
        a.GetProperty("detail").GetString().ShouldBe(b.GetProperty("detail").GetString());
    }

    // =======================================================================
    // 3. Who may call
    // =======================================================================

    [Theory]
    [InlineData("GET", Projects)]
    [InlineData("GET", Projects + "/0190a2f0-0000-7000-8000-000000000000")]
    [InlineData("POST", Projects)]
    public async Task An_anonymous_request_is_refused(string method, string path)
    {
        RequireDatabase();
        var client = new StudioClient(Factory.CreateBrowserClient());

        var response = method == "GET"
            ? await client.Http.GetAsync(path, Ct)
            : await client.PostAsync(path, new { name = "x", kind = "NewProduct" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Signed in, provisioned by nobody (ADR-0009). The same coded 403 the
    /// organization endpoints give, so the studio can say "ask an operator".
    /// </summary>
    [Fact]
    public async Task An_unprovisioned_account_reaches_no_project()
    {
        RequireDatabase();
        var client = new StudioClient(Factory.CreateBrowserClient());
        await client.SignUpAndSignInAsync($"{Unique("nobody")}@example.test", Password, Ct);

        var list = await client.Http.GetAsync(Projects, Ct);
        var create = await client.PostAsync(Projects, new { name = "x", kind = "NewProduct" }, Ct);

        list.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        Code(await JsonAsync(list)).ShouldBe("authz.no_organization_membership");
        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        Code(await JsonAsync(create)).ShouldBe("authz.no_organization_membership");
    }

    [Fact]
    public async Task An_approver_only_membership_can_read_projects_but_not_create_them()
    {
        RequireDatabase();
        var (approver, provisioned) = await SignedInAsync(OrganizationRoles.Approver);

        // Seeded directly: this account cannot create one, which is the point.
        await using (var db = fixture.CreateContext())
        {
            db.Projects.Add(DesignProject.Create(
                provisioned.OrganizationId, "To review", ProjectKind.NewProduct, new SystemClock()));
            await db.SaveChangesAsync(Ct);
        }

        var list = await approver.Http.GetAsync(Projects, Ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(list)).GetProperty("projects").GetArrayLength().ShouldBe(1);

        var create = await approver.PostAsync(Projects, new { name = "x", kind = "NewProduct" }, Ct);
        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        Code(await JsonAsync(create)).ShouldBe("authz.member_required");
    }

    [Fact]
    public async Task Revoking_a_membership_removes_access_on_the_next_request()
    {
        RequireDatabase();
        var email = $"{Unique("leaver")}@example.test";
        await ProvisionAsync(email, Unique("org"), OrganizationRoles.Member);
        var client = await SignInAsync(email);
        var id = (await CreateAsync(client, "Before", "NewProduct")).GetProperty("id").GetGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<OperatorProvisioningService>()
                .RevokeAsync(email, Operator, Ct);
        }

        var response = await client.Http.GetAsync($"{Projects}/{id}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        Code(await JsonAsync(response)).ShouldBe("authz.no_organization_membership");
    }

    [Fact]
    public async Task Creating_without_an_antiforgery_token_is_refused_and_creates_nothing()
    {
        RequireDatabase();
        var (client, provisioned) = await SignedInAsync(OrganizationRoles.Member);

        var response = await client.PostWithoutAntiforgeryAsync(
            Projects, new { name = "Forged", kind = "NewProduct" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Code(await JsonAsync(response)).ShouldBe("auth.antiforgery_failed");

        await using var db = fixture.CreateContext();
        (await db.Projects.CountAsync(p => p.OrganizationId == provisioned.OrganizationId, Ct))
            .ShouldBe(0);
    }

    // =======================================================================
    // 4. Validation — every refusal is a coded problem, never a 500
    // =======================================================================

    public static TheoryData<string, string> InvalidBodies => new()
    {
        { """{"kind":"NewProduct"}""", "Name" },
        { """{"name":"","kind":"NewProduct"}""", "Name" },
        { """{"name":"   ","kind":"NewProduct"}""", "Name" },
        { $$"""{"name":"{{new string('a', 201)}}","kind":"NewProduct"}""", "Name" },
        { """{"name":"Acme"}""", "Kind" },
        { """{"name":"Acme","kind":"Website"}""", "Kind" },
        { """{"name":"Acme","kind":"newproduct"}""", "Kind" },
        { """{"name":"Acme","kind":"1"}""", "Kind" },
        { """{"name":"Acme","kind":"0"}""", "Kind" },
    };

    /// <summary>
    /// <c>kind</c> is accepted only by name. A numeric string would otherwise
    /// parse — <c>Enum.TryParse("1")</c> succeeds — and bind the client to the
    /// enum's declaration order, which nothing promises to keep.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task An_invalid_body_is_a_400_naming_the_field(string json, string field)
    {
        RequireDatabase();
        var (client, provisioned) = await SignedInAsync(OrganizationRoles.Member);
        var token = await client.GetAntiforgeryTokenAsync(Ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, Projects)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(StudioClient.AntiforgeryHeader, token);

        var response = await client.Http.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        var problem = await JsonAsync(response);
        Code(problem).ShouldBe("request.invalid");
        problem.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(problem.ToString());

        await using var db = fixture.CreateContext();
        (await db.Projects.CountAsync(p => p.OrganizationId == provisioned.OrganizationId, Ct))
            .ShouldBe(0);
    }

    /// <summary>
    /// Found by this suite (M01-04): a body that is not JSON was a **500** in
    /// Development, on every endpoint. Minimal APIs default
    /// <c>RouteHandlerOptions.ThrowOnBadRequest</c> to true in Development, and
    /// this host has no developer exception page — only the exception handler,
    /// which turned the <c>BadHttpRequestException</c> into an unexplained 500.
    /// Production already answered 400. The login case is here too because the
    /// defect was never specific to projects.
    /// </summary>
    [Theory]
    [InlineData(Projects, true)]
    [InlineData("/api/v1/auth/login", false)]
    public async Task A_malformed_body_is_a_400_problem_not_a_500(string path, bool signedIn)
    {
        RequireDatabase();
        var client = signedIn
            ? (await SignedInAsync(OrganizationRoles.Member)).Client
            : new StudioClient(Factory.CreateBrowserClient());
        var token = await client.GetAntiforgeryTokenAsync(Ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(StudioClient.AntiforgeryHeader, token);

        var response = await client.Http.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await JsonAsync(response)).GetProperty("status").GetInt32().ShouldBe(400);
    }

    // =======================================================================
    // 5. Kind is immutable — no endpoint accepts it after creation
    // =======================================================================

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task No_method_on_a_project_changes_its_kind(string method)
    {
        RequireDatabase();
        var (client, _) = await SignedInAsync(OrganizationRoles.Member | OrganizationRoles.Approver);
        var id = (await CreateAsync(client, "Fixed", "NewProduct")).GetProperty("id").GetGuid();

        var response = await SendAsync(
            client, new HttpMethod(method), $"{Projects}/{id}", new { name = "Fixed", kind = "Redesign" });

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);

        var after = await JsonAsync(await client.Http.GetAsync($"{Projects}/{id}", Ct));
        after.GetProperty("kind").GetString().ShouldBe("NewProduct");
    }

    /// <summary>
    /// The structural half of "no endpoint accepts kind after creation".
    ///
    /// <para>Probing PUT and PATCH proves only those two. This walks every
    /// mapped endpoint under <c>/api/v1/projects</c> that is not a GET and fails
    /// if any of them binds a request type from the API assembly with a
    /// <c>Kind</c> property — except the one place a kind is chosen. A later
    /// endpoint whose body legitimately carries some other kind (an asset kind,
    /// say) must be added to the allow-list with its reason; being unclassified
    /// is itself the failure.</para>
    /// </summary>
    [Fact]
    public async Task Only_project_creation_binds_a_kind()
    {
        RequireDatabase();
        _ = Factory.CreateClient(); // builds the host, so the endpoints exist

        string[] allowed = ["POST /api/v1/projects"];

        var binding = Factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? string.Empty)
                .StartsWith("/api/v1/projects", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                .Where(m => m != "GET")
                // A group prefix plus an empty pattern renders with a trailing
                // slash; normalise so the allow-list names routes as callers do.
                .Select(m => (Route: $"{m} {e.RoutePattern.RawText!.TrimEnd('/')}", Method: e.Metadata.GetMetadata<MethodInfo>())))
            .Where(x => x.Method is not null && x.Method.GetParameters().Any(p =>
                p.ParameterType.Assembly == typeof(Program).Assembly &&
                p.ParameterType.GetProperty("Kind") is not null))
            .Select(x => x.Route)
            .Distinct()
            .ToArray();

        binding.ShouldNotBeEmpty("the guard must at least see the create endpoint, or it is checking nothing");
        binding.Except(allowed).ShouldBeEmpty();
    }
}
