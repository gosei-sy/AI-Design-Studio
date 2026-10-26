using AIDesignStudio.Infrastructure;
using AIDesignStudio.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "AIDS_");

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.Configure<JobDispatcherOptions>(
    builder.Configuration.GetSection(JobDispatcherOptions.SectionName));

// Job handlers are registered here as milestones add them. M00 ships the
// dispatcher and its lease/retry mechanics with no handlers: the mechanism is
// what needed proving, and a handler without a verified provider call would be
// untested code.
builder.Services.AddHostedService<JobDispatcher>();

var host = builder.Build();
host.Run();
