namespace ProspectStudio.Core.Domain;

/// <summary>
/// The research document saved for one lead (technical-design §5.2, table <c>research</c>).
/// Persistence-ignorant - the mapping lives in an <c>IEntityTypeConfiguration&lt;Research&gt;</c> in
/// Infrastructure/Storage.
/// </summary>
public sealed class Research
{
    /// <summary>Half of the composite key, with <see cref="LeadId"/>: one document per lead.</summary>
    public required string CampaignId { get; set; }

    public required string LeadId { get; set; }

    /// <summary>
    /// The whole validated document as opaque TEXT, never filtered inside (CLAUDE.md). It is the
    /// source of truth; <see cref="LlmAdjustment"/> is a copy of one of its fields.
    /// </summary>
    public required string ResearchJson { get; set; }

    /// <summary>
    /// The document's <c>llmAdjustment</c>, denormalized into a column so scoring never has to read
    /// inside the JSON. §5.2 lists it, and the two must always agree.
    /// </summary>
    public int LlmAdjustment { get; set; }

    public DateTimeOffset SavedAt { get; set; }
}

/// <summary>
/// One cited signal from a lead's research (technical-design §5.2, table <c>signals</c>), extracted out
/// of the document so §7.6's <c>signals</c> feature and <c>list_leads</c>' <c>topSignal</c> can read it
/// as rows rather than JSON.
/// </summary>
public sealed class Signal
{
    public required string Id { get; set; }

    public required string CampaignId { get; set; }

    public required string LeadId { get; set; }

    /// <summary>One of <see cref="Leads.SignalTypes"/>.</summary>
    public required string Type { get; set; }

    public required string Text { get; set; }

    public required string Url { get; set; }

    /// <summary>
    /// <c>YYYY-MM</c> or <c>YYYY-MM-DD</c>, kept as TEXT. This is a deliberate exception to
    /// CLAUDE.md's "let EF map <c>DateTimeOffset</c>" rule rather than a mapping to tighten later:
    /// <c>schemas/research.schema.json</c> admits a month without a day, and no date type can hold that
    /// without inventing one. §7.6's 12-month window is therefore applied in the scorer at whole-month
    /// granularity, never as a SQL string comparison.
    /// </summary>
    public required string Date { get; set; }
}
