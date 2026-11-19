using AIDesignStudio.Infrastructure.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AIDesignStudio.UnitTests;

/// <summary>
/// Proves a reset token is never written to a log outside Development.
///
/// <para>M01-W2-R3 asked for this directly, and the gap it names is real: the
/// startup gate refuses the wrong configuration, but nothing asserted what the
/// notifier itself does if it is ever reached in the wrong environment — through
/// a misconfiguration, a future code path that skips startup validation, or a
/// host that registers the notifier without the hosted service. A reset token is
/// a credential; "the gate upstream prevents it" is an argument, not
/// evidence.</para>
///
/// These are unit tests because the question is about one class's behaviour given
/// an environment and an option, which needs no host and no database.
/// </summary>
public sealed class PasswordResetNotifierTests
{
    private const string Token = "SUPER-SECRET-RESET-TOKEN-VALUE";
    private const string Email = "someone@example.test";

    /// <summary>Captures what was logged, so the assertion can be about content.</summary>
    private sealed class RecordingLogger : ILogger<LoggingPasswordResetNotifier>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class StubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "AIDesignStudio.Tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static (LoggingPasswordResetNotifier Notifier, RecordingLogger Logger) Build(
        string environment, PasswordResetTransport transport, bool logTokens)
    {
        var logger = new RecordingLogger();

        var notifier = new LoggingPasswordResetNotifier(
            new StubEnvironment(environment),
            Options.Create(new PasswordResetNotifierOptions
            {
                Transport = transport,
                LogTokensInDevelopment = logTokens,
            }),
            logger);

        return (notifier, logger);
    }

    /// <summary>
    /// The whole matrix of ways the token could leak. Only one cell may log it:
    /// Development, with the development transport, with logging explicitly
    /// opted in.
    /// </summary>
    [Theory]
    [InlineData("Development", PasswordResetTransport.DevelopmentLog, true, true)]
    [InlineData("Development", PasswordResetTransport.DevelopmentLog, false, false)]
    [InlineData("Development", PasswordResetTransport.None, true, false)]
    [InlineData("Development", PasswordResetTransport.None, false, false)]
    [InlineData("Staging", PasswordResetTransport.DevelopmentLog, true, false)]
    [InlineData("Staging", PasswordResetTransport.DevelopmentLog, false, false)]
    [InlineData("Staging", PasswordResetTransport.None, true, false)]
    [InlineData("Production", PasswordResetTransport.DevelopmentLog, true, false)]
    [InlineData("Production", PasswordResetTransport.DevelopmentLog, false, false)]
    [InlineData("Production", PasswordResetTransport.None, true, false)]
    [InlineData("Production", PasswordResetTransport.None, false, false)]
    public async Task A_reset_token_is_logged_only_in_development_with_logging_opted_in(
        string environment, PasswordResetTransport transport, bool logTokens, bool shouldLogToken)
    {
        var (notifier, logger) = Build(environment, transport, logTokens);

        await notifier.SendPasswordResetAsync(Email, Token, TestContext.Current.CancellationToken);

        var logged = logger.Messages.Any(m => m.Contains(Token, StringComparison.Ordinal));

        logged.ShouldBe(
            shouldLogToken,
            $"{environment} + {transport} (logTokens={logTokens}) must " +
            (shouldLogToken ? "log the token" : "NOT log the token"));
    }

    /// <summary>
    /// Not logging the token must not mean saying nothing. An operator needs to
    /// know a reset was requested and not delivered, or an outage looks like
    /// silence.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task An_undeliverable_reset_is_still_reported_without_the_token(string environment)
    {
        var (notifier, logger) = Build(environment, PasswordResetTransport.None, logTokens: true);

        await notifier.SendPasswordResetAsync(Email, Token, TestContext.Current.CancellationToken);

        var message = logger.Messages.ShouldHaveSingleItem();

        message.ShouldNotContain(Token);
        message.ShouldContain(Email);
        message.ShouldContain("D-8");
    }

    /// <summary>
    /// The notifier never throws. It used to throw outside Development, which
    /// made `/forgot-password` fail only for addresses that exist — an
    /// account-existence oracle (W2-002, W2-003).
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Development")]
    public async Task The_notifier_never_throws_at_request_time(string environment)
    {
        var (notifier, _) = Build(environment, PasswordResetTransport.None, logTokens: false);

        await Should.NotThrowAsync(() => notifier.SendPasswordResetAsync(
            Email, Token, TestContext.Current.CancellationToken));
    }
}
