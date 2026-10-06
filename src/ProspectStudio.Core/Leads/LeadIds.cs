namespace ProspectStudio.Core.Leads;

/// <summary>
/// The ids chunk C6 generates. Technical-design §5.3: string ids are generated in Core, not by the
/// database, so the desktop app and the MCP server mint them the same way.
/// </summary>
public static class LeadIds
{
    public const string SignalPrefix = "sg_";

    /// <summary>
    /// A <c>signals</c> row id. Random rather than derived from the lead and the signal's position:
    /// <c>save_research</c> replaces a lead's signals wholesale, and a positional id would let a stale
    /// row be mistaken for a current one.
    /// </summary>
    public static string Signal() => SignalPrefix + Guid.NewGuid().ToString("N")[..16];
}
