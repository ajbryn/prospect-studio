using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// Stores every <see cref="DateTimeOffset"/> as its UTC instant.
/// </summary>
/// <remarks>
/// The SQLite provider's own <see cref="DateTimeOffset"/> mapping keeps the offset in the text it
/// writes, which does not sort, so it refuses <c>ORDER BY</c> and range comparisons on such a column
/// ("SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses"). Times are
/// stored in UTC anyway (CLAUDE.md §Conventions), so dropping the offset costs nothing and keeps
/// <c>list_campaigns</c>-style "newest first" queries translatable on SQLite and on a database server
/// alike. Applied once as a convention in <see cref="ProspectDbContext"/>, never per property.
/// </remarks>
public sealed class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, DateTime>
{
    public UtcDateTimeOffsetConverter()
        : base(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)))
    {
    }
}
