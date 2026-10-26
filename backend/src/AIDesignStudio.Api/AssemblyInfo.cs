using System.Runtime.CompilerServices;

// WebApplicationFactory<Program> needs to see the implicit Program class that
// top-level statements generate, which is internal by default.
[assembly: InternalsVisibleTo("AIDesignStudio.IntegrationTests")]
