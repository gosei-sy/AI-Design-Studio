namespace AIDesignStudio.SharedKernel;

/// <summary>
/// Marks a clock whose instants are simulated rather than read from the host.
///
/// Exists so that time-dependent SQL predicates have one authoritative time
/// model in both production and test. A predicate such as
/// "the lease has not expired" must be evaluated by PostgreSQL at the moment it
/// executes the write; passing an application timestamp captured earlier
/// reintroduces a time-of-check/time-of-use gap (see ADR-0005 amendment A6).
///
/// Production therefore lets the database supply the instant. A test that needs
/// deterministic control implements this interface, and the query substitutes
/// the simulated instant for the server clock — the same predicate, with its
/// time source redirected. This is the explicit seam; there is no other
/// difference between the two paths.
/// </summary>
public interface ISimulatedClock : IClock
{
}
