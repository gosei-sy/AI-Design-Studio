using System.Runtime.CompilerServices;

// The adapter's internal request-shaping and response-mapping helpers are tested
// directly. They are internal rather than public because they are not part of the
// contract any other module may depend on.
[assembly: InternalsVisibleTo("AIDesignStudio.UnitTests")]
