using AIDesignStudio.Domain.Organizations;

namespace AIDesignStudio.Provisioning;

internal enum Command
{
    None,
    Provision,
    Revoke,
}

internal sealed record ParsedCommandLine
{
    public Command Command { get; init; }

    public string? Email { get; init; }

    public string? Organization { get; init; }

    public string? Actor { get; init; }

    public OrganizationRoles Roles { get; init; }

    public bool ShowHelp { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// A hand-rolled parser rather than a command-line library, for two reasons: it
/// is a hundred lines, and every dependency added to a tool that holds database
/// credentials and grants access has to be worth it.
/// </summary>
internal static class CommandLine
{
    public const string Usage = """
        aids-provision — trusted operator provisioning (decision D-9, ADR-0009)

        The only path that grants an organization membership or a role. There is no
        HTTP equivalent, by design.

        USAGE
          aids-provision provision --email <address> --organization <name>
                                   --role Member [--role Approver] --actor <who>
          aids-provision revoke    --email <address> --actor <who>

        OPTIONS
          --email         The account. Created if it does not exist.
          --organization  Selected by name if it exists, created if it does not.
          --role          Member and/or Approver. Repeat the flag for both.
                          At least one is required.
          --actor         Required. Who asserts this person was verified out of band.
                          Recorded on the membership and not reconstructable later.

        A password is NEVER accepted as an argument. When a new account has to be
        created the password is read from standard input and is not echoed.

        Run this interactively and let it prompt. Reading from stdin is only as safe
        as whatever writes to it, so do NOT pipe a literal:

            echo 'secret' | aids-provision ...     # WRONG — shell history, process args

        For automation, keep the literal out of the command itself:

            read -rs PW; printf '%s' "$PW" | aids-provision ... ; unset PW

        The connection string comes from ConnectionStrings:Default, or from
        AIDS_ConnectionStrings__Default in the environment.
        """;

    public static ParsedCommandLine Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            return new ParsedCommandLine { ShowHelp = true };
        }

        var command = args[0] switch
        {
            "provision" => Command.Provision,
            "revoke" => Command.Revoke,
            _ => Command.None,
        };

        if (command == Command.None)
        {
            return new ParsedCommandLine { Error = $"unknown command '{args[0]}'." };
        }

        string? email = null;
        string? organization = null;
        string? actor = null;
        var roles = OrganizationRoles.None;

        for (var i = 1; i < args.Length; i++)
        {
            var option = args[i];
            var value = i + 1 < args.Length ? args[i + 1] : null;

            switch (option)
            {
                case "--email" when value is not null:
                    email = value;
                    i++;
                    break;

                case "--organization" when value is not null:
                    organization = value;
                    i++;
                    break;

                case "--actor" when value is not null:
                    actor = value;
                    i++;
                    break;

                case "--role" when value is not null:
                    if (!TryParseRole(value, out var role))
                    {
                        return new ParsedCommandLine
                        {
                            Error = $"'{value}' is not a role. Valid roles are Member and Approver.",
                        };
                    }

                    roles |= role;
                    i++;
                    break;

                // Rejected rather than ignored. A password that reached argv is
                // already in the process table and the shell history, so the only
                // useful response is to say so loudly: silently ignoring the flag
                // would leave the operator believing the secret was never
                // exposed.
                case "--password":
                case "--password-file":
                    return new ParsedCommandLine
                    {
                        Error =
                            $"{option} is not supported and never will be. A password passed as a " +
                            "command-line argument is readable by every local user through " +
                            "/proc/<pid>/cmdline and is recorded in your shell history. Treat that " +
                            "password as compromised and choose another. The tool reads it from " +
                            "standard input.",
                    };

                default:
                    return new ParsedCommandLine { Error = $"unrecognised option '{option}'." };
            }
        }

        return Validate(command, email, organization, actor, roles);
    }

    private static ParsedCommandLine Validate(
        Command command, string? email, string? organization, string? actor, OrganizationRoles roles)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return new ParsedCommandLine { Error = "--email is required." };
        }

        if (string.IsNullOrWhiteSpace(actor))
        {
            return new ParsedCommandLine
            {
                Error = "--actor is required: the membership records who asserted this person was "
                    + "verified out of band.",
            };
        }

        if (command == Command.Provision)
        {
            if (string.IsNullOrWhiteSpace(organization))
            {
                return new ParsedCommandLine { Error = "--organization is required." };
            }

            if (roles == OrganizationRoles.None)
            {
                return new ParsedCommandLine
                {
                    Error = "at least one --role is required (Member, Approver, or both).",
                };
            }
        }

        return new ParsedCommandLine
        {
            Command = command,
            Email = email,
            Organization = organization,
            Actor = actor,
            Roles = roles,
        };
    }

    /// <summary>
    /// Accepts only a single named role per flag.
    ///
    /// <para><c>Enum.TryParse</c> on a flags enum also accepts "3" and
    /// "Member, Approver", which would let an operator pass an integer and
    /// bypass the names entirely. Repeating <c>--role</c> is the supported way to
    /// grant both, so a comma list has no reason to parse.</para>
    /// </summary>
    private static bool TryParseRole(string value, out OrganizationRoles role)
    {
        role = OrganizationRoles.None;

        foreach (var candidate in new[] { OrganizationRoles.Member, OrganizationRoles.Approver })
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                role = candidate;
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Reads a new account's password from standard input without echoing it.
///
/// <para>Falls back to a plain read when input is redirected: a pipe has no
/// console to suppress echo on, and refusing would make the tool unusable from
/// automation that already holds the secret. Nothing is echoed either way, and
/// the value is never logged.</para>
/// </summary>
internal static class PasswordPrompt
{
    public static Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
        {
            return Task.FromResult(Console.ReadLine() ?? string.Empty);
        }

        // The prompt goes to stderr so that stdout stays a clean record of what
        // was provisioned, which is what an operator pastes into a ticket.
        Console.Error.Write("Initial password for the new account (not echoed): ");

        var password = new System.Text.StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }

        return Task.FromResult(password.ToString());
    }
}
