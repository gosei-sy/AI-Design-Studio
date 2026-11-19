using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AIDesignStudio.Application.Organizations;
using AIDesignStudio.Domain.Assets;
using AIDesignStudio.Domain.DesignContracts;
using AIDesignStudio.Domain.DesignSystems;
using AIDesignStudio.Domain.Jobs;
using AIDesignStudio.Domain.Organizations;
using AIDesignStudio.Domain.Projects;
using AIDesignStudio.Domain.Redesign;
using AIDesignStudio.Domain.Screens;
using AIDesignStudio.Infrastructure.Identity;
using AIDesignStudio.Infrastructure.Organizations;
using AIDesignStudio.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace AIDesignStudio.IntegrationTests;

/// <summary>
/// M01-03 — organization-scoped authorization, against the real host and a real
/// PostgreSQL database. Decisions D-3 and D-9; see ADR-0009.
///
/// <para>Integration rather than unit tests, for the same reason M01-02's were:
/// every property worth asserting is a property of the composition. Whether a
/// policy is actually attached to an endpoint, whether a global query filter
/// really reaches the generated SQL, whether a filtered unique index refuses the
/// second concurrent membership — a mocked authorization service proves none of
/// it.</para>
///
/// <para><b>What these tests avoid asserting.</b> Not "the policy is
/// registered", not "the requirement object holds the Approver flag". Those are
/// mechanisms. Each test here asks what a user or an attacker would experience:
/// a status code, a visible row, a refused write.</para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AuthorizationTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private const string Password = "Correct-Horse-9-Battery!";
    private const string Operator = "ops@example.test";

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

    private StudioClient NewClient() => new(Factory.CreateBrowserClient());

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}@example.test";

    private static string UniqueOrganization(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}";

    // -----------------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------------

    /// <summary>
    /// Provisions through the **real** <see cref="OperatorProvisioningService"/>,
    /// resolved from the running host. Tests never insert a membership row
    /// directly unless the point of the test is what the database does with a
    /// hand-written row — otherwise they would prove a shape the application
    /// never produces.
    /// </summary>
    private async Task<ProvisionResult> ProvisionAsync(
        string email,
        string organization,
        OrganizationRoles roles,
        StudioApiFactory? host = null,
        string? actor = null)
    {
        using var scope = (host ?? Factory).Services.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<OperatorProvisioningService>();

        return await provisioning.ProvisionAsync(
            new ProvisionRequest(email, organization, roles, actor ?? Operator),
            _ => Task.FromResult(Password),
            Ct);
    }

    private async Task RevokeAsync(string email, string actor)
    {
        using var scope = Factory.Services.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<OperatorProvisioningService>();

        await provisioning.RevokeAsync(email, actor, Ct);
    }

    /// <summary>Signs in an already-provisioned account and returns its client.</summary>
    private async Task<StudioClient> SignInAsync(string email, StudioApiFactory? host = null)
    {
        var client = new StudioClient((host ?? Factory).CreateBrowserClient());

        (await client.LoginAsync(email, Password, null, Ct)).EnsureSuccessStatusCode();

        return client;
    }

    /// <summary>A provisioned, signed-in account, which is the common setup.</summary>
    private async Task<(StudioClient Client, ProvisionResult Provisioned)> SignedInMemberAsync(
        OrganizationRoles roles, string? organization = null)
    {
        var email = UniqueEmail("user");
        var provisioned = await ProvisionAsync(
            email, organization ?? UniqueOrganization("org"), roles);

        return (await SignInAsync(email), provisioned);
    }

    /// <summary>
    /// Buffers the body once and returns a detached element.
    ///
    /// <para>Reading an <c>HttpContent</c> stream twice throws
    /// <c>ObjectDisposedException</c>, and a test that asserts two things about
    /// one response is the normal case here — so the body is read once and the
    /// element is cloned out of the document before it is disposed.</para>
    /// </summary>
    private async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return document.RootElement.Clone();
    }

    private static string[] Roles(JsonElement body) =>
        [.. body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!)];

    // =======================================================================
    // 1. Anonymous requests are rejected
    // =======================================================================

    [Theory]
    [InlineData("/api/v1/organizations/current")]
    [InlineData("/api/v1/organizations/current/members")]
    public async Task An_anonymous_request_is_refused(string path)
    {
        RequireDatabase();

        var response = await NewClient().Http.GetAsync(path, Ct);

        // 401, not 403: the caller has not identified themselves at all. The
        // fallback policy in ADR-0008 is what makes this the default for a new
        // endpoint rather than something each one has to remember.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_anonymous_request_for_a_specific_organization_is_refused()
    {
        RequireDatabase();

        var provisioned = await ProvisionAsync(
            UniqueEmail("owner"), UniqueOrganization("org"), OrganizationRoles.Member);

        var response = await NewClient().Http.GetAsync(
            $"/api/v1/organizations/{provisioned.OrganizationId}", Ct);

        // Note the organization genuinely exists. An anonymous caller still
        // gets 401 and learns nothing about it.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // =======================================================================
    // 2. Authenticated but unprovisioned: no organization access
    // =======================================================================

    /// <summary>
    /// The state every self-registered Development account starts in, and the
    /// heart of D-9: authentication is not authorization.
    /// </summary>
    [Fact]
    public async Task An_authenticated_but_unprovisioned_account_reaches_no_organization()
    {
        RequireDatabase();

        var client = NewClient();
        var email = UniqueEmail("unprovisioned");
        await client.SignUpAndSignInAsync(email, Password, Ct);

        // Signed in: /me works, so this is genuinely an authenticated session
        // rather than a failure to log in.
        (await client.Http.GetAsync("/api/v1/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var current = await client.Http.GetAsync("/api/v1/organizations/current", Ct);
        current.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var problem = await JsonAsync(current);
        problem.GetProperty("code").GetString().ShouldBe("authz.no_organization_membership");

        (await client.Http.GetAsync("/api/v1/organizations/current/members", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // =======================================================================
    // 3. Development self-registration grants nothing
    // =======================================================================

    [Fact]
    public async Task Development_self_registration_creates_no_membership_and_no_roles()
    {
        RequireDatabase();

        var email = UniqueEmail("selfreg");
        var client = NewClient();

        (await client.RegisterAsync(email, Password, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await using var db = fixture.CreateContext();

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, Ct);
        user.ShouldNotBeNull("registration should still create the account");

        // Not one membership row, active or revoked.
        (await db.OrganizationMemberships.AsNoTracking().CountAsync(m => m.UserId == user.Id, Ct))
            .ShouldBe(0);

        // And no Identity role assignment either — the other way a role could
        // have arrived.
        (await db.UserRoles.AsNoTracking().CountAsync(r => r.UserId == user.Id, Ct)).ShouldBe(0);
    }

    // =======================================================================
    // 4. Outside Development, public registration is disabled entirely
    // =======================================================================

    /// <summary>
    /// Outside Development the route is **not mapped**, so registration is gone
    /// rather than merely refused.
    ///
    /// <para>Two assertions, because one alone would be misleading. An anonymous
    /// POST gets <b>401</b>, not 404 — the authorization middleware applies
    /// ADR-0008's fallback policy to requests that match no endpoint at all, so
    /// an unmapped route is closed before the router can report it missing.
    /// That is the fail-closed behaviour the requirement asks for, and it also
    /// means a 404 assertion on an anonymous call would have proven nothing: a
    /// live-but-protected handler would answer identically.</para>
    ///
    /// <para>So the test then signs in a provisioned account, which satisfies the
    /// fallback policy, and asserts <b>404</b>. That is the router's own answer,
    /// and it can only be produced by the route genuinely not existing.</para>
    /// </summary>
    [Fact]
    public async Task Public_registration_is_absent_outside_development()
    {
        RequireDatabase();

        // Transport "None" because a DevelopmentLog transport refuses to start
        // outside Development (W2-RR-001). Production is not tested here because
        // it refuses to start at all until D-8 provides a transport (ADR-0008
        // A2) — proving that would mean weakening the gate, so Production's
        // refusal is covered by AuthenticationTests' startup matrix instead.
        await using var host = new StudioApiFactory(
            fixture.ConnectionString, "Staging", passwordResetTransport: "None");

        var anonymous = new StudioClient(host.CreateBrowserClient());

        // The host is up and serving anonymous endpoints in the same route group,
        // so nothing below is a symptom of a broken host or a mistyped path.
        (await anonymous.Http.GetAsync("/api/v1/auth/antiforgery-token", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await anonymous.RegisterAsync(UniqueEmail("blocked"), Password, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Now with a session, which passes the fallback policy. What is left is
        // the router's verdict.
        var email = UniqueEmail("staging-user");
        await ProvisionAsync(email, UniqueOrganization("org"), OrganizationRoles.Member, host);
        var signedIn = await SignInAsync(email, host);

        (await signedIn.RegisterAsync(UniqueEmail("blocked"), Password, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // And nothing was created on the way to either refusal.
        await using var db = fixture.CreateContext();
        (await db.Users.AsNoTracking().CountAsync(u => u.Email!.StartsWith("blocked-"), Ct)).ShouldBe(0);
    }

    /// <summary>
    /// The counterpart: in Development the same signed-in call is accepted, so
    /// the 404 above is environmental rather than a permanently broken route.
    /// </summary>
    [Fact]
    public async Task Registration_is_mapped_in_development_for_the_same_caller()
    {
        RequireDatabase();

        var email = UniqueEmail("devuser");
        await ProvisionAsync(email, UniqueOrganization("org"), OrganizationRoles.Member);
        var signedIn = await SignInAsync(email);

        (await signedIn.RegisterAsync(UniqueEmail("dev"), Password, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Registration_is_available_in_development()
    {
        RequireDatabase();

        // The counterpart to the test above: the 404 there is environmental, not
        // a permanently broken route.
        (await NewClient().RegisterAsync(UniqueEmail("dev"), Password, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    // =======================================================================
    // 5. Operator provisioning creates exactly the approved organization and roles
    // =======================================================================

    [Fact]
    public async Task Operator_provisioning_creates_exactly_one_organization_and_one_membership()
    {
        RequireDatabase();

        var email = UniqueEmail("provisioned");
        var organizationName = UniqueOrganization("acme");

        var result = await ProvisionAsync(
            email, organizationName, OrganizationRoles.Member | OrganizationRoles.Approver);

        result.UserCreated.ShouldBeTrue();
        result.OrganizationCreated.ShouldBeTrue();

        await using var db = fixture.CreateContext();

        var organizations = await db.Organizations.AsNoTracking()
            .Where(o => o.Name == organizationName).ToListAsync(Ct);
        organizations.Count.ShouldBe(1);

        var memberships = await db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.UserId == result.UserId).ToListAsync(Ct);

        memberships.Count.ShouldBe(1);
        memberships[0].OrganizationId.ShouldBe(organizations[0].Id);
        memberships[0].Roles.ShouldBe(OrganizationRoles.Member | OrganizationRoles.Approver);
        memberships[0].Status.ShouldBe(MembershipStatus.Active);

        // Attribution is recorded, and it names both the operator and the tool.
        memberships[0].ProvisionedBy.ShouldStartWith(Operator);
        memberships[0].ProvisionedBy.ShouldContain("aids-provision");
    }

    [Fact]
    public async Task Re_provisioning_an_account_adjusts_its_roles_without_duplicating_the_membership()
    {
        RequireDatabase();

        var email = UniqueEmail("reprovisioned");
        var organizationName = UniqueOrganization("acme");

        await ProvisionAsync(email, organizationName, OrganizationRoles.Member);
        var second = await ProvisionAsync(email, organizationName, OrganizationRoles.Approver);

        second.UserCreated.ShouldBeFalse();
        second.OrganizationCreated.ShouldBeFalse();

        await using var db = fixture.CreateContext();

        var memberships = await db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.UserId == second.UserId).ToListAsync(Ct);

        memberships.Count.ShouldBe(1);
        memberships[0].Roles.ShouldBe(OrganizationRoles.Approver);
    }

    /// <summary>
    /// One active membership per user is the MVP rule (ADR-0009). Moving an
    /// account between organizations silently would revoke its access to the
    /// first as a side effect of a command that reads like it only adds
    /// something.
    /// </summary>
    [Fact]
    public async Task Provisioning_an_account_into_a_second_organization_is_refused()
    {
        RequireDatabase();

        var email = UniqueEmail("twoorgs");
        await ProvisionAsync(email, UniqueOrganization("first"), OrganizationRoles.Member);

        var failure = await Should.ThrowAsync<ProvisioningException>(() =>
            ProvisionAsync(email, UniqueOrganization("second"), OrganizationRoles.Member));

        failure.Code.ShouldBe("provisioning.user_already_provisioned");
    }

    [Fact]
    public async Task Provisioning_refuses_a_membership_with_no_roles()
    {
        RequireDatabase();

        var failure = await Should.ThrowAsync<ProvisioningException>(() => ProvisionAsync(
            UniqueEmail("noroles"), UniqueOrganization("org"), OrganizationRoles.None));

        failure.Code.ShouldBe("provisioning.invalid_roles");
    }

    [Fact]
    public async Task Provisioning_refuses_an_unattributed_grant()
    {
        RequireDatabase();

        using var scope = Factory.Services.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<OperatorProvisioningService>();

        var failure = await Should.ThrowAsync<ProvisioningException>(() => provisioning.ProvisionAsync(
            new ProvisionRequest(
                UniqueEmail("anon"), UniqueOrganization("org"), OrganizationRoles.Member, "  "),
            _ => Task.FromResult(Password),
            Ct));

        failure.Code.ShouldBe("provisioning.actor_required");
    }

    /// <summary>
    /// A provisioned account can sign in with the password the operator set,
    /// which is the whole delivery path while D-8 is unresolved: no registration
    /// outside Development and no reset email, so the operator sets the first
    /// password and communicates it out of band.
    /// </summary>
    [Fact]
    public async Task A_provisioned_account_can_sign_in_and_see_its_organization()
    {
        RequireDatabase();

        var email = UniqueEmail("signin");
        var organizationName = UniqueOrganization("acme");
        await ProvisionAsync(email, organizationName, OrganizationRoles.Member);

        var client = await SignInAsync(email);
        var response = await client.Http.GetAsync("/api/v1/organizations/current", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await JsonAsync(response);
        body.GetProperty("name").GetString().ShouldBe(organizationName);
        Roles(body).ShouldBe(["Member"]);
    }

    // =======================================================================
    // 6-8. Member vs Approver vs both
    // =======================================================================

    [Fact]
    public async Task A_member_cannot_perform_an_approver_only_action()
    {
        RequireDatabase();

        var (client, _) = await SignedInMemberAsync(OrganizationRoles.Member);

        var response = await client.Http.GetAsync("/api/v1/organizations/current/members", Ct);

        // 403, not 401: identified, and refused. The account is a legitimate
        // member of the organization — it simply does not hold Approver.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_approver_can_perform_an_approver_only_action()
    {
        RequireDatabase();

        var (client, provisioned) = await SignedInMemberAsync(OrganizationRoles.Approver);

        var response = await client.Http.GetAsync("/api/v1/organizations/current/members", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await JsonAsync(response);
        body.GetProperty("organizationId").GetGuid().ShouldBe(provisioned.OrganizationId);
        body.GetProperty("members").EnumerateArray().Count().ShouldBe(1);
    }

    [Fact]
    public async Task An_account_holding_both_roles_can_do_both()
    {
        RequireDatabase();

        var (client, _) = await SignedInMemberAsync(
            OrganizationRoles.Member | OrganizationRoles.Approver);

        var current = await client.Http.GetAsync("/api/v1/organizations/current", Ct);
        current.StatusCode.ShouldBe(HttpStatusCode.OK);
        Roles(await JsonAsync(current)).ShouldBe(["Member", "Approver"]);

        (await client.Http.GetAsync("/api/v1/organizations/current/members", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // =======================================================================
    // 9-10. Cross-organization access is 404
    // =======================================================================

    [Fact]
    public async Task A_cross_organization_read_is_not_found_rather_than_forbidden()
    {
        RequireDatabase();

        var (client, _) = await SignedInMemberAsync(OrganizationRoles.Member);

        var other = await ProvisionAsync(
            UniqueEmail("other"), UniqueOrganization("other"), OrganizationRoles.Member);

        var response = await client.Http.GetAsync($"/api/v1/organizations/{other.OrganizationId}", Ct);

        // 404 and not 403. A 403 would confirm the organization exists, turning
        // the endpoint into a tenant-existence oracle — the same class of leak
        // as the account-enumeration oracles corrected in W2-002.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var problem = await JsonAsync(response);
        problem.GetProperty("code").GetString().ShouldBe("organization.not_found");
    }

    /// <summary>
    /// The response for an organization that exists and one that does not must be
    /// identical, or the difference is the oracle.
    /// </summary>
    [Fact]
    public async Task An_existing_other_organization_and_a_nonexistent_one_look_the_same()
    {
        RequireDatabase();

        var (client, _) = await SignedInMemberAsync(OrganizationRoles.Member);

        var other = await ProvisionAsync(
            UniqueEmail("other"), UniqueOrganization("other"), OrganizationRoles.Member);

        var existing = await client.Http.GetAsync($"/api/v1/organizations/{other.OrganizationId}", Ct);
        var fictional = await client.Http.GetAsync($"/api/v1/organizations/{Guid.CreateVersion7()}", Ct);

        existing.StatusCode.ShouldBe(fictional.StatusCode);

        var existingBody = await ComparableBodyAsync(existing);
        var fictionalBody = await ComparableBodyAsync(fictional);

        existingBody.ShouldBe(fictionalBody);
    }

    [Fact]
    public async Task Reading_your_own_organization_by_id_works()
    {
        RequireDatabase();

        var (client, provisioned) = await SignedInMemberAsync(OrganizationRoles.Member);

        var response = await client.Http.GetAsync(
            $"/api/v1/organizations/{provisioned.OrganizationId}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Memberships are the one organization-bearing table with no global query
    /// filter — they are what produces the filter. That makes the member listing
    /// the single query where tenant scoping is the author's responsibility, so
    /// it gets its own test rather than relying on the model.
    /// </summary>
    [Fact]
    public async Task An_approver_cannot_list_another_organizations_members()
    {
        RequireDatabase();

        var organizationA = UniqueOrganization("a");
        var organizationB = UniqueOrganization("b");

        var (client, provisioned) = await SignedInMemberAsync(OrganizationRoles.Approver, organizationA);

        // Two accounts in the other organization, so a leak would be obvious.
        var otherOne = await ProvisionAsync(UniqueEmail("b1"), organizationB, OrganizationRoles.Member);
        await ProvisionAsync(UniqueEmail("b2"), organizationB, OrganizationRoles.Approver);

        var response = await client.Http.GetAsync("/api/v1/organizations/current/members", Ct);
        var body = await JsonAsync(response);

        var returnedIds = body.GetProperty("members").EnumerateArray()
            .Select(m => m.GetProperty("userId").GetGuid())
            .ToList();

        returnedIds.ShouldBe([provisioned.UserId]);
        returnedIds.ShouldNotContain(otherOne.UserId);
        body.GetProperty("organizationId").GetGuid().ShouldNotBe(otherOne.OrganizationId);
    }

    /// <summary>
    /// **Every** tenant-owned aggregate root, across two real tenants, by list and
    /// by primary key (W3-001).
    ///
    /// <para>The previous version of this test seeded two <i>projects</i> and
    /// nothing else, and the Wave 3 report cited it as evidence for "organization
    /// A cannot read organization B's resources". The independent review was
    /// right that this overstated it: contracts, design systems, screens and
    /// inventories had no filter at all, and no test would have noticed.</para>
    ///
    /// <para>Written as a loop over every root so that adding an aggregate
    /// without adding it here is visible — the count assertion at the end fails
    /// if the model grows and this does not.</para>
    /// </summary>
    [Fact]
    public async Task No_tenant_owned_aggregate_leaks_across_organizations()
    {
        RequireDatabase();

        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        var seededForA = await SeedEveryTenantAggregateAsync(a, "A");
        var seededForB = await SeedEveryTenantAggregateAsync(b, "B");

        await using var asA = fixture.CreateScopedContext(new FixedOrganizationScope(a));

        // --- Lists: A sees exactly its own rows, of every kind ---------------
        (await asA.Projects.AsNoTracking().ToListAsync(Ct))
            .ShouldAllBe(x => x.OrganizationId == a);
        (await asA.DesignContracts.AsNoTracking().ToListAsync(Ct))
            .ShouldAllBe(x => x.OrganizationId == a);
        (await asA.DesignSystems.AsNoTracking().ToListAsync(Ct))
            .ShouldAllBe(x => x.OrganizationId == a);
        (await asA.Screens.AsNoTracking().ToListAsync(Ct))
            .ShouldAllBe(x => x.OrganizationId == a);
        (await asA.FeatureInventories.AsNoTracking().ToListAsync(Ct))
            .ShouldAllBe(x => x.OrganizationId == a);
        (await asA.Assets.AsNoTracking().ToListAsync(Ct))
            .ShouldAllBe(x => x.OrganizationId == a);
        (await asA.Jobs.AsNoTracking().ToListAsync(Ct))
            .ShouldAllBe(x => x.OrganizationId == a);

        // --- By primary key: B's rows do not exist for A ---------------------
        //
        // The important half. A filter that only applied to list queries would
        // be worthless: M01-05's handlers will look rows up by id, which is
        // exactly the path W3-001 said could return another tenant's row.
        (await asA.Projects.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seededForB.ProjectId, Ct)).ShouldBeNull();
        (await asA.DesignContracts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seededForB.ContractId, Ct)).ShouldBeNull();
        (await asA.DesignSystems.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seededForB.DesignSystemId, Ct)).ShouldBeNull();
        (await asA.Screens.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seededForB.ScreenId, Ct)).ShouldBeNull();
        (await asA.FeatureInventories.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seededForB.InventoryId, Ct)).ShouldBeNull();
        (await asA.Assets.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seededForB.AssetId, Ct)).ShouldBeNull();
        (await asA.Jobs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seededForB.JobId, Ct)).ShouldBeNull();

        // --- …and A's own rows are all reachable, so the filter is not simply
        //     hiding everything.
        (await asA.Projects.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seededForA.ProjectId, Ct)).ShouldNotBeNull();
        (await asA.DesignContracts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seededForA.ContractId, Ct)).ShouldNotBeNull();
        (await asA.DesignSystems.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seededForA.DesignSystemId, Ct)).ShouldNotBeNull();
        (await asA.Screens.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seededForA.ScreenId, Ct)).ShouldNotBeNull();
        (await asA.FeatureInventories.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seededForA.InventoryId, Ct)).ShouldNotBeNull();
        (await asA.Assets.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seededForA.AssetId, Ct)).ShouldNotBeNull();
        (await asA.Jobs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seededForA.JobId, Ct)).ShouldNotBeNull();

        // --- Owned collections travel with their root ------------------------
        //
        // A contract's versions are an owned collection, reachable only through
        // the contract. B's contract does not exist for A, so neither do its
        // versions — which is why owned types need no filter of their own.
        var contracts = await asA.DesignContracts.AsNoTracking().ToListAsync(Ct);
        contracts.SelectMany(c => c.Versions).ShouldNotBeEmpty("A's own versions load");
        contracts.ShouldAllBe(c => c.OrganizationId == a);

        // --- Counts, so a future aggregate cannot be quietly left out --------
        var tenantTables = await asA.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value"
              FROM information_schema.columns
             WHERE table_schema = 'app' AND column_name = 'organization_id'
            """).SingleAsync(Ct);

        tenantTables.ShouldBe(
            7,
            "seven app tables carry organization_id; if this changed, the new one needs a query " +
            "filter (OrganizationScopeGuardTests) and a case in this test");
    }

    /// <summary>
    /// A project in the given organization plus one of every aggregate that hangs
    /// off it, so a cross-tenant read has something of each kind to find.
    /// </summary>
    private async Task<SeededTenant> SeedEveryTenantAggregateAsync(Guid organizationId, string label)
    {
        var clock = new SystemClock();

        await using var db = fixture.CreateContext();

        var project = DesignProject.Create(
            organizationId, $"{label}'s project", ProjectKind.Redesign, clock);

        var contract = DesignContract.Create(project.Ownership, clock);
        contract.StartDraft($$"""{"owner":"{{label}}"}""", clock);

        var designSystem = DesignSystem.Create(
            project.Ownership, $"{label}'s system", clock);
        designSystem.StartDraft("""{"color":{"primary":"#abcdef"}}""", clock);

        var screen = Screen.Create(project.Ownership, $"/{label}-home", "Home", clock);

        var inventory = FeatureInventory.Create(project.Ownership, clock);
        inventory.AddItem($"{label}-export", "Export", clock);

        var asset = AssetReference.Create(
            project.Ownership, AssetKind.Screenshot,
            $"{organizationId}/{project.Id}/{label}.png", "image/png", 1024,
            new string('a', 64), $"{label}.png", clock);

        var job = JobRecord.Enqueue(
            organizationId, "screen.generate", """{"screen":"home"}""",
            $"{label}-{Guid.CreateVersion7():N}", clock);

        db.Projects.Add(project);
        db.DesignContracts.Add(contract);
        db.DesignSystems.Add(designSystem);
        db.Screens.Add(screen);
        db.FeatureInventories.Add(inventory);
        db.Assets.Add(asset);
        db.Jobs.Add(job);

        await db.SaveChangesAsync(Ct);

        return new SeededTenant(
            project.Id, contract.Id, designSystem.Id, screen.Id, inventory.Id, asset.Id, job.Id);
    }

    private sealed record SeededTenant(
        Guid ProjectId,
        Guid ContractId,
        Guid DesignSystemId,
        Guid ScreenId,
        Guid InventoryId,
        Guid AssetId,
        Guid JobId);

    /// <summary>
    /// The closed default, over every tenant table rather than just projects: a
    /// context that never resolved a membership sees nothing at all.
    /// </summary>
    [Fact]
    public async Task A_closed_scope_sees_no_tenant_row_of_any_kind()
    {
        RequireDatabase();

        await SeedEveryTenantAggregateAsync(Guid.CreateVersion7(), "closed");

        await using var closed = fixture.CreateScopedContext(ClosedOrganizationScope.Instance);

        (await closed.Projects.CountAsync(Ct)).ShouldBe(0);
        (await closed.DesignContracts.CountAsync(Ct)).ShouldBe(0);
        (await closed.DesignSystems.CountAsync(Ct)).ShouldBe(0);
        (await closed.Screens.CountAsync(Ct)).ShouldBe(0);
        (await closed.FeatureInventories.CountAsync(Ct)).ShouldBe(0);
        (await closed.Assets.CountAsync(Ct)).ShouldBe(0);
        (await closed.Jobs.CountAsync(Ct)).ShouldBe(0);

        // And the same rows are all there under system scope, so the zeroes above
        // are the filter working rather than an empty database.
        await using var system = fixture.CreateContext();
        (await system.DesignContracts.CountAsync(Ct)).ShouldBeGreaterThan(0);
        (await system.Screens.CountAsync(Ct)).ShouldBeGreaterThan(0);
    }

    // =======================================================================
    // 11. Client-supplied organization and role values change nothing
    // =======================================================================

    /// <summary>
    /// Every channel a browser controls, tried against the same session. The
    /// endpoint never reads any of them, so the expected result is "no
    /// difference at all" — including no error, because rejecting them would
    /// imply they were read.
    /// </summary>
    [Fact]
    public async Task A_client_supplied_organization_is_ignored()
    {
        RequireDatabase();

        var (client, provisioned) = await SignedInMemberAsync(OrganizationRoles.Member);

        var victim = await ProvisionAsync(
            UniqueEmail("victim"), UniqueOrganization("victim"), OrganizationRoles.Approver);

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/organizations/current?organizationId={victim.OrganizationId}" +
            $"&organization={victim.OrganizationName}&roles=Approver");

        request.Headers.Add("X-Organization-Id", victim.OrganizationId.ToString());
        request.Headers.Add("X-Organization", victim.OrganizationName);
        request.Headers.Add("X-Roles", "Member,Approver");

        var response = await client.Http.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await JsonAsync(response);
        body.GetProperty("organizationId").GetGuid().ShouldBe(provisioned.OrganizationId);
        body.GetProperty("organizationId").GetGuid().ShouldNotBe(victim.OrganizationId);
        Roles(body).ShouldBe(["Member"]);
    }

    [Fact]
    public async Task A_client_supplied_role_cannot_reach_an_approver_only_action()
    {
        RequireDatabase();

        var (client, _) = await SignedInMemberAsync(OrganizationRoles.Member);

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/v1/organizations/current/members?roles=Approver&role=Approver");

        request.Headers.Add("X-Roles", "Approver");
        request.Headers.Add("X-Organization-Role", "Approver");

        (await client.Http.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The sharpest version of "no role claim is trusted", because this one is
    /// not forged by a client at all — it is a **real** ASP.NET Core Identity
    /// role, assigned in the database, named exactly "Approver".
    ///
    /// <para>Identity roles are global. If the authorization path ever read a
    /// role claim — <c>RequireRole("Approver")</c> is one method call away — this
    /// account would become an approver of every organization in the system. The
    /// test fails the moment that happens.</para>
    /// </summary>
    [Fact]
    public async Task A_global_identity_role_named_Approver_grants_nothing()
    {
        RequireDatabase();

        var email = UniqueEmail("globalrole");
        await ProvisionAsync(email, UniqueOrganization("org"), OrganizationRoles.Member);

        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

            if (!await roles.RoleExistsAsync("Approver"))
            {
                (await roles.CreateAsync(new ApplicationRole { Name = "Approver" })).Succeeded
                    .ShouldBeTrue();
            }

            var user = await users.FindByEmailAsync(email);
            (await users.AddToRoleAsync(user!, "Approver")).Succeeded.ShouldBeTrue();
            (await users.IsInRoleAsync(user!, "Approver")).ShouldBeTrue("the global role really is assigned");
        }

        var client = await SignInAsync(email);

        (await client.Http.GetAsync("/api/v1/organizations/current/members", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // And the organization view still reports only the membership's roles.
        var current = await client.Http.GetAsync("/api/v1/organizations/current", Ct);
        Roles(await JsonAsync(current)).ShouldBe(["Member"]);
    }

    // =======================================================================
    // 12. Role and membership changes take effect immediately
    // =======================================================================

    [Fact]
    public async Task Granting_the_approver_role_takes_effect_on_the_next_request()
    {
        RequireDatabase();

        var email = UniqueEmail("promoted");
        var organizationName = UniqueOrganization("org");
        await ProvisionAsync(email, organizationName, OrganizationRoles.Member);

        var client = await SignInAsync(email);

        (await client.Http.GetAsync("/api/v1/organizations/current/members", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await ProvisionAsync(email, organizationName, OrganizationRoles.Member | OrganizationRoles.Approver);

        // The same session — no sign-out, no new cookie, no cache to invalidate.
        // Authorization is read per request, so the change is live.
        (await client.Http.GetAsync("/api/v1/organizations/current/members", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoking_a_membership_removes_access_on_the_next_request()
    {
        RequireDatabase();

        var email = UniqueEmail("revoked");
        await ProvisionAsync(email, UniqueOrganization("org"), OrganizationRoles.Approver);

        var client = await SignInAsync(email);
        (await client.Http.GetAsync("/api/v1/organizations/current", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        using (var scope = Factory.Services.CreateScope())
        {
            var provisioning = scope.ServiceProvider.GetRequiredService<OperatorProvisioningService>();
            await provisioning.RevokeAsync(email, "security@example.test", Ct);
        }

        (await client.Http.GetAsync("/api/v1/organizations/current", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await client.Http.GetAsync("/api/v1/organizations/current/members", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The *session* is deliberately untouched: revoking organization access
        // is not a credential event, and signing the user out everywhere would
        // conflate the two. Authentication still works; authorization does not.
        (await client.Http.GetAsync("/api/v1/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoking_a_membership_records_the_revocation()
    {
        RequireDatabase();

        var email = UniqueEmail("audited");
        var provisioned = await ProvisionAsync(
            email, UniqueOrganization("org"), OrganizationRoles.Member);

        using (var scope = Factory.Services.CreateScope())
        {
            var provisioning = scope.ServiceProvider.GetRequiredService<OperatorProvisioningService>();
            await provisioning.RevokeAsync(email, "security@example.test", Ct);
        }

        await using var db = fixture.CreateContext();

        var membership = await db.OrganizationMemberships.AsNoTracking()
            .FirstAsync(m => m.UserId == provisioned.UserId, Ct);

        membership.Status.ShouldBe(MembershipStatus.Revoked);
        membership.RevokedBy.ShouldStartWith("security@example.test");
        membership.RevokedAt.ShouldNotBeNull();

        // The row survives rather than being deleted: it is the record of who
        // had access and who took it away.
        membership.ProvisionedBy.ShouldStartWith(Operator);
    }

    [Fact]
    public async Task A_revoked_account_can_be_provisioned_again()
    {
        RequireDatabase();

        var email = UniqueEmail("rehired");
        await ProvisionAsync(email, UniqueOrganization("first"), OrganizationRoles.Member);

        using (var scope = Factory.Services.CreateScope())
        {
            var provisioning = scope.ServiceProvider.GetRequiredService<OperatorProvisioningService>();
            await provisioning.RevokeAsync(email, "security@example.test", Ct);
        }

        // A different organization this time, which the single-active rule only
        // permits because the first membership is revoked.
        var second = await ProvisionAsync(email, UniqueOrganization("second"), OrganizationRoles.Approver);

        await using var db = fixture.CreateContext();

        var memberships = await db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.UserId == second.UserId).ToListAsync(Ct);

        memberships.Count.ShouldBe(2);
        memberships.Count(m => m.Status == MembershipStatus.Active).ShouldBe(1);
    }

    // =======================================================================
    // 13. The database refuses duplicate and invalid memberships
    // =======================================================================

    /// <summary>
    /// Written as raw SQL on purpose. The aggregate already refuses these, and
    /// that is tested in <c>OrganizationMembershipTests</c> — what this asserts
    /// is that the guarantee survives a writer that never loaded the aggregate: a
    /// migration, a psql session, or a future repository method.
    /// </summary>
    [Fact]
    public async Task The_database_refuses_a_second_active_membership_for_one_user()
    {
        RequireDatabase();

        var provisioned = await ProvisionAsync(
            UniqueEmail("dupe"), UniqueOrganization("org"), OrganizationRoles.Member);

        var failure = await Should.ThrowAsync<PostgresException>(() => InsertMembershipAsync(
            provisioned.OrganizationId, provisioned.UserId, roles: 1, status: "Active"));

        failure.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        failure.ConstraintName.ShouldBe("ux_organization_memberships_active_user");
    }

    [Fact]
    public async Task The_database_allows_many_revoked_memberships_for_one_user()
    {
        RequireDatabase();

        var provisioned = await ProvisionAsync(
            UniqueEmail("history"), UniqueOrganization("org"), OrganizationRoles.Member);

        // The filtered index constrains active rows only — history is unbounded.
        // The audit fields are supplied because ck_organization_memberships_revoked_audit
        // requires them, which is itself asserted below.
        await InsertMembershipAsync(
            provisioned.OrganizationId, provisioned.UserId, roles: 1, status: "Revoked",
            revokedAt: DateTimeOffset.UtcNow, revokedBy: "history-test");
        await InsertMembershipAsync(
            provisioned.OrganizationId, provisioned.UserId, roles: 3, status: "Revoked",
            revokedAt: DateTimeOffset.UtcNow, revokedBy: "history-test");

        await using var db = fixture.CreateContext();
        (await db.OrganizationMemberships.AsNoTracking()
            .CountAsync(m => m.UserId == provisioned.UserId, Ct)).ShouldBe(3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(999)]
    public async Task The_database_refuses_an_invalid_role_set(int roles)
    {
        RequireDatabase();

        // A freshly created account with no membership, so the foreign key and
        // the single-active index are both satisfied and the role check is the
        // only constraint this row can violate.
        var (organizationId, userId) = await AnOrganizationAndABareUserAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() =>
            InsertMembershipAsync(organizationId, userId, roles, "Active"));

        failure.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        failure.ConstraintName.ShouldBe("ck_organization_memberships_roles");
    }

    [Fact]
    public async Task The_database_refuses_an_unknown_membership_status()
    {
        RequireDatabase();

        var (organizationId, userId) = await AnOrganizationAndABareUserAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() =>
            InsertMembershipAsync(organizationId, userId, roles: 1, status: "Suspended"));

        failure.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        failure.ConstraintName.ShouldBe("ck_organization_memberships_status");
    }

    /// <summary>
    /// A revoked row without its audit fields, and an active row that claims
    /// them, are both refused. Otherwise "revoked by nobody at no time" would be
    /// a writable state and the revocation trail would be optional.
    /// </summary>
    [Fact]
    public async Task The_database_refuses_a_revocation_with_no_audit_trail()
    {
        RequireDatabase();

        var (organizationId, userId) = await AnOrganizationAndABareUserAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() =>
            InsertMembershipAsync(organizationId, userId, roles: 1, status: "Revoked"));

        failure.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        failure.ConstraintName.ShouldBe("ck_organization_memberships_revoked_audit");
    }

    [Fact]
    public async Task The_database_refuses_an_active_membership_that_claims_to_be_revoked()
    {
        RequireDatabase();

        var (organizationId, userId) = await AnOrganizationAndABareUserAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => InsertMembershipAsync(
            organizationId, userId, roles: 1, status: "Active",
            revokedAt: DateTimeOffset.UtcNow, revokedBy: "nobody"));

        failure.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        failure.ConstraintName.ShouldBe("ck_organization_memberships_active_audit");
    }

    [Fact]
    public async Task The_database_refuses_a_membership_for_a_user_that_does_not_exist()
    {
        RequireDatabase();

        var (organizationId, _) = await AnOrganizationAndABareUserAsync();

        // The foreign key is what makes "membership belongs to an authenticated
        // user id" a guarantee rather than a convention. Without it a row written
        // for an unused id would grant organization access to whoever was later
        // issued that id.
        var failure = await Should.ThrowAsync<PostgresException>(() => InsertMembershipAsync(
            organizationId, Guid.CreateVersion7(), roles: 1, status: "Active"));

        failure.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        failure.ConstraintName.ShouldBe("fk_organization_memberships_user");
    }

    [Fact]
    public async Task The_database_refuses_a_membership_of_an_organization_that_does_not_exist()
    {
        RequireDatabase();

        var (_, userId) = await AnOrganizationAndABareUserAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => InsertMembershipAsync(
            Guid.CreateVersion7(), userId, roles: 1, status: "Active"));

        failure.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        failure.ConstraintName.ShouldBe("fk_organization_memberships_organization");
    }

    [Fact]
    public async Task The_database_refuses_two_organizations_with_the_same_name()
    {
        RequireDatabase();

        var name = UniqueOrganization("collide");
        await ProvisionAsync(UniqueEmail("first"), name, OrganizationRoles.Member);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO auth.organizations (id, name, created_at)
            VALUES (@id, @name, now())
            """,
            connection);

        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("name", name);

        var failure = await Should.ThrowAsync<PostgresException>(() =>
            command.ExecuteNonQueryAsync(Ct));

        failure.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        failure.ConstraintName.ShouldBe("ux_organizations_name");
    }

    /// <summary>
    /// An organization and an account that has no membership at all, so a
    /// hand-written row for it can violate exactly one constraint at a time.
    /// </summary>
    private async Task<(Guid OrganizationId, Guid UserId)> AnOrganizationAndABareUserAsync()
    {
        var provisioned = await ProvisionAsync(
            UniqueEmail("holder"), UniqueOrganization("org"), OrganizationRoles.Member);

        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var bare = new ApplicationUser(UniqueEmail("bare"));
        (await users.CreateAsync(bare, Password)).Succeeded.ShouldBeTrue();

        return (provisioned.OrganizationId, bare.Id);
    }

    /// <summary>
    /// Inserts a membership row with raw SQL, bypassing the aggregate entirely.
    /// The aggregate's own refusals are covered in
    /// <c>OrganizationMembershipTests</c>; what these rows prove is that the
    /// guarantee survives a writer that never loaded it — a migration, a psql
    /// session, or a future repository method.
    /// </summary>
    private async Task InsertMembershipAsync(
        Guid organizationId,
        Guid userId,
        int roles,
        string status,
        DateTimeOffset? revokedAt = null,
        string? revokedBy = null)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO auth.organization_memberships
                (id, organization_id, user_id, roles, status, provisioned_by, revoked_at, revoked_by,
                 created_at)
            VALUES (@id, @organization_id, @user_id, @roles, @status, 'raw-sql-test', @revoked_at,
                    @revoked_by, now())
            """,
            connection);

        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("roles", roles);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue(
            "revoked_at", revokedAt.HasValue ? revokedAt.Value : DBNull.Value);
        command.Parameters.AddWithValue("revoked_by", revokedBy ?? (object)DBNull.Value);

        await command.ExecuteNonQueryAsync(Ct);
    }

    // =======================================================================
    // 13a. Every privileged change is durably attributable (W3-002)
    // =======================================================================

    /// <summary>
    /// Three different operators, three changes, one persisted chain.
    ///
    /// <para>W3-002: operator A granted Member, operator B later granted
    /// **Approver**, and the stored membership still named A. A log line named B,
    /// but a log is not an audit record — nobody designed its retention,
    /// integrity or correlation, and the authoritative row said something
    /// false.</para>
    ///
    /// <para>Read back from PostgreSQL rather than from the aggregate in memory,
    /// because the defect was in what was *persisted*.</para>
    /// </summary>
    [Fact]
    public async Task The_persisted_audit_chain_names_the_operator_behind_every_change()
    {
        RequireDatabase();

        var email = UniqueEmail("audited-chain");
        var organizationName = UniqueOrganization("org");

        var first = await ProvisionAsync(
            email, organizationName, OrganizationRoles.Member, actor: "alice@ops.test");

        await ProvisionAsync(
            email, organizationName, OrganizationRoles.Member | OrganizationRoles.Approver,
            actor: "bob@ops.test");

        await RevokeAsync(email, "carol@security.test");

        await using var db = fixture.CreateContext();

        var membership = await db.OrganizationMemberships
            .Include(m => m.Events)
            .AsNoTracking()
            .SingleAsync(m => m.UserId == first.UserId, Ct);

        // The row distinguishes all three actors without overwriting any of them.
        membership.ProvisionedBy.ShouldStartWith("alice@ops.test");
        membership.RolesChangedBy.ShouldStartWith("bob@ops.test");
        membership.RevokedBy.ShouldStartWith("carol@security.test");
        membership.RolesChangedAt.ShouldNotBeNull();

        // And the chain records the whole sequence, with the role transition.
        var events = membership.Events.OrderBy(e => e.OccurredAt).ToList();
        events.Count.ShouldBe(3);

        events[0].Type.ShouldBe(MembershipEventType.Provisioned);
        events[0].Actor.ShouldStartWith("alice@ops.test");
        events[0].NewRoles.ShouldBe(OrganizationRoles.Member);

        events[1].Type.ShouldBe(MembershipEventType.RolesChanged);
        events[1].Actor.ShouldStartWith("bob@ops.test");
        events[1].PreviousRoles.ShouldBe(OrganizationRoles.Member);
        events[1].NewRoles.ShouldBe(OrganizationRoles.Member | OrganizationRoles.Approver);

        events[2].Type.ShouldBe(MembershipEventType.Revoked);
        events[2].Actor.ShouldStartWith("carol@security.test");

        // Self-contained: an auditor querying this table alone can answer who
        // changed whose access, in which tenant.
        events.ShouldAllBe(e => e.OrganizationId == first.OrganizationId);
        events.ShouldAllBe(e => e.UserId == first.UserId);
        events.ShouldAllBe(e => e.MembershipId == membership.Id);
    }

    /// <summary>
    /// The roster the API returns must not misattribute the promotion either —
    /// that was the externally visible half of W3-002.
    /// </summary>
    [Fact]
    public async Task The_member_roster_reports_who_last_changed_the_roles()
    {
        RequireDatabase();

        var email = UniqueEmail("promoted-roster");
        var organizationName = UniqueOrganization("org");

        await ProvisionAsync(
            email, organizationName, OrganizationRoles.Member, actor: "alice@ops.test");
        await ProvisionAsync(
            email, organizationName, OrganizationRoles.Member | OrganizationRoles.Approver,
            actor: "bob@ops.test");

        var client = await SignInAsync(email);
        var body = await JsonAsync(await client.Http.GetAsync(
            "/api/v1/organizations/current/members", Ct));

        var member = body.GetProperty("members").EnumerateArray().Single();

        member.GetProperty("provisionedBy").GetString().ShouldStartWith("alice@ops.test");
        member.GetProperty("rolesChangedBy").GetString().ShouldStartWith("bob@ops.test");
        member.GetProperty("rolesChangedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_membership_that_was_never_changed_reports_no_role_change()
    {
        RequireDatabase();

        var provisioned = await ProvisionAsync(
            UniqueEmail("unchanged"), UniqueOrganization("org"), OrganizationRoles.Approver);

        await using var db = fixture.CreateContext();

        var membership = await db.OrganizationMemberships
            .Include(m => m.Events)
            .AsNoTracking()
            .SingleAsync(m => m.UserId == provisioned.UserId, Ct);

        // Null rather than "the provisioner changed nothing": the two questions
        // are different, and conflating them is what W3-002 was.
        membership.RolesChangedBy.ShouldBeNull();
        membership.RolesChangedAt.ShouldBeNull();
        membership.Events.Count.ShouldBe(1);
        membership.Events[0].Type.ShouldBe(MembershipEventType.Provisioned);
    }

    [Fact]
    public async Task The_database_refuses_a_role_change_with_half_an_audit_record()
    {
        RequireDatabase();

        var (organizationId, userId) = await AnOrganizationAndABareUserAsync();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO auth.organization_memberships
                (id, organization_id, user_id, roles, status, provisioned_by, roles_changed_at,
                 created_at)
            VALUES (@id, @organization_id, @user_id, 3, 'Active', 'raw-sql-test', now(), now())
            """,
            connection);

        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.AddWithValue("user_id", userId);

        var failure = await Should.ThrowAsync<PostgresException>(() =>
            command.ExecuteNonQueryAsync(Ct));

        failure.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        failure.ConstraintName.ShouldBe("ck_organization_memberships_role_change_audit");
    }

    // =======================================================================
    // 13b. The tenant key cannot disagree with the project (W3-RR-001)
    // =======================================================================

    /// <summary>
    /// Every project-owned table refuses one organization's tenant key on
    /// another organization's project.
    ///
    /// <para>The re-review ran this insert and PostgreSQL answered
    /// <c>INSERT 0 1</c>. The global filter then served that row as
    /// organization A's data while the project relationship said B. Written as
    /// raw SQL for the same reason the membership-constraint tests are: the
    /// aggregate now takes a single <c>ProjectOwnership</c> and cannot express
    /// the mismatch, so the only way to attempt it is to bypass the aggregate —
    /// which is exactly what a migration, a psql session or a future repository
    /// method could do.</para>
    /// </summary>
    [Theory]
    [InlineData("design_contracts", "fk_design_contracts_project_tenant")]
    [InlineData("design_systems", "fk_design_systems_project_tenant")]
    [InlineData("screens", "fk_screens_project_tenant")]
    [InlineData("feature_inventories", "fk_feature_inventories_project_tenant")]
    [InlineData("assets", "fk_assets_project_tenant")]
    public async Task The_database_refuses_one_tenants_key_on_another_tenants_project(
        string table, string constraint)
    {
        RequireDatabase();

        var (organizationA, _) = await AnOrganizationAndABareUserAsync();
        var projectOfB = await ASeededProjectAsync(Guid.CreateVersion7());

        var failure = await Should.ThrowAsync<PostgresException>(() =>
            InsertProjectOwnedRowAsync(table, organizationA, projectOfB));

        failure.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        failure.ConstraintName.ShouldBe(constraint);
    }

    /// <summary>
    /// The matching pair still inserts. Without this, a constraint that rejected
    /// everything would pass the test above.
    /// </summary>
    [Theory]
    [InlineData("design_contracts")]
    [InlineData("design_systems")]
    [InlineData("screens")]
    [InlineData("feature_inventories")]
    [InlineData("assets")]
    public async Task The_database_accepts_a_matching_tenant_and_project(string table)
    {
        RequireDatabase();

        var organizationId = Guid.CreateVersion7();
        var projectId = await ASeededProjectAsync(organizationId);

        await InsertProjectOwnedRowAsync(table, organizationId, projectId);

        await using var db = fixture.CreateContext();
        (await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM app.projects WHERE id = {projectId}")
            .SingleAsync(Ct)).ShouldBe(1);
    }

    /// <summary>
    /// The aggregate cannot express the mismatch at all, because a project's
    /// ownership arrives as one value (W3-RR-001, required correction 2).
    /// </summary>
    [Fact]
    public async Task A_child_takes_its_tenant_from_the_project_it_is_created_from()
    {
        RequireDatabase();

        var organizationId = Guid.CreateVersion7();
        var clock = new SystemClock();

        await using var db = fixture.CreateContext();

        var project = DesignProject.Create(organizationId, "Owned", ProjectKind.NewProduct, clock);
        var contract = DesignContract.Create(project.Ownership, clock);

        contract.OrganizationId.ShouldBe(project.OrganizationId);
        contract.ProjectId.ShouldBe(project.Id);

        db.Projects.Add(project);
        db.DesignContracts.Add(contract);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> ASeededProjectAsync(Guid organizationId)
    {
        await using var db = fixture.CreateContext();

        var project = DesignProject.Create(
            organizationId, "Tenant probe", ProjectKind.NewProduct, new SystemClock());

        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);

        return project.Id;
    }

    /// <summary>
    /// Inserts a minimal row into one project-owned table with raw SQL, so the
    /// aggregate's single-value ownership cannot prevent the attempt.
    /// </summary>
    private async Task InsertProjectOwnedRowAsync(string table, Guid organizationId, Guid projectId)
    {
        var sql = table switch
        {
            "design_contracts" =>
                "INSERT INTO app.design_contracts (id, project_id, organization_id, created_at) " +
                "VALUES (@id, @project_id, @organization_id, now())",
            "design_systems" =>
                "INSERT INTO app.design_systems (id, project_id, organization_id, name, created_at) " +
                "VALUES (@id, @project_id, @organization_id, 'probe', now())",
            "screens" =>
                "INSERT INTO app.screens (id, project_id, organization_id, key, title, created_at) " +
                "VALUES (@id, @project_id, @organization_id, @id::text, 'probe', now())",
            "feature_inventories" =>
                "INSERT INTO app.feature_inventories (id, project_id, organization_id, created_at) " +
                "VALUES (@id, @project_id, @organization_id, now())",
            // Every NOT NULL column is supplied deliberately. A missing one
            // raises 23502 before the foreign key is ever checked, which made
            // this probe pass for the wrong reason on its first run.
            "assets" =>
                "INSERT INTO app.assets (id, project_id, organization_id, kind, storage_key, " +
                "content_type, size_bytes, content_hash, original_file_name, created_at) " +
                "VALUES (@id, @project_id, @organization_id, 'Screenshot', @id::text, " +
                "'image/png', 1, 'hash', 'probe.png', now())",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "unknown table"),
        };

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("project_id", projectId);
        command.Parameters.AddWithValue("organization_id", organizationId);

        await command.ExecuteNonQueryAsync(Ct);
    }

    // =======================================================================
    // 14. Host-level guards
    // =======================================================================

    /// <summary>
    /// The API host must never hold a system-scoped context. If it did, every
    /// query filter in the model would pass trivially and the tenant boundary
    /// would be gone — with no test failing, because every individual endpoint
    /// would still "work".
    /// </summary>
    [Fact]
    public void The_api_host_is_never_system_scoped()
    {
        RequireDatabase();

        using var scope = Factory.Services.CreateScope();

        var organizationScope = scope.ServiceProvider.GetRequiredService<IOrganizationScope>();

        organizationScope.IsSystem.ShouldBeFalse();
        organizationScope.ShouldBeOfType<OrganizationScope>();

        // And it starts closed: nothing is in scope until a membership resolves.
        organizationScope.OrganizationId.ShouldBeNull();
    }

    /// <summary>
    /// No HTTP route may grant a membership or a role. ADR-0009 puts provisioning
    /// behind shell access on purpose; an admin endpoint added later "just for
    /// setup" would quietly replace that control, and this enumerates the real
    /// route table rather than trusting a reading of the source.
    /// </summary>
    [Fact]
    public void No_http_endpoint_can_grant_a_membership()
    {
        RequireDatabase();

        var dataSources = Factory.Services
            .GetRequiredService<IEnumerable<Microsoft.AspNetCore.Routing.EndpointDataSource>>();

        var routes = dataSources
            .SelectMany(source => source.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToList();

        routes.ShouldNotBeEmpty("the route table should have been discovered");

        string[] forbidden =
        [
            "membership", "memberships", "provision", "bootstrap", "setup", "admin",
            "grant", "role", "roles", "invite",
        ];

        var offenders = routes
            .Where(route => forbidden.Any(word =>
                route.Contains(word, StringComparison.OrdinalIgnoreCase)))
            // The approver-only read of the caller's own organization roster is
            // not a grant: it creates nothing and is listed explicitly so that
            // adding anything else here is a deliberate act.
            .Where(route => route != "/api/v1/organizations/current/members")
            .ToList();

        offenders.ShouldBeEmpty(
            $"no HTTP route may create or change a membership. Found: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// A forward-looking guard, and **vacuously true today**: no approve, reject
    /// or waive endpoint exists yet — those are M01-05. It is here so that the
    /// first one added without the Approver policy fails the build rather than
    /// shipping. Stated plainly because a vacuous test reported as evidence is
    /// worse than no test.
    /// </summary>
    [Fact]
    public void Any_approval_endpoint_must_carry_the_approver_policy()
    {
        RequireDatabase();

        var dataSources = Factory.Services
            .GetRequiredService<IEnumerable<Microsoft.AspNetCore.Routing.EndpointDataSource>>();

        var approvalEndpoints = dataSources
            .SelectMany(source => source.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(endpoint => new[] { "approve", "reject", "waive" }.Any(verb =>
                (endpoint.RoutePattern.RawText ?? string.Empty)
                    .Contains(verb, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var endpoint in approvalEndpoints)
        {
            var policies = endpoint.Metadata
                .OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                .Select(data => data.Policy)
                .ToList();

            policies.ShouldContain(
                AIDesignStudio.Api.Identity.OrganizationPolicies.Approver,
                $"{endpoint.RoutePattern.RawText} performs an approval action and must require the " +
                "Approver policy (D-3).");
        }
    }

    /// <summary>
    /// Existing guards must not have been loosened to make organization scoping
    /// fit. Named explicitly because "do not weaken existing guards" is a
    /// requirement of this task, and a requirement with no test is a hope.
    /// </summary>
    [Fact]
    public async Task Organization_scoping_did_not_open_an_unauthenticated_hole()
    {
        RequireDatabase();

        var anonymous = Factory.CreateBrowserClient();

        // The pre-existing closed endpoints are still closed.
        (await anonymous.GetAsync("/api/v1/providers", Ct)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/v1/auth/me", Ct)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);

        // Health is still open, and still does not require a membership.
        (await anonymous.GetAsync("/health", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Antiforgery still bites on the organization surface's host. A GET needs no
    /// token, so this checks the state-changing endpoint that does.
    /// </summary>
    [Fact]
    public async Task Antiforgery_still_refuses_a_state_change_without_a_token()
    {
        RequireDatabase();

        var (client, _) = await SignedInMemberAsync(OrganizationRoles.Member);

        var response = await client.PostWithoutAntiforgeryAsync(
            "/api/v1/auth/logout", new { }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<string> ComparableBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        using var document = JsonDocument.Parse(body);

        var fields = document.RootElement.EnumerateObject()
            .Where(p => p.Name != "traceId")
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.Value}");

        return string.Join("&", fields);
    }
}
