namespace ProspectStudio.Core.Geography;

/// <param name="Fips">The two-digit state FIPS code, which is what the county file's <c>STATEFP</c> holds.</param>
/// <param name="Abbreviation">The USPS two-letter code, which is what a <c>GeoScope</c>'s <c>states</c> holds.</param>
public sealed record UsState(string Fips, string Abbreviation, string Name);

/// <summary>
/// The states, DC and the island areas the Census county file covers, so <c>resolve_geography</c> can
/// turn "Texas", "TX" or <c>STATEFP</c> 48 into the same answer (technical-design §6.2).
/// </summary>
public static class UsStates
{
    private static readonly UsState[] _all =
    [
        new("01", "AL", "Alabama"),
        new("02", "AK", "Alaska"),
        new("04", "AZ", "Arizona"),
        new("05", "AR", "Arkansas"),
        new("06", "CA", "California"),
        new("08", "CO", "Colorado"),
        new("09", "CT", "Connecticut"),
        new("10", "DE", "Delaware"),
        new("11", "DC", "District of Columbia"),
        new("12", "FL", "Florida"),
        new("13", "GA", "Georgia"),
        new("15", "HI", "Hawaii"),
        new("16", "ID", "Idaho"),
        new("17", "IL", "Illinois"),
        new("18", "IN", "Indiana"),
        new("19", "IA", "Iowa"),
        new("20", "KS", "Kansas"),
        new("21", "KY", "Kentucky"),
        new("22", "LA", "Louisiana"),
        new("23", "ME", "Maine"),
        new("24", "MD", "Maryland"),
        new("25", "MA", "Massachusetts"),
        new("26", "MI", "Michigan"),
        new("27", "MN", "Minnesota"),
        new("28", "MS", "Mississippi"),
        new("29", "MO", "Missouri"),
        new("30", "MT", "Montana"),
        new("31", "NE", "Nebraska"),
        new("32", "NV", "Nevada"),
        new("33", "NH", "New Hampshire"),
        new("34", "NJ", "New Jersey"),
        new("35", "NM", "New Mexico"),
        new("36", "NY", "New York"),
        new("37", "NC", "North Carolina"),
        new("38", "ND", "North Dakota"),
        new("39", "OH", "Ohio"),
        new("40", "OK", "Oklahoma"),
        new("41", "OR", "Oregon"),
        new("42", "PA", "Pennsylvania"),
        new("44", "RI", "Rhode Island"),
        new("45", "SC", "South Carolina"),
        new("46", "SD", "South Dakota"),
        new("47", "TN", "Tennessee"),
        new("48", "TX", "Texas"),
        new("49", "UT", "Utah"),
        new("50", "VT", "Vermont"),
        new("51", "VA", "Virginia"),
        new("53", "WA", "Washington"),
        new("54", "WV", "West Virginia"),
        new("55", "WI", "Wisconsin"),
        new("56", "WY", "Wyoming"),
        new("60", "AS", "American Samoa"),
        new("66", "GU", "Guam"),
        new("69", "MP", "Northern Mariana Islands"),
        new("72", "PR", "Puerto Rico"),
        new("78", "VI", "United States Virgin Islands"),
    ];

    private static readonly Dictionary<string, UsState> _byFips =
        _all.ToDictionary(state => state.Fips, StringComparer.Ordinal);

    private static readonly Dictionary<string, UsState> _byAbbreviation =
        _all.ToDictionary(state => state.Abbreviation, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, UsState> _byName =
        _all.ToDictionary(state => state.Name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<UsState> All => _all;

    public static UsState? ByFips(string? fips) =>
        fips is not null && _byFips.TryGetValue(fips.Trim(), out var state) ? state : null;

    /// <summary>A state by USPS code or full name, which is how both a query and the CBSA file name one.</summary>
    public static UsState? Find(string? nameOrAbbreviation)
    {
        if (string.IsNullOrWhiteSpace(nameOrAbbreviation))
        {
            return null;
        }

        var trimmed = nameOrAbbreviation.Trim();

        return _byAbbreviation.TryGetValue(trimmed, out var byAbbreviation) ? byAbbreviation
            : _byName.TryGetValue(trimmed, out var byName) ? byName
            : null;
    }

    /// <summary>The USPS code for a <c>STATEFP</c>, falling back to the code itself for an unknown one.</summary>
    public static string AbbreviationForFips(string fips) => ByFips(fips)?.Abbreviation ?? fips;
}
