using AIDesignStudio.Application.Providers;
using AIDesignStudio.Infrastructure.Providers.Stitch;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace AIDesignStudio.UnitTests;

/// <summary>
/// A fake CLI that replays recorded output. The recordings below are verbatim
/// stdout from @google/stitch 0.11.1, captured during SPIKE-01, so these tests
/// assert against the provider's real behaviour without needing it installed or
/// authenticated.
/// </summary>
internal sealed class FakeStitchRunner(StitchResult result) : IStitchCommandRunner
{
    public List<IReadOnlyList<string>> Invocations { get; } = [];

    public Task<StitchResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        Invocations.Add(args);
        return Task.FromResult(result);
    }

    /// <summary>Builds a runner that parses raw recorded stdout the way the real runner does.</summary>
    public static FakeStitchRunner FromRecordedStdout(string stdout) =>
        new(StitchProcessCommandRunner.Parse(stdout, string.Empty, exitCode: 0));
}

/// <summary>A runner that fails hard, to prove readiness still does not throw.</summary>
internal sealed class ThrowingStitchRunner : IStitchCommandRunner
{
    public Task<StitchResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("the runner itself blew up");
}

public sealed class StitchEnvelopeParsingTests
{
    /// <summary>
    /// Recorded from <c>stitch get screen 12345 --project 99999 --json</c> with no
    /// credentials configured.
    ///
    /// The critical detail: the process exited 0. Treating exit code as the
    /// success signal would read this failure as a success.
    /// </summary>
    private const string MissingCredentialsStdout = """
        {
          "success": false,
          "error": {
            "code": "MISSING_CANVAS_CREDENTIALS",
            "message": "MISSING_CANVAS_CREDENTIALS: Stitch Canvas requires authentication. Set STITCH_API_KEY or run 'stitch login'.",
            "recoverable": true
          }
        }
        """;

    [Fact]
    public void A_failure_envelope_is_detected_even_when_the_process_exits_zero()
    {
        var result = StitchProcessCommandRunner.Parse(MissingCredentialsStdout, string.Empty, exitCode: 0);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("MISSING_CANVAS_CREDENTIALS");
        result.ErrorMessage!.ShouldContain("STITCH_API_KEY");
    }

    [Fact]
    public void A_success_envelope_exposes_its_data_payload()
    {
        // Recorded from `stitch generate screen ... --dry-run --json`.
        const string stdout = """
            {
              "success": true,
              "data": {
                "resource": "screen",
                "id": "screen-preview-dry-run",
                "project": "[will-create-new-project]",
                "device": "MOBILE",
                "dryRun": true,
                "requests": [
                  { "kind": "tool", "tool": "create_project", "args": { "title": "Probe" } },
                  { "kind": "tool", "tool": "generate_screen_from_text",
                    "args": { "projectId": "[will-create-new-project]", "prompt": "Login screen", "deviceType": "MOBILE" } }
                ]
              }
            }
            """;

        var result = StitchProcessCommandRunner.Parse(stdout, string.Empty, exitCode: 0);

        result.Success.ShouldBeTrue();
        result.Data.ShouldNotBeNull();
        result.Data!.Value.GetProperty("id").GetString().ShouldBe("screen-preview-dry-run");
    }

