using AIDesignStudio.Infrastructure;
using AIDesignStudio.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "AIDS_");

// System-scoped, explicitly (ADR-0009). The worker dispatches jobs for every
// tenant, so an organization-scoped context would see an empty queue — and a
// worker that silently processed nothing is exactly the kind of failure that
// takes a week to notice. There is no HTTP request here to resolve a membership
// from, which is why the elevation is safe: it is a property of the process, not
// of anything a caller can send.
builder.Services.AddInfrastructure(builder.Configuration, OrganizationScoping.System);

builder.Services.Configure<JobDispatcherOptions>(
    builder.Configuration.GetSection(JobDispatcherOptions.SectionName));

// Job handlers are registered here as milestones add them. M00 ships the
// dispatcher and its lease/retry mechanics with no handlers: the mechanism is
// what needed proving, and a handler without a verified provider call would be
// untested code.
builder.Services.AddHostedService<JobDispatcher>();

var host = builder.Build();
host.Run();
