using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIDesignStudio.Infrastructure.Providers.Stitch;

public sealed class StitchOptions
{
    public const string SectionName = "Providers:Stitch";

    /// <summary>
    /// Path to the <c>stitch</c> executable. Resolved from PATH when left as the default.
    /// </summary>
    public string ExecutablePath { get; set; } = "stitch";

    /// <summary>Working directory for CLI invocations; also where <c>.stitch/</c> artifacts land.</summary>
    public string WorkingDirectory { get; set; } = ".";

    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// When true, the adapter refuses any billable operation and serves only
    /// planning and readiness calls. Default for local and CI environments so
    /// that a mistake cannot spend money.
    /// </summary>
    public bool BillableOperationsEnabled { get; set; }
}

/// <summary>
/// Normalized result of one CLI invocation.
/// </summary>
/// <param name="Success">The <c>success</c> field of the CLI's JSON envelope.</param>
/// <param name="Data">The <c>data</c> payload when successful.</param>
/// <param name="ErrorCode">The CLI's machine-readable error code, e.g. <c>MISSING_CANVAS_CREDENTIALS</c>.</param>
/// <param name="ErrorMessage">Human-readable error text.</param>
/// <param name="RawStdout">Unparsed stdout, retained for diagnostics.</param>
public sealed record StitchResult(
    bool Success,
    JsonElement? Data,
    string? ErrorCode,
    string? ErrorMessage,
    string RawStdout);

/// <summary>
/// Port over the Stitch CLI. Exists so the adapter's request-shaping and
/// response-mapping logic can be tested against recorded CLI output without the
/// CLI installed and without credentials.
/// </summary>
public interface IStitchCommandRunner
{
    Task<StitchResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken);
}

/// <summary>
/// Invokes the real <c>stitch</c> CLI as a child process.
///
/// Two behaviours of the CLI drive this design, both verified in SPIKE-01:
///
///   1. It exits with status 0 even when the operation failed. Success must
///      therefore be read from the JSON envelope's <c>success</c> field; branching
///      on exit code would treat every auth failure as a success.
///   2. Errors arrive as structured JSON with a stable <c>error.code</c>, which is
///      what retry policy keys off.
///
/// Credentials are never passed as arguments (process arguments are world-readable
/// via /proc); they are inherited from the environment only.
/// </summary>
public sealed class StitchProcessCommandRunner(
    IOptions<StitchOptions> options,
    ILogger<StitchProcessCommandRunner> logger) : IStitchCommandRunner
{
    private readonly StitchOptions _options = options.Value;

    public async Task<StitchResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            WorkingDirectory = Path.GetFullPath(_options.WorkingDirectory),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // Force machine-readable output and disable any browser launch; this
        // process has no display and must never block on a user interaction.
        psi.Environment["STITCH_NO_BROWSER"] = "1";
        psi.Environment["NO_COLOR"] = "1";

        logger.LogDebug("Invoking stitch {Args}", string.Join(' ', Redact(args)));

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new StitchResult(
                false, null, "CLI_NOT_FOUND",
                $"Could not start '{_options.ExecutablePath}': {ex.Message}", string.Empty);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            return new StitchResult(
                false, null, "CLI_TIMEOUT",
                $"stitch did not complete within {_options.TimeoutSeconds}s.", stdout.ToString());
        }

        return Parse(stdout.ToString(), stderr.ToString(), process.ExitCode);
    }

    /// <summary>
    /// Parses the CLI's JSON envelope. Falls back to a synthetic error when stdout
    /// is not JSON, which happens if the CLI prints a usage error.
    /// </summary>
    internal static StitchResult Parse(string stdout, string stderr, int exitCode)
    {
        var trimmed = stdout.Trim();

        if (trimmed.Length == 0)
        {
            return new StitchResult(
                false, null, "CLI_NO_OUTPUT",
                $"stitch produced no output (exit {exitCode}). stderr: {stderr.Trim()}", stdout);
        }

        JsonDocument doc;

        try
        {
            doc = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            return new StitchResult(
                false, null, "CLI_UNPARSEABLE_OUTPUT",
                $"stitch output was not JSON (exit {exitCode}).", stdout);
        }

        using (doc)
        {
            var root = doc.RootElement.Clone();

            // Envelope form: { "success": bool, "data"|"error": ... }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("success", out var successEl))
            {
                var success = successEl.ValueKind == JsonValueKind.True;

                if (success)
                {
                    return new StitchResult(
                        true,
                        root.TryGetProperty("data", out var data) ? data : null,
                        null, null, stdout);
                }

                string? code = null;
                string? message = null;

                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                    message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                }

                return new StitchResult(false, null, code ?? "UNKNOWN", message ?? "stitch reported a failure", stdout);
            }

            // Bare form: commands such as `status --json` and `--schema` return the
            // object directly, with no envelope.
            return new StitchResult(true, root, null, null, stdout);
        }
    }

    /// <summary>Strips values that could carry a secret before logging.</summary>
    private static IEnumerable<string> Redact(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (arg is "--api-key" or "--access-token")
            {
                yield return arg;
                yield return "***";
                i++;
                continue;
            }

            yield return arg.StartsWith("--api-key=", StringComparison.Ordinal) ||
                         arg.StartsWith("--access-token=", StringComparison.Ordinal)
                ? arg[..arg.IndexOf('=')] + "=***"
                : arg;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill; nothing to do.
        }
    }
}
