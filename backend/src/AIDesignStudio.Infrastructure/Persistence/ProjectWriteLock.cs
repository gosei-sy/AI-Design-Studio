using Microsoft.EntityFrameworkCore;

namespace AIDesignStudio.Infrastructure.Persistence;

/// <summary>
/// Makes changes to one project's design contract happen one at a time
/// (M01-05).
///
/// <para><b>What it prevents.</b> Every write loads the aggregate, applies a
/// domain operation and saves, and EF writes only the columns that changed. Two
/// requests that load the same draft therefore do not conflict in the database:
/// an approval writes <c>status</c> and <c>payload_hash</c>, an edit writes
/// <c>payload_json</c>, and both succeed — leaving an <em>approved</em> version
/// whose payload is not the one that was hashed, changed after approval by a
/// request that was told it was editing a draft. No constraint catches that; it
/// is two individually valid writes. The filtered unique indexes still stop two
/// approved versions, but not this.</para>
///
/// <para><b>How.</b> A row lock on the project, held for the transaction. The
/// second writer waits, then loads what the first committed and is judged
/// against that. The project row rather than the contract row because the first
/// draft creates the contract — there is no contract row to lock yet, and the
/// project always exists.</para>
///
/// <para><c>FOR NO KEY UPDATE</c>, not <c>FOR UPDATE</c>: it excludes other
/// takers of this lock without blocking the <c>FOR KEY SHARE</c> that foreign-key
/// checks take, so inserting a screen or an asset for the same project is not
/// held up by someone editing its contract.</para>
///
/// <para>Chosen over a concurrency token because it needs no column, no
/// migration and no retry loop in every caller, and contention on one project's
/// contract is a person or two.</para>
/// </summary>
public static class ProjectWriteLock
{
    /// <param name="projectId">
    /// A project the caller has already loaded through the organization query
    /// filter. This statement is raw SQL and is not filtered, so it must never
    /// be given an id taken straight from a request.
    /// </param>
    /// <exception cref="InvalidOperationException">No transaction is open.</exception>
    public static async Task LockProjectForWriteAsync(
        this AppDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            // Outside a transaction the lock is released as the statement ends,
            // which would look like protection and provide none.
            throw new InvalidOperationException(
                "LockProjectForWriteAsync must be called inside a transaction; otherwise the lock " +
                "is released immediately and serializes nothing.");
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM app.projects WHERE id = {projectId} FOR NO KEY UPDATE",
            cancellationToken);
    }
}
