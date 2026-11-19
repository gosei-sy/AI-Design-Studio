using AIDesignStudio.Domain.Organizations;
using AIDesignStudio.Provisioning;
using Shouldly;
using Xunit;

namespace AIDesignStudio.UnitTests;

/// <summary>
/// The provisioning tool's command line (M01-03 requirement 10, ADR-0009).
///
/// <para>Tested rather than documented, because two of the rules here are
/// security properties and a comment is not a control. A password must never be
/// accepted as an argument — <c>/proc/&lt;pid&gt;/cmdline</c> is world-readable
/// and shell history is forever — and an unattributed grant of access must be
/// refused outright.</para>
/// </summary>
public sealed class ProvisioningCommandLineTests
{
    private static ParsedCommandLine Parse(params string[] args) => CommandLine.Parse(args);

    /// <summary>The parse error, asserted to exist before it is inspected.</summary>
    private static string ErrorFrom(ParsedCommandLine parsed)
    {
        parsed.Error.ShouldNotBeNull("the command line should have been refused");

        return parsed.Error;
    }

    // -----------------------------------------------------------------------
    // No secret in argv
    // -----------------------------------------------------------------------

    /// <summary>
    /// Rejected loudly, not ignored. An operator who passes <c>--password</c> has
    /// already exposed it; silently ignoring the flag would leave them believing
    /// otherwise, so the error says the password is compromised and to pick
    /// another.
    /// </summary>
    [Theory]
    [InlineData("--password")]
    [InlineData("--password-file")]
    public void A_password_passed_as_an_argument_is_refused(string option)
    {
        var parsed = Parse("provision", "--email", "a@b.test", option, "hunter2");

        ErrorFrom(parsed).ShouldContain("/proc/<pid>/cmdline");
        ErrorFrom(parsed).ShouldContain("shell history");
        parsed.Command.ShouldBe(Command.None);
    }

    [Fact]
    public void No_option_accepts_a_secret()
    {
        // The parser's whole vocabulary, so a future option carrying a credential
        // has to be added here deliberately.
        string[] accepted = ["--email", "--organization", "--role", "--actor"];

        foreach (var option in accepted)
        {
            option.ShouldNotContain("password");
            option.ShouldNotContain("secret");
            option.ShouldNotContain("token");
        }
    }

    // -----------------------------------------------------------------------
    // Attribution
    // -----------------------------------------------------------------------

    [Fact]
    public void Provisioning_without_an_actor_is_refused() =>
        ErrorFrom(Parse(
            "provision", "--email", "a@b.test", "--organization", "Acme", "--role", "Member"))
            .ShouldContain("--actor is required");

    [Fact]
    public void Revoking_without_an_actor_is_refused() =>
        ErrorFrom(Parse("revoke", "--email", "a@b.test")).ShouldContain("--actor is required");

    // -----------------------------------------------------------------------
    // Roles
    // -----------------------------------------------------------------------

    [Fact]
    public void Both_roles_can_be_granted_by_repeating_the_flag()
    {
        var parsed = Parse(
            "provision", "--email", "a@b.test", "--organization", "Acme",
            "--role", "Member", "--role", "Approver", "--actor", "ops");

        parsed.Error.ShouldBeNull();
        parsed.Roles.ShouldBe(OrganizationRoles.Member | OrganizationRoles.Approver);
        parsed.Command.ShouldBe(Command.Provision);
        parsed.Email.ShouldBe("a@b.test");
        parsed.Organization.ShouldBe("Acme");
        parsed.Actor.ShouldBe("ops");
    }

    [Theory]
    [InlineData("member", OrganizationRoles.Member)]
    [InlineData("APPROVER", OrganizationRoles.Approver)]
    public void Role_names_are_case_insensitive(string name, OrganizationRoles expected) =>
        Parse("provision", "--email", "a@b.test", "--organization", "Acme",
                "--role", name, "--actor", "ops")
            .Roles.ShouldBe(expected);

    /// <summary>
    /// <c>Enum.TryParse</c> on a flags enum happily accepts "3" and
    /// "Member, Approver". Both are refused here: an integer bypasses the names
    /// entirely, and a comma list is a second way to express something
    /// <c>--role</c> twice already says.
    /// </summary>
    [Theory]
    [InlineData("3")]
    [InlineData("1")]
    [InlineData("Member, Approver")]
    [InlineData("None")]
    [InlineData("Admin")]
    [InlineData("")]
    public void An_unrecognised_role_is_refused(string role) =>
        ErrorFrom(Parse("provision", "--email", "a@b.test", "--organization", "Acme",
                "--role", role, "--actor", "ops"))
            .ShouldContain("not a role");

    [Fact]
    public void Provisioning_requires_at_least_one_role() =>
        ErrorFrom(Parse(
            "provision", "--email", "a@b.test", "--organization", "Acme", "--actor", "ops"))
            .ShouldContain("at least one --role");

    [Fact]
    public void Provisioning_requires_an_organization() =>
        ErrorFrom(Parse("provision", "--email", "a@b.test", "--role", "Member", "--actor", "ops"))
            .ShouldContain("--organization is required");

    [Fact]
    public void Provisioning_requires_an_email() =>
        ErrorFrom(Parse("provision", "--organization", "Acme", "--role", "Member", "--actor", "ops"))
            .ShouldContain("--email is required");

    // -----------------------------------------------------------------------
    // Shape
    // -----------------------------------------------------------------------

    [Fact]
    public void Revoking_needs_no_organization_or_role()
    {
        var parsed = Parse("revoke", "--email", "a@b.test", "--actor", "security");

        parsed.Error.ShouldBeNull();
        parsed.Command.ShouldBe(Command.Revoke);
        parsed.Roles.ShouldBe(OrganizationRoles.None);
    }

    [Fact]
    public void An_unknown_command_is_refused() =>
        ErrorFrom(Parse("delete-everything", "--email", "a@b.test")).ShouldContain("unknown command");

    [Fact]
    public void An_unrecognised_option_is_refused() =>
        ErrorFrom(Parse("provision", "--sudo")).ShouldContain("unrecognised option");

    [Theory]
    [InlineData]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void Help_is_available_and_is_not_an_error(params string[] args)
    {
        var parsed = Parse(args);

        parsed.ShowHelp.ShouldBeTrue();
        parsed.Error.ShouldBeNull();
    }

    /// <summary>
    /// The usage text is where an operator learns the rules, so it has to state
    /// the two that matter.
    /// </summary>
    [Fact]
    public void Usage_states_that_a_password_is_never_an_argument()
    {
        CommandLine.Usage.ShouldContain("NEVER accepted as an argument");
        CommandLine.Usage.ShouldContain("standard input");
        CommandLine.Usage.ShouldContain("verified out of band");
    }

    /// <summary>
    /// W3-004. Refusing <c>--password</c> and then telling the operator to pipe a
    /// literal is not a control: <c>echo 'secret' | …</c> puts the value in shell
    /// history and in the producer's process arguments — the same two places the
    /// refusal exists to protect. The usage text has to say so, and name a safe
    /// alternative, or the runbook recreates the exposure.
    /// </summary>
    [Fact]
    public void Usage_warns_that_stdin_is_only_as_safe_as_its_producer()
    {
        CommandLine.Usage.ShouldContain("only as safe");
        CommandLine.Usage.ShouldContain("do NOT pipe a literal");

        // The wrong way, shown as wrong.
        CommandLine.Usage.ShouldContain("echo 'secret' | aids-provision ...     # WRONG");

        // And a usable alternative, or the warning is just an obstacle.
        CommandLine.Usage.ShouldContain("read -rs");
    }
}
