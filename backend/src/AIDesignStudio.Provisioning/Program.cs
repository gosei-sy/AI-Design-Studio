using AIDesignStudio.Application.Organizations;
using AIDesignStudio.Infrastructure;
using AIDesignStudio.Infrastructure.Organizations;
using AIDesignStudio.Provisioning;
using AIDesignStudio.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// aids-provision — the trusted operator provisioning tool (D-9, ADR-0009).
//
// This is the only way an account gains an organization membership or a role.
// Deliberately **not** an HTTP endpoint: an admin API is reachable by anyone who
// reaches the network, and a bootstrap route is reachable by everyone until it
// has been used once. Running this needs shell access to a host holding the
// database connection string, and that is the control.
//
// Three rules it keeps, each for a stated reason:
//
//  1. **No secret in argv.** /proc/<pid>/cmdline is world-readable on Linux, so
//     a password passed as an argument is visible to every local user while the
//     process lives — and is written to the operator's shell history. The
//     password is read from standard input instead.
//  2. **No secret in output.** Nothing printed or logged here contains a
//     password or a reset token. A provisioning run often ends up pasted into a
//     ticket.
//  3. **Attribution is required.** --actor is mandatory, because the membership
//     records the operator's assertion that this person was verified out of
//     band. That assertion *is* the proofing.
var parsed = CommandLine.Parse(args);

if (parsed.Error is { } error)
{
    Console.Error.WriteLine($"aids-provision: {error}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLine.Usage);
    return 1;
}

if (parsed.ShowHelp)
{
    Console.WriteLine(CommandLine.Usage);
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "AIDS_");
// System-scoped: an organization must be found or created before any membership
// exists to scope the work to. There is no HTTP request here, so nothing a
// caller sends can reach this elevation.
builder.Services.AddInfrastructure(builder.Configuration, OrganizationScoping.System);

// Keep the console readable: the interesting line is the service's own
// "Provisioned ..." record, not EF Core's SQL.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

// All logging to stderr, so stdout is only the provisioning record. An operator
// pastes that record into a ticket, and host startup chatter interleaved with it
// is both noise and a way for a warning to be missed.
builder.Logging.AddConsole(options =>
    options.LogToStandardErrorThreshold = LogLevel.Trace);

using var host = builder.Build();

// Started so the host's own startup validation runs — including the fail-closed
// Data Protection and password-reset transport gates (W2-003, W2-008). A tool
// that cheerfully provisioned accounts into a deployment the API refuses to
// start in would be telling the operator something false.
await host.StartAsync();

using var serviceScope = host.Services.CreateScope();
var provisioning = serviceScope.ServiceProvider.GetRequiredService<OperatorProvisioningService>();

try
{
    switch (parsed.Command)
    {
        case Command.Provision:
            {
                var result = await provisioning.ProvisionAsync(
                    new ProvisionRequest(parsed.Email!, parsed.Organization!, parsed.Roles, parsed.Actor!),
                    PasswordPrompt.ReadAsync,
                    CancellationToken.None);

                var userState = result.UserCreated ? "created" : "existing";
                var organizationState = result.OrganizationCreated ? "created" : "existing";

                Console.WriteLine(
                    $"""
                Provisioned.
                  user          {result.UserId} ({parsed.Email}) [{userState}]
                  organization  {result.OrganizationId} ({result.OrganizationName}) [{organizationState}]
                  roles         {result.Roles}
                  asserted by   {result.ProvisionedBy}
                """);

                break;
            }

        case Command.Revoke:
            {
                var membershipId = await provisioning.RevokeAsync(
                    parsed.Email!, parsed.Actor!, CancellationToken.None);

                Console.WriteLine($"Revoked membership {membershipId} for {parsed.Email}.");
                break;
            }
    }
}
catch (ProvisioningException failure)
{
    Console.Error.WriteLine($"aids-provision: {failure.Code}: {failure.Message}");
    return 1;
}
catch (DomainException failure)
{
    Console.Error.WriteLine($"aids-provision: {failure.Code}: {failure.Message}");
    return 1;
}
finally
{
    await host.StopAsync();
}

return 0;