    /// <summary>
    /// <c>stitch status --json</c> returns a bare object with no success/error
    /// envelope, so the parser must accept both shapes.
    /// </summary>
    [Fact]
    public void A_bare_object_response_is_treated_as_success()
    {
        const string stdout = """
            { "authenticated": false, "canvas": { "present": false } }
            """;

        var result = StitchProcessCommandRunner.Parse(stdout, string.Empty, exitCode: 0);

        result.Success.ShouldBeTrue();
        result.Data!.Value.GetProperty("authenticated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Non_json_output_is_reported_rather_than_throwing()
    {
        var result = StitchProcessCommandRunner.Parse("Usage: stitch [options]", "", exitCode: 1);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("CLI_UNPARSEABLE_OUTPUT");
    }

    [Fact]
    public void Empty_output_is_reported_rather_than_throwing()
    {
        var result = StitchProcessCommandRunner.Parse("", "command not found", exitCode: 127);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("CLI_NO_OUTPUT");
        result.ErrorMessage!.ShouldContain("command not found");
    }
}

public sealed class StitchDesignProviderTests
{
    private static StitchDesignProvider Build(
        IStitchCommandRunner runner, bool billableEnabled = false) =>
        new(runner,
            Options.Create(new StitchOptions { BillableOperationsEnabled = billableEnabled }),
            NullLogger<StitchDesignProvider>.Instance);

    [Fact]
    public void The_adapter_declares_only_capabilities_it_implements()
    {
        var provider = Build(FakeStitchRunner.FromRecordedStdout("{}"));

        provider.ProviderId.ShouldBe("google-stitch");
        provider.GetCapability<IScreenGenerationCapability>().ShouldNotBeNull();
        provider.GetCapability<IScreenImportCapability>().ShouldNotBeNull();
        provider.GetCapability<IDesignSystemProjectionCapability>().ShouldNotBeNull();

        // Not yet verified against a live account, so deliberately not claimed.
        provider.GetCapability<IScreenVariantCapability>().ShouldBeNull();
    }

    /// <summary>
    /// Recorded from <c>stitch status --json</c> with no credentials. Being signed
    /// in to Google is not sufficient for canvas work, so the two credentials are
    /// reported independently.
    /// </summary>
    [Fact]
    public async Task Readiness_reports_the_two_credentials_separately()
    {
        const string stdout = """
            {
              "authenticated": false,
              "experiments": { "loop": false },
              "canvas": {
                "credential": "STITCH_API_KEY",
                "present": false,
                "authMethod": "none",
                "requiredFor": "projects, screens, captures and design systems"
              }
            }
            """;

        var readiness = await Build(FakeStitchRunner.FromRecordedStdout(stdout))
            .CheckReadinessAsync(CancellationToken.None);

        readiness.IsInstalled.ShouldBeTrue();
        readiness.IsAuthenticated.ShouldBeFalse();
        readiness.IsUsable.ShouldBeFalse();
        readiness.MissingCredentialNames.ShouldBe(["google-oauth", "STITCH_API_KEY"]);
    }

    [Fact]
    public async Task Readiness_requires_both_oauth_and_a_canvas_credential()
    {
        const string signedInNoCanvas = """
            { "authenticated": true, "canvas": { "present": false } }
            """;

        var readiness = await Build(FakeStitchRunner.FromRecordedStdout(signedInNoCanvas))
            .CheckReadinessAsync(CancellationToken.None);

        readiness.IsAuthenticated.ShouldBeFalse();
        readiness.MissingCredentialNames.ShouldBe(["STITCH_API_KEY"]);
        readiness.Detail.ShouldContain("no canvas credential");
    }

    [Fact]
    public async Task Readiness_reports_a_missing_cli_distinctly_from_a_missing_credential()
    {
        var runner = new FakeStitchRunner(
            new StitchResult(false, null, "CLI_NOT_FOUND", "not found", string.Empty));

        var readiness = await Build(runner).CheckReadinessAsync(CancellationToken.None);

        readiness.IsInstalled.ShouldBeFalse();
        readiness.Detail.ShouldContain("not installed");
    }

    /// <summary>
    /// The runner treats any valid bare JSON as success, so these shapes all
    /// reach the readiness mapper. TryGetProperty throws on a non-object, which
    /// would break the interface's "must never throw" contract from outside the
    /// adapter's try block (M00-CX-011).
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("[{\"authenticated\":true}]")]
    public async Task Readiness_reports_unavailable_rather_than_throwing_on_non_object_json(
        string stdout)
    {
        var provider = Build(FakeStitchRunner.FromRecordedStdout(stdout));

        var readiness = await provider.CheckReadinessAsync(CancellationToken.None);

        readiness.IsUsable.ShouldBeFalse();
        readiness.Detail.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Readiness_never_throws_whatever_the_runner_returns()
    {
        var runners = new IStitchCommandRunner[]
        {
            FakeStitchRunner.FromRecordedStdout("[]"),
            FakeStitchRunner.FromRecordedStdout("not json at all"),
            FakeStitchRunner.FromRecordedStdout(""),
            new FakeStitchRunner(new StitchResult(true, null, null, null, string.Empty)),
            new ThrowingStitchRunner(),
        };

        foreach (var runner in runners)
        {
            var readiness = await Build(runner).CheckReadinessAsync(CancellationToken.None);
            readiness.ProviderId.ShouldBe("google-stitch");
            readiness.IsUsable.ShouldBeFalse();
        }
    }

    /// <summary>
    /// The cost guard. Every billable call must be blocked unless explicitly
    /// enabled for the environment.
    /// </summary>
    [Fact]
    public async Task Billable_generation_is_refused_when_disabled()
    {
        var provider = Build(FakeStitchRunner.FromRecordedStdout("{}"), billableEnabled: false);

        var ex = await Should.ThrowAsync<ProviderException>(() => provider.GenerateScreenAsync(
            new GenerateScreenRequest("A login screen", TargetDevice.Mobile, null, "Probe", null, null),
            CancellationToken.None));

        ex.Code.ShouldBe("BILLABLE_OPERATIONS_DISABLED");
        ex.IsRetryable.ShouldBeFalse();
    }

    /// <summary>
    /// Planning uses the CLI's --dry-run, which SPIKE-01 verified works with no
    /// credentials and no network. It must therefore stay available even with
    /// billable operations disabled.
    /// </summary>
    [Fact]
    public async Task Planning_is_allowed_when_billable_operations_are_disabled()
    {
        const string dryRunStdout = """
            {
              "success": true,
              "data": {
                "id": "screen-preview-dry-run",
                "dryRun": true,
                "requests": [
                  { "kind": "tool", "tool": "create_project", "args": { "title": "Probe" } },
                  { "kind": "tool", "tool": "generate_screen_from_text",
                    "args": { "prompt": "Login screen", "deviceType": "MOBILE" } }
                ]
              }
            }
            """;

        var runner = FakeStitchRunner.FromRecordedStdout(dryRunStdout);
        var provider = Build(runner, billableEnabled: false);

        var plan = await provider.PlanScreenAsync(
            new GenerateScreenRequest("Login screen", TargetDevice.Mobile, null, "Probe", null, null),
            CancellationToken.None);

        plan.ProviderId.ShouldBe("google-stitch");
        plan.IsBillable.ShouldBeTrue("executing the plan would cost money, even though planning did not");
        plan.Steps.Select(s => s.Operation).ShouldBe(["create_project", "generate_screen_from_text"]);
        runner.Invocations.Single().ShouldContain("--dry-run");
    }

    [Fact]
    public async Task A_generation_request_must_target_either_an_existing_or_a_new_project()
    {
        var provider = Build(FakeStitchRunner.FromRecordedStdout("{}"), billableEnabled: true);

        await Should.ThrowAsync<ArgumentException>(() => provider.PlanScreenAsync(
            new GenerateScreenRequest("prompt", TargetDevice.Desktop, null, null, null, null),
            CancellationToken.None));
    }

    [Theory]
    [InlineData(TargetDevice.Mobile, "MOBILE")]
    [InlineData(TargetDevice.Desktop, "DESKTOP")]
    [InlineData(TargetDevice.Tablet, "TABLET")]
    [InlineData(TargetDevice.Agnostic, "AGNOSTIC")]
    public void Every_target_device_maps_to_a_value_the_provider_schema_accepts(
        TargetDevice device, string expected) =>
        StitchDesignProvider.ToStitchDevice(device).ShouldBe(expected);

    /// <summary>
    /// A credential error must not be retried: it cannot succeed, and for billable
    /// calls each retry is wasted spend.
    /// </summary>
    [Fact]
    public async Task A_missing_credential_error_is_classified_as_non_retryable()
    {
        var runner = new FakeStitchRunner(new StitchResult(
            false, null, "MISSING_CANVAS_CREDENTIALS", "no key", string.Empty));

        var ex = await Should.ThrowAsync<ProviderException>(() =>
            Build(runner).GetScreenAsync("p1", "s1", CancellationToken.None));

        ex.Code.ShouldBe("MISSING_CANVAS_CREDENTIALS");
        ex.IsRetryable.ShouldBeFalse();
    }

    [Fact]
    public async Task A_timeout_is_classified_as_retryable()
    {
        var runner = new FakeStitchRunner(new StitchResult(
            false, null, "CLI_TIMEOUT", "timed out", string.Empty));

        var ex = await Should.ThrowAsync<ProviderException>(() =>
            Build(runner).GetScreenAsync("p1", "s1", CancellationToken.None));

        ex.IsRetryable.ShouldBeTrue();
    }
}
