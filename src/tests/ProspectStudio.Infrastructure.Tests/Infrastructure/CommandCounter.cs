using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ProspectStudio.Infrastructure.Tests.Infrastructure;

/// <summary>
/// Counts the commands EF actually sends to the database, so a test can assert that a bulk write is
/// batched without measuring wall-clock time.
/// </summary>
/// <remarks>
/// This replaces a stopwatch. A per-row <c>SaveChangesAsync</c> (or a per-row
/// <c>ExecuteNonQueryAsync</c> loop) sends one command per row and is what technical-design §5.3's
/// "bulk writes" rule exists to prevent; a timing threshold measures the machine as much as the code,
/// and the one that used to be here passed at 7-9 s and failed at 10-11.5 s on the same build under a
/// parallel <c>dotnet test</c>. Command count is the thing the rule is actually about, and it does not
/// move when the test host is busy.
/// </remarks>
internal sealed class CommandCounter : DbCommandInterceptor
{
    private int _commands;

    /// <summary>Commands executed since construction, across every context this was registered on.</summary>
    public int Commands => Volatile.Read(ref _commands);

    /// <summary>The text of each command, kept so a failure can say what was being sent.</summary>
    public List<string> CommandTexts { get; } = [];

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Record(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Record(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <summary>A few of the commands sent, for a failure message.</summary>
    public string Sample(int take = 3)
    {
        lock (CommandTexts)
        {
            return string.Join(
                Environment.NewLine,
                CommandTexts.Take(take).Select(text => text.Length > 240 ? text[..240] + " ..." : text));
        }
    }

    private void Record(DbCommand command)
    {
        Interlocked.Increment(ref _commands);

        lock (CommandTexts)
        {
            if (CommandTexts.Count < 50)
            {
                CommandTexts.Add(command.CommandText);
            }
        }
    }
}

/// <summary>
/// Counts <c>SaveChanges</c> invocations, which is what technical-design §5.3 actually prescribes:
/// "batches of ~500 per <c>SaveChangesAsync</c>".
/// </summary>
/// <remarks>
/// This is the guard for §5.3's bulk-write rule, and command count is <strong>not</strong>. EF Core's
/// SQLite provider does not batch inserts into multi-statement commands: a standalone probe over one
/// four-column table, <c>AddRange</c> plus <c>SaveChangesAsync</c> every 500 rows, sent 5,000
/// single-row <c>INSERT</c> commands for 5,000 rows -
/// <c>INSERT INTO "companies" ("id", "domain", "name", "name_norm") VALUES (@p0, @p1, @p2, @p3)</c> -
/// and a <c>SaveChangesAsync</c> per row sends the same number. So commands cannot tell the two shapes
/// apart here, while <c>SaveChanges</c> calls differ by three orders of magnitude.
/// </remarks>
internal sealed class SaveChangesCounter : SaveChangesInterceptor
{
    private int _calls;

    /// <summary>Calls to <c>SaveChanges</c> or <c>SaveChangesAsync</c> since construction.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Interlocked.Increment(ref _calls);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
