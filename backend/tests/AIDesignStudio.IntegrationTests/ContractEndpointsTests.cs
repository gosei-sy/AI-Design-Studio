using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIDesignStudio.Api.Identity;
using AIDesignStudio.Domain.DesignContracts;
using AIDesignStudio.Domain.Organizations;
using AIDesignStudio.Infrastructure.Organizations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AIDesignStudio.IntegrationTests;

/// <summary>
/// M01-05 — design contract endpoints, against the real host and a real
/// PostgreSQL database.
///
/// <para>Each test states something a caller, a reviewer or an attacker would
/// observe: a status and a code, a row, a hash that does or does not match its
/// payload. The transitions and every rejection named in the task's acceptance
/// criteria are here, plus two things the criteria did not ask for and the
/// claims depend on: that the stored payload is what was validated, and that two
/// requests arriving together cannot leave an approved version that disagrees
/// with its own hash.</para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ContractEndpointsTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private const string Password = "Correct-Horse-9-Battery!";
    private const string Operator = "ops@example.test";

    private readonly ContractSaveBarrier _barrier = new();
    private StudioApiFactory? _factory;

    private StudioApiFactory Factory => _factory
        ?? throw new InvalidOperationException("The test host was not initialised.");

    public ValueTask InitializeAsync()
    {
        if (fixture.IsAvailable)
        {
            _factory = new StudioApiFactory(fixture.ConnectionString, dbInterceptors: [_barrier]);
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
    // Harness
    // -----------------------------------------------------------------------

    /// <summary>A valid contract. <paramref name="name"/> distinguishes one payload from another.</summary>
    private static string Contract(string name = "Acme", string stepScreenKey = "cart") => $$"""
        {
          "schemaVersion": 1,
          "product": { "name": "{{name}}", "platforms": ["web"] },
          "informationArchitecture": {
            "flows": [{ "key": "checkout", "name": "Checkout", "steps": [{ "screenKey": "{{stepScreenKey}}" }] }]
          },
          "screens": [{ "key": "cart", "title": "Cart", "purpose": "Review items." }]
        }
        """;

    private async Task<ProvisionResult> ProvisionAsync(string organization, OrganizationRoles roles, string email)
    {
        using var scope = Factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<OperatorProvisioningService>().ProvisionAsync(
            new ProvisionRequest(email, organization, roles, Operator),
            _ => Task.FromResult(Password),
            Ct);
    }

    private sealed record Actor(StudioClient Client, Guid UserId, Guid OrganizationId, string Organization);

    /// <summary>A provisioned, signed-in account — in a new organization unless one is named.</summary>
    private async Task<Actor> SignedInAsync(OrganizationRoles roles, string? organization = null)
    {
        var email = $"{Unique("user")}@example.test";
        var provisioned = await ProvisionAsync(organization ?? Unique("org"), roles, email);

        var client = new StudioClient(Factory.CreateBrowserClient());
        (await client.LoginAsync(email, Password, null, Ct)).EnsureSuccessStatusCode();

        return new Actor(client, provisioned.UserId, provisioned.OrganizationId, provisioned.OrganizationName);
    }

    private const OrganizationRoles Both = OrganizationRoles.Member | OrganizationRoles.Approver;

    private async Task<Guid> CreateProjectAsync(Actor actor)
    {
        var response = await actor.Client.PostAsync(
            "/api/v1/projects", new { name = "Storefront", kind = "NewProduct" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static string Base(Guid projectId) => $"/api/v1/projects/{projectId}/contract";

    private Task<HttpResponseMessage> OpenDraftAsync(Actor actor, Guid projectId, string json) =>
        actor.Client.SendJsonAsync(HttpMethod.Post, $"{Base(projectId)}/drafts", json, Ct);

    private Task<HttpResponseMessage> EditDraftAsync(Actor actor, Guid projectId, string json) =>
        actor.Client.SendJsonAsync(HttpMethod.Put, $"{Base(projectId)}/drafts/current", json, Ct);

    private Task<HttpResponseMessage> DiscardDraftAsync(Actor actor, Guid projectId) =>
        actor.Client.SendJsonAsync(HttpMethod.Delete, $"{Base(projectId)}/drafts/current", null, Ct);

    private Task<HttpResponseMessage> ApproveAsync(Actor actor, Guid projectId, string? body = null) =>
        actor.Client.SendJsonAsync(HttpMethod.Post, $"{Base(projectId)}/drafts/current/approve", body, Ct);

    private async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> ReadAsync(Actor actor, Guid projectId, string suffix = "")
    {
        var response = await actor.Client.Http.GetAsync($"{Base(projectId)}{suffix}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await JsonAsync(response);
    }

    private async Task ShouldBeProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, body);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("code").GetString().ShouldBe(code);
    }

    private static string ProductName(JsonElement version) =>
        version.GetProperty("payload").GetProperty("product").GetProperty("name").GetString()!;

    /// <summary>What the database holds for a project's contract, read around every filter.</summary>
    private async Task<List<DesignContractVersion>> StoredVersionsAsync(Guid projectId)
    {
        await using var db = fixture.CreateContext();

        var contract = await db.DesignContracts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProjectId == projectId, Ct);

        return contract is null ? [] : [.. contract.Versions.OrderBy(v => v.VersionNumber)];
    }

    /// <summary>
    /// For every non-draft version of the project: is its recorded hash the
    /// SHA-256 of the payload <em>as PostgreSQL holds it</em>? Computed by the
    /// database, so it cannot agree with the application by sharing its code.
    /// </summary>
    private async Task<(int Checked, int Matching)> HashAuditAsync(Guid projectId)
    {
        await using var db = fixture.CreateContext();

        var rows = await db.Database.SqlQuery<bool>($"""
            SELECT encode(sha256(convert_to(v.payload_json::text, 'UTF8')), 'hex') = v.payload_hash AS "Value"
            FROM app.design_contract_versions v
            JOIN app.design_contracts c ON c.id = v.contract_id
            WHERE c.project_id = {projectId} AND v.status <> 'Draft'
            """).ToListAsync(Ct);

        return (rows.Count, rows.Count(matches => matches));
    }

    // =======================================================================
    // 1. Reading
    // =======================================================================

    [Fact]
    public async Task A_project_with_no_contract_yet_reads_as_empty_rather_than_missing()
    {
        RequireDatabase();
        var actor = await SignedInAsync(OrganizationRoles.Member);
        var projectId = await CreateProjectAsync(actor);

        var contract = await ReadAsync(actor, projectId);
        var history = await ReadAsync(actor, projectId, "/versions");

        contract.GetProperty("projectId").GetGuid().ShouldBe(projectId);
        contract.GetProperty("approved").ValueKind.ShouldBe(JsonValueKind.Null);
        contract.GetProperty("draft").ValueKind.ShouldBe(JsonValueKind.Null);
        history.GetProperty("versions").GetArrayLength().ShouldBe(0);
    }

    public static TheoryData<string, string, bool> Routes => new()
    {
        { "GET", "", false },
        { "GET", "/versions", false },
        { "POST", "/drafts", true },
        { "PUT", "/drafts/current", true },
        { "DELETE", "/drafts/current", false },
        { "POST", "/drafts/current/approve", false },
    };

    /// <summary>
    /// Every route, for a caller with both roles in another organization. The
    /// project exists and holds a draft; the answer must be the 404 a made-up id
    /// gets — same status, same code, same detail — and nothing may change.
    /// </summary>
    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Another_organizations_contract_is_indistinguishable_from_a_missing_project(
        string method, string suffix, bool hasBody)
    {
        RequireDatabase();
        var owner = await SignedInAsync(Both);
        var stranger = await SignedInAsync(Both);
        var projectId = await CreateProjectAsync(owner);
        (await OpenDraftAsync(owner, projectId, Contract("Owner's"))).StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = hasBody ? Contract("Stranger's") : null;
        var theirs = await stranger.Client.SendJsonAsync(new HttpMethod(method), $"{Base(projectId)}{suffix}", body, Ct);
        var missing = await stranger.Client.SendJsonAsync(
            new HttpMethod(method), $"{Base(Guid.CreateVersion7())}{suffix}", body, Ct);

        await ShouldBeProblemAsync(theirs, HttpStatusCode.NotFound, "project.not_found");
        await ShouldBeProblemAsync(missing, HttpStatusCode.NotFound, "project.not_found");
        (await JsonAsync(theirs)).GetProperty("detail").GetString()
            .ShouldBe((await JsonAsync(missing)).GetProperty("detail").GetString());

        var stored = await StoredVersionsAsync(projectId);
        stored.ShouldHaveSingleItem().Status.ShouldBe(ContractVersionStatus.Draft);
        stored[0].PayloadJson.ShouldContain("Owner's");
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task An_anonymous_request_is_refused(string method, string suffix, bool hasBody)
    {
        RequireDatabase();
        var anonymous = new StudioClient(Factory.CreateBrowserClient());

        var response = await anonymous.SendJsonAsync(
            new HttpMethod(method), $"{Base(Guid.CreateVersion7())}{suffix}", hasBody ? Contract() : null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>Signed in, provisioned by nobody (ADR-0009): a coded 403 on every route.</summary>
    [Theory]
    [MemberData(nameof(Routes))]
    public async Task An_unprovisioned_account_reaches_no_contract(string method, string suffix, bool hasBody)
    {
        RequireDatabase();
        var nobody = new StudioClient(Factory.CreateBrowserClient());
        await nobody.SignUpAndSignInAsync($"{Unique("nobody")}@example.test", Password, Ct);

        var response = await nobody.SendJsonAsync(
            new HttpMethod(method), $"{Base(Guid.CreateVersion7())}{suffix}", hasBody ? Contract() : null, Ct);

        await ShouldBeProblemAsync(response, HttpStatusCode.Forbidden, "authz.no_organization_membership");
    }

    // =======================================================================
    // 2. The draft
    // =======================================================================

    [Fact]
    public async Task Opening_the_first_draft_returns_201_and_the_draft()
    {
        RequireDatabase();
        var actor = await SignedInAsync(OrganizationRoles.Member);
        var projectId = await CreateProjectAsync(actor);

        var response = await OpenDraftAsync(actor, projectId, Contract("First"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.Location!.OriginalString.ShouldBe($"{Base(projectId)}/drafts/current");

        var draft = await JsonAsync(response);
        draft.GetProperty("versionNumber").GetInt32().ShouldBe(1);
        draft.GetProperty("status").GetString().ShouldBe("Draft");
        draft.GetProperty("payloadHash").ValueKind.ShouldBe(JsonValueKind.Null);
        draft.GetProperty("approvedBy").ValueKind.ShouldBe(JsonValueKind.Null);
        ProductName(draft).ShouldBe("First");

        var read = await ReadAsync(actor, projectId);
        read.GetProperty("draft").GetProperty("id").GetGuid().ShouldBe(draft.GetProperty("id").GetGuid());
        read.GetProperty("approved").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_second_draft_while_one_is_open_is_409_and_the_first_is_untouched()
    {
        RequireDatabase();
        var actor = await SignedInAsync(OrganizationRoles.Member);
        var projectId = await CreateProjectAsync(actor);
        (await OpenDraftAsync(actor, projectId, Contract("First"))).StatusCode.ShouldBe(HttpStatusCode.Created);

        var second = await OpenDraftAsync(actor, projectId, Contract("Second"));

        await ShouldBeProblemAsync(second, HttpStatusCode.Conflict, "contract.draft_already_open");
        (await StoredVersionsAsync(projectId)).ShouldHaveSingleItem().PayloadJson.ShouldContain("First");
    }

    [Fact]
    public async Task Editing_the_draft_replaces_its_payload_and_keeps_its_number()
    {
        RequireDatabase();
        var actor = await SignedInAsync(OrganizationRoles.Member);
        var projectId = await CreateProjectAsync(actor);
        (await OpenDraftAsync(actor, projectId, Contract("Before"))).StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await EditDraftAsync(actor, projectId, Contract("After"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var edited = await JsonAsync(response);
        edited.GetProperty("versionNumber").GetInt32().ShouldBe(1);
        edited.GetProperty("status").GetString().ShouldBe("Draft");

        ProductName((await ReadAsync(actor, projectId)).GetProperty("draft")).ShouldBe("After");
        (await StoredVersionsAsync(projectId)).ShouldHaveSingleItem().PayloadJson.ShouldContain("After");
    }

    /// <summary>
    /// Nothing to edit, discard or approve. Each is the domain's
    /// <c>contract.no_draft</c>, and — the part that is easy to get wrong — none
    /// of them may leave an empty contract row behind.
    /// </summary>
    [Fact]
    public async Task With_no_contract_yet_there_is_no_draft_to_edit_discard_or_approve()
    {
        RequireDatabase();
        var actor = await SignedInAsync(Both);
        var projectId = await CreateProjectAsync(actor);

        await ShouldBeProblemAsync(
            await EditDraftAsync(actor, projectId, Contract()), HttpStatusCode.Conflict, "contract.no_draft");
        await ShouldBeProblemAsync(
            await DiscardDraftAsync(actor, projectId), HttpStatusCode.Conflict, "contract.no_draft");
        await ShouldBeProblemAsync(
            await ApproveAsync(actor, projectId), HttpStatusCode.Conflict, "contract.no_draft");

        await using var db = fixture.CreateContext();
        (await db.DesignContracts.CountAsync(c => c.ProjectId == projectId, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Discarding_the_draft_removes_it_and_frees_its_number()
    {
        RequireDatabase();
        var actor = await SignedInAsync(OrganizationRoles.Member);
        var projectId = await CreateProjectAsync(actor);
        (await OpenDraftAsync(actor, projectId, Contract("Discarded"))).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await DiscardDraftAsync(actor, projectId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ReadAsync(actor, projectId)).GetProperty("draft").ValueKind.ShouldBe(JsonValueKind.Null);
        (await StoredVersionsAsync(projectId)).ShouldBeEmpty();

        await ShouldBeProblemAsync(
            await DiscardDraftAsync(actor, projectId), HttpStatusCode.Conflict, "contract.no_draft");

        var next = await JsonAsync(await OpenDraftAsync(actor, projectId, Contract("Kept")));
        next.GetProperty("versionNumber").GetInt32().ShouldBe(1);
    }

    // =======================================================================
    // 3. Approval
    // =======================================================================

    [Fact]
    public async Task Approving_records_the_approver_the_time_and_a_hash_of_what_is_stored()
    {
        RequireDatabase();
        var actor = await SignedInAsync(Both);
        var projectId = await CreateProjectAsync(actor);
        (await OpenDraftAsync(actor, projectId, Contract())).StatusCode.ShouldBe(HttpStatusCode.Created);
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        var response = await ApproveAsync(actor, projectId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var approved = await JsonAsync(response);
        approved.GetProperty("status").GetString().ShouldBe("Approved");
        approved.GetProperty("versionNumber").GetInt32().ShouldBe(1);

        // The session's immutable user id — not an email address (W2-005).
        approved.GetProperty("approvedBy").GetString().ShouldBe(actor.UserId.ToString());
        approved.GetProperty("approvedAt").GetDateTimeOffset().ShouldBeGreaterThan(before);
        approved.GetProperty("payloadHash").GetString()!.Length.ShouldBe(64);

        var read = await ReadAsync(actor, projectId);
        read.GetProperty("draft").ValueKind.ShouldBe(JsonValueKind.Null);
        read.GetProperty("approved").GetProperty("payloadHash").GetString()
            .ShouldBe(approved.GetProperty("payloadHash").GetString());

        (await HashAuditAsync(projectId)).ShouldBe((1, 1));
    }

    /// <summary>The approver is whoever holds the session. A body claiming otherwise is not read.</summary>
    [Fact]
    public async Task The_approver_is_the_session_whatever_the_request_claims()
    {
        RequireDatabase();
        var actor = await SignedInAsync(Both);
        var projectId = await CreateProjectAsync(actor);
        (await OpenDraftAsync(actor, projectId, Contract())).StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await ApproveAsync(
            actor, projectId, """{"approvedBy":"ceo@example.test","userId":"00000000-0000-0000-0000-000000000001"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StoredVersionsAsync(projectId)).ShouldHaveSingleItem().ApprovedBy.ShouldBe(actor.UserId.ToString());
    }

    [Fact]
    public async Task Approving_a_new_version_supersedes_the_old_one_which_stays_readable()
    {
        RequireDatabase();
        var actor = await SignedInAsync(Both);
        var projectId = await CreateProjectAsync(actor);

        (await OpenDraftAsync(actor, projectId, Contract("One"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        var first = await JsonAsync(await ApproveAsync(actor, projectId));
        (await OpenDraftAsync(actor, projectId, Contract("Two"))).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Between the draft and its approval, version one is still in force.
        var during = await ReadAsync(actor, projectId);
        ProductName(during.GetProperty("approved")).ShouldBe("One");
        ProductName(during.GetProperty("draft")).ShouldBe("Two");

        (await ApproveAsync(actor, projectId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await ReadAsync(actor, projectId);
        ProductName(after.GetProperty("approved")).ShouldBe("Two");
        after.GetProperty("approved").GetProperty("versionNumber").GetInt32().ShouldBe(2);

        var history = (await ReadAsync(actor, projectId, "/versions")).GetProperty("versions");
        history.EnumerateArray().Select(v => v.GetProperty("versionNumber").GetInt32()).ShouldBe([2, 1]);
        history.EnumerateArray().Select(v => v.GetProperty("status").GetString()).ShouldBe(["Approved", "Superseded"]);
        ProductName(history[1]).ShouldBe("One");
        history[1].GetProperty("payloadHash").GetString().ShouldBe(first.GetProperty("payloadHash").GetString());

        (await StoredVersionsAsync(projectId)).Count(v => v.Status == ContractVersionStatus.Approved).ShouldBe(1);
        (await HashAuditAsync(projectId)).ShouldBe((2, 2));
    }

    /// <summary>
    /// AC-2.4 through the API: an approved contract with no draft open cannot be
    /// edited. The refusal is the version's own guard, and what was approved —
    /// payload and hash — is exactly as it was.
    /// </summary>
    [Fact]
    public async Task Editing_an_approved_contract_is_409_version_immutable_and_changes_nothing()
    {
        RequireDatabase();
        var actor = await SignedInAsync(Both);
        var projectId = await CreateProjectAsync(actor);
        (await OpenDraftAsync(actor, projectId, Contract("Approved"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ApproveAsync(actor, projectId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = (await StoredVersionsAsync(projectId)).Single();

        var response = await EditDraftAsync(actor, projectId, Contract("Tampered"));

        await ShouldBeProblemAsync(response, HttpStatusCode.Conflict, "contract.version_immutable");

        var after = (await StoredVersionsAsync(projectId)).ShouldHaveSingleItem();
        after.Status.ShouldBe(ContractVersionStatus.Approved);
        after.PayloadJson.ShouldBe(before.PayloadJson);
        after.PayloadHash.ShouldBe(before.PayloadHash);
        (await HashAuditAsync(projectId)).ShouldBe((1, 1));
    }

    // =======================================================================
    // 4. Roles
    // =======================================================================

    [Fact]
    public async Task A_member_who_is_not_an_approver_can_draft_but_not_approve()
    {
        RequireDatabase();
        var member = await SignedInAsync(OrganizationRoles.Member);
        var projectId = await CreateProjectAsync(member);
        (await OpenDraftAsync(member, projectId, Contract())).StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await ApproveAsync(member, projectId);

        await ShouldBeProblemAsync(response, HttpStatusCode.Forbidden, "authz.approver_required");
        (await StoredVersionsAsync(projectId)).ShouldHaveSingleItem().Status.ShouldBe(ContractVersionStatus.Draft);
    }

    /// <summary>
    /// Assumption A-16: an Approver-only membership reviews and approves; it
    /// does not author. Two accounts in one organization.
    /// </summary>
    [Fact]
    public async Task An_approver_who_is_not_a_member_can_read_and_approve_but_not_author()
    {
        RequireDatabase();
        var author = await SignedInAsync(OrganizationRoles.Member);
        var approver = await SignedInAsync(OrganizationRoles.Approver, author.Organization);
        var projectId = await CreateProjectAsync(author);
        (await OpenDraftAsync(author, projectId, Contract("Authored"))).StatusCode.ShouldBe(HttpStatusCode.Created);

        await ShouldBeProblemAsync(
            await EditDraftAsync(approver, projectId, Contract("Rewritten")),
            HttpStatusCode.Forbidden, "authz.member_required");
        await ShouldBeProblemAsync(
            await DiscardDraftAsync(approver, projectId), HttpStatusCode.Forbidden, "authz.member_required");
        await ShouldBeProblemAsync(
            await OpenDraftAsync(approver, projectId, Contract("Another")),
            HttpStatusCode.Forbidden, "authz.member_required");

        ProductName((await ReadAsync(approver, projectId)).GetProperty("draft")).ShouldBe("Authored");

        var approved = await ApproveAsync(approver, projectId);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        (await StoredVersionsAsync(projectId)).ShouldHaveSingleItem().ApprovedBy.ShouldBe(approver.UserId.ToString());
    }

    public static TheoryData<string, string, bool> WriteRoutes => new()
    {
        { "POST", "/drafts", true },
        { "PUT", "/drafts/current", true },
        { "DELETE", "/drafts/current", false },
        { "POST", "/drafts/current/approve", false },
    };

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task A_write_without_an_antiforgery_token_is_refused_and_changes_nothing(
        string method, string suffix, bool hasBody)
    {
        RequireDatabase();
        var actor = await SignedInAsync(Both);
        var projectId = await CreateProjectAsync(actor);

        // POST /drafts is tried against an empty contract; the others against an open draft.
        if (suffix != "/drafts")
        {
            (await OpenDraftAsync(actor, projectId, Contract("Original"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var before = await StoredVersionsAsync(projectId);

        var response = await actor.Client.SendJsonAsync(
            new HttpMethod(method), $"{Base(projectId)}{suffix}", hasBody ? Contract("Forged") : null, Ct,
            antiforgery: false);

        await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "auth.antiforgery_failed");

        var after = await StoredVersionsAsync(projectId);
        after.Select(v => (v.Status, v.PayloadJson)).ShouldBe(before.Select(v => (v.Status, v.PayloadJson)));
    }

    // =======================================================================
    // 5. What may become a contract
    // =======================================================================

    private static (string Pointer, string Keyword)[] Errors(JsonElement problem) =>
        [.. problem.GetProperty("errors").EnumerateArray()
            .Select(e => (e.GetProperty("pointer").GetString()!, e.GetProperty("keyword").GetString()!))];

    /// <summary>AC-2.6: 400, with a JSON Pointer to each value that failed.</summary>
    [Fact]
    public async Task A_payload_that_breaks_the_schema_is_400_with_the_failing_pointers_and_stores_nothing()
    {
        RequireDatabase();
        var actor = await SignedInAsync(OrganizationRoles.Member);
        var projectId = await CreateProjectAsync(actor);

        var body = Contract()
            .Replace("\"name\": \"Acme\"", "\"name\": \"\"", StringComparison.Ordinal)
            .Replace("\"key\": \"cart\"", "\"key\": \"My Cart\"", StringComparison.Ordinal)
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"owner\": \"me\"", StringComparison.Ordinal);

        var response = await OpenDraftAsync(actor, projectId, body);

        await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "contract.payload_invalid");
        var problem = await JsonAsync(response);
        Errors(problem).ShouldBe(
            [("", "additionalProperties"), ("/product/name", "minLength"), ("/screens/0/key", "pattern")],
            ignoreOrder: true);
        problem.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        (await StoredVersionsAsync(projectId)).ShouldBeEmpty();
    }

    /// <summary>AC-2.7: schema-valid, but a flow step names a screen that is not listed — 422.</summary>
    [Fact]
    public async Task A_flow_step_naming_an_unlisted_screen_is_422_and_stores_nothing()
    {
        RequireDatabase();
        var actor = await SignedInAsync(OrganizationRoles.Member);
        var projectId = await CreateProjectAsync(actor);

        var response = await OpenDraftAsync(actor, projectId, Contract(stepScreenKey: "checkout/payment"));

        await ShouldBeProblemAsync(response, HttpStatusCode.UnprocessableEntity, "contract.unknown_screen_reference");
        Errors(await JsonAsync(response)).ShouldBe(
            [("/informationArchitecture/flows/0/steps/0/screenKey", "screenReference")]);
        (await StoredVersionsAsync(projectId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_invalid_edit_leaves_the_draft_as_it_was()
    {
        RequireDatabase();
        var actor = await SignedInAsync(OrganizationRoles.Member);
        var projectId = await CreateProjectAsync(actor);
        (await OpenDraftAsync(actor, projectId, Contract("Good"))).StatusCode.ShouldBe(HttpStatusCode.Created);

        await ShouldBeProblemAsync(
            await EditDraftAsync(actor, projectId, Contract(stepScreenKey: "nowhere")),
            HttpStatusCode.UnprocessableEntity, "contract.unknown_screen_reference");
        await ShouldBeProblemAsync(
            await EditDraftAsync(actor, projectId, """{"schemaVersion":2}"""),
            HttpStatusCode.BadRequest, "contract.payload_invalid");

        ProductName((await ReadAsync(actor, projectId)).GetProperty("draft")).ShouldBe("Good");
    }

    public static TheoryData<string, string, string> Unstorable => new()
    {
        { "not JSON", "{not json", "json" },
        { "an empty body", "", "json" },
        { "a duplicated property", Contract().TrimEnd().TrimEnd('}') + ", \"screens\": [] }", "duplicateProperty" },
        { "a NUL character", Contract("Ac\\u0000me"), "encoding" },
        { "half a surrogate pair", Contract("\\uD83D"), "encoding" },
        { "a number beyond exact storage", Contract().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1e400", StringComparison.Ordinal), "number" },
    };

    /// <summary>
    /// Bodies the schema alone would let through to the database, where each is
    /// either an error (a 500) or a different value from the one validated. All
    /// are 400 with a reason, and nothing is stored.
    /// </summary>
    [Theory]
    [MemberData(nameof(Unstorable))]
    public async Task A_body_that_cannot_be_stored_as_what_it_says_is_400_not_500(
        string what, string body, string keyword)
    {
        RequireDatabase();
        var actor = await SignedInAsync(OrganizationRoles.Member);
        var projectId = await CreateProjectAsync(actor);

        var response = await OpenDraftAsync(actor, projectId, body);

        await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "contract.payload_invalid");
        Errors(await JsonAsync(response)).Select(e => e.Keyword).ShouldContain(keyword, what);
        (await StoredVersionsAsync(projectId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_body_that_is_not_declared_as_json_is_415()
    {
        RequireDatabase();
        var actor = await SignedInAsync(OrganizationRoles.Member);
        var projectId = await CreateProjectAsync(actor);

        var response = await actor.Client.SendJsonAsync(
            HttpMethod.Post, $"{Base(projectId)}/drafts", Contract(), Ct, contentType: "text/plain");

        await ShouldBeProblemAsync(
            response, HttpStatusCode.UnsupportedMediaType, "request.unsupported_media_type");
        (await StoredVersionsAsync(projectId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_body_over_the_size_limit_is_413()
    {
        RequireDatabase();
        var actor = await SignedInAsync(OrganizationRoles.Member);
        var projectId = await CreateProjectAsync(actor);
        var oversized = Contract(new string('a', Api.Endpoints.ContractEndpoints.MaxPayloadBytes));

        var response = await OpenDraftAsync(actor, projectId, oversized);

        await ShouldBeProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "contract.payload_too_large");
        (await StoredVersionsAsync(projectId)).ShouldBeEmpty();
    }

    /// <summary>
    /// What "stored" means. PostgreSQL keeps the document as <c>jsonb</c>: the
    /// same JSON value comes back, in the database's spelling rather than the
    /// caller's — reordered keys, no whitespace, <c>1.0</c> as <c>1</c>. The
    /// approval hash is therefore of the stored text, and the audit proves it.
    /// </summary>
    [Fact]
    public async Task The_contract_comes_back_as_the_same_value_and_its_hash_is_of_the_stored_text()
    {
        RequireDatabase();
        var actor = await SignedInAsync(Both);
        var projectId = await CreateProjectAsync(actor);

        const string sent = """
            {
                "screens":   [ { "purpose": "Review items.", "title": "Cart", "key": "cart" } ],
                "product":   { "platforms": [ "web" ], "name": "Ünïcödé 😀 \"quoted\"" },
                "informationArchitecture": { "flows": [ { "steps": [ { "screenKey": "cart" } ], "name": "Checkout", "key": "checkout" } ] },
                "schemaVersion": 1.0
            }
            """;

        (await OpenDraftAsync(actor, projectId, sent)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ApproveAsync(actor, projectId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var returned = (await ReadAsync(actor, projectId)).GetProperty("approved").GetProperty("payload");
        var expected = JsonNode.Parse(sent.Replace("1.0", "1", StringComparison.Ordinal));

        JsonNode.DeepEquals(JsonNode.Parse(returned.GetRawText()), expected).ShouldBeTrue(returned.GetRawText());

        var stored = (await StoredVersionsAsync(projectId)).Single();
        stored.PayloadJson.ShouldNotBe(sent, "jsonb does not keep the caller's bytes");
        stored.PayloadHash.ShouldBe(DesignContractVersion.ComputeHash(stored.PayloadJson));
        (await HashAuditAsync(projectId)).ShouldBe((1, 1));
    }

    // =======================================================================
    // 6. Two requests at once
    // =======================================================================

    /// <summary>
    /// An approval and an edit of the same draft, forced to overlap.
    ///
    /// <para>Unserialized, both load the draft, and because EF writes only the
    /// columns that changed, both succeed: the approval writes the status and a
    /// hash of the old payload, the edit writes the new payload. The result is
    /// an <em>approved</em> version whose content was changed after approval and
    /// no longer matches its hash — from two requests that each did something
    /// legal. The barrier holds both at <c>SaveChanges</c> until both have
    /// arrived, which is the interleaving that produces it; with the project
    /// lock the second cannot load until the first has committed, so the
    /// barrier simply times out.</para>
    ///
    /// <para>Either order is a correct outcome. What must hold in both is that
    /// whatever is approved matches its hash.</para>
    /// </summary>
    [Fact]
    public async Task An_edit_racing_an_approval_cannot_leave_an_approved_version_that_disagrees_with_its_hash()
    {
        RequireDatabase();
        var actor = await SignedInAsync(Both);
        var projectId = await CreateProjectAsync(actor);
        (await OpenDraftAsync(actor, projectId, Contract("Before"))).StatusCode.ShouldBe(HttpStatusCode.Created);

        var second = await SignedInAsync(Both, actor.Organization);

        HttpResponseMessage approval, edit;

        using (_barrier.Arm(parties: 2))
        {
            var approving = ApproveAsync(actor, projectId);
            var editing = EditDraftAsync(second, projectId, Contract("After"));
            approval = await approving;
            edit = await editing;
        }

        var stored = (await StoredVersionsAsync(projectId)).ShouldHaveSingleItem();

        approval.StatusCode.ShouldBe(HttpStatusCode.OK, await approval.Content.ReadAsStringAsync(Ct));
        stored.Status.ShouldBe(ContractVersionStatus.Approved);

        if (edit.StatusCode == HttpStatusCode.OK)
        {
            // The edit went first; what was approved is the edited draft.
            stored.PayloadJson.ShouldContain("After");
        }
        else
        {
            // The approval went first; the edit met an approved contract.
            await ShouldBeProblemAsync(edit, HttpStatusCode.Conflict, "contract.version_immutable");
            stored.PayloadJson.ShouldContain("Before");
        }

        stored.PayloadHash.ShouldBe(DesignContractVersion.ComputeHash(stored.PayloadJson));
        (await HashAuditAsync(projectId)).ShouldBe((1, 1));
    }

    /// <summary>
    /// Two first drafts at once. Unserialized, both find no contract and both
    /// insert one; the second hits the one-contract-per-project index and the
    /// caller gets a 500. Serialized, the second finds the first's draft.
    /// </summary>
    [Fact]
    public async Task Two_first_drafts_at_once_produce_one_draft_and_one_conflict()
    {
        RequireDatabase();
        var actor = await SignedInAsync(OrganizationRoles.Member);
        var second = await SignedInAsync(OrganizationRoles.Member, actor.Organization);
        var projectId = await CreateProjectAsync(actor);

        HttpResponseMessage[] responses;

        using (_barrier.Arm(parties: 2))
        {
            responses = await Task.WhenAll(
                OpenDraftAsync(actor, projectId, Contract("One")),
                OpenDraftAsync(second, projectId, Contract("Two")));
        }

        responses.Select(r => r.StatusCode).Order().ShouldBe([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        await ShouldBeProblemAsync(
            responses.Single(r => r.StatusCode == HttpStatusCode.Conflict),
            HttpStatusCode.Conflict, "contract.draft_already_open");

        (await StoredVersionsAsync(projectId)).ShouldHaveSingleItem().VersionNumber.ShouldBe(1);
    }

    // =======================================================================
    // 7. No route forgets its role
    // =======================================================================

    /// <summary>
    /// Every route in the API that changes something must carry an
    /// organization role policy — and the guard partitions <em>all</em> of them,
    /// so a route added later is either covered or listed here with its reason.
    /// A guard that chose its own population would only confirm the routes its
    /// author had thought of (W3-001).
    /// </summary>
    [Fact]
    public async Task Every_route_that_changes_something_requires_an_organization_role()
    {
        RequireDatabase();
        _ = Factory.CreateClient();

        // Changing your own account needs a session, not a place in an
        // organization: an unprovisioned account must still be able to sign in,
        // change its password and enrol a second factor.
        const string accountRoutes = "/api/v1/auth/";

        var policies = Factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var unprotected = new List<string>();
        var examined = 0;

        foreach (var endpoint in Factory.Services.GetServices<EndpointDataSource>()
                     .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            var route = endpoint.RoutePattern.RawText ?? string.Empty;
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];

            if (!route.StartsWith("/api/", StringComparison.Ordinal) ||
                route.StartsWith(accountRoutes, StringComparison.Ordinal) ||
                methods.All(m => m is "GET" or "HEAD" or "OPTIONS"))
            {
                continue;
            }

            examined++;
            OrganizationRoles required = OrganizationRoles.None;

            foreach (var name in endpoint.Metadata.OfType<IAuthorizeData>().Select(d => d.Policy).OfType<string>())
            {
                var policy = await policies.GetPolicyAsync(name);
                required |= policy?.Requirements.OfType<OrganizationRoleRequirement>()
                    .Aggregate(OrganizationRoles.None, (all, r) => all | r.Required) ?? OrganizationRoles.None;
            }

            if (required == OrganizationRoles.None)
            {
                unprotected.Add($"{string.Join('|', methods)} {route}");
            }
            else if (route.EndsWith("/approve", StringComparison.Ordinal) ||
                     route.EndsWith("/reject", StringComparison.Ordinal) ||
                     route.EndsWith("/waive", StringComparison.Ordinal))
            {
                required.HasFlag(OrganizationRoles.Approver).ShouldBeTrue($"{route} decides, so it needs Approver (D-3)");
            }
        }

        examined.ShouldBeGreaterThanOrEqualTo(5, "the project and contract write routes must be in the population");
        unprotected.ShouldBeEmpty();
    }

    /// <summary>
    /// Holds contract saves at the door until the expected number have arrived,
    /// or patience runs out. This makes "two requests both loaded before either
    /// saved" happen on purpose: without serialization both arrive and are
    /// released together; with it, the second never arrives while the first
    /// waits, and the first proceeds alone once patience is spent.
    /// </summary>
    private sealed class ContractSaveBarrier : SaveChangesInterceptor
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

        private readonly Lock _gate = new();
        private TaskCompletionSource? _everyoneArrived;
        private int _expected;
        private int _arrived;

        public IDisposable Arm(int parties)
        {
            lock (_gate)
            {
                _expected = parties;
                _arrived = 0;
                _everyoneArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return new Disarm(this);
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Task? everyone = null;

            lock (_gate)
            {
                if (_everyoneArrived is not null &&
                    eventData.Context is { } context &&
                    context.ChangeTracker.Entries<DesignContract>().Any())
                {
                    if (++_arrived >= _expected)
                    {
                        _everyoneArrived.TrySetResult();
                    }

                    everyone = _everyoneArrived.Task;
                }
            }

            if (everyone is not null)
            {
                await Task.WhenAny(everyone, Task.Delay(Patience, cancellationToken));
            }

            return result;
        }

        private sealed class Disarm(ContractSaveBarrier barrier) : IDisposable
        {
            public void Dispose()
            {
                lock (barrier._gate)
                {
                    barrier._everyoneArrived = null;
                }
            }
        }
    }
}
