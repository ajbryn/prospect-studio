using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectStudio.Core.Domain;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// Maps <see cref="Research"/> to the <c>research</c> table from technical-design §5.2, keyed on
/// (<c>campaign_id</c>, <c>lead_id</c>): <c>save_research</c> replaces the document rather than
/// appending, so two rows for one lead would make "the" research ambiguous and a re-score
/// non-deterministic.
/// </summary>
public sealed class ResearchConfiguration : IEntityTypeConfiguration<Research>
{
    public void Configure(EntityTypeBuilder<Research> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("research");
        builder.HasKey(research => new { research.CampaignId, research.LeadId });

        builder.Property(research => research.CampaignId).HasColumnName("campaign_id").HasMaxLength(16);
        builder.Property(research => research.LeadId).HasColumnName("lead_id").HasMaxLength(16);

        // The whole validated document, opaque TEXT and never filtered inside (CLAUDE.md).
        builder.Property(research => research.ResearchJson).HasColumnName("research_json").IsRequired();

        // Denormalized out of the document precisely so scoring reads a column instead of reaching
        // inside the JSON. save_research writes both together; they must always agree.
        builder.Property(research => research.LlmAdjustment).HasColumnName("llm_adjustment");
        builder.Property(research => research.SavedAt).HasColumnName("saved_at");

        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(research => new { research.CampaignId, research.LeadId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Maps <see cref="Signal"/> to the <c>signals</c> table from technical-design §5.2: the cited signals
/// of a research document, extracted into rows so §7.6's <c>signals</c> feature and
/// <c>list_leads</c>' <c>topSignal</c> read columns rather than JSON.
/// </summary>
public sealed class SignalConfiguration : IEntityTypeConfiguration<Signal>
{
    public void Configure(EntityTypeBuilder<Signal> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("signals");
        builder.HasKey(signal => signal.Id);

        builder.Property(signal => signal.Id).HasColumnName("id").HasMaxLength(32);
        builder.Property(signal => signal.CampaignId).HasColumnName("campaign_id").HasMaxLength(16).IsRequired();
        builder.Property(signal => signal.LeadId).HasColumnName("lead_id").HasMaxLength(16).IsRequired();
        builder.Property(signal => signal.Type).HasColumnName("type").HasMaxLength(20).IsRequired();
        builder.Property(signal => signal.Text).HasColumnName("text").HasMaxLength(240).IsRequired();
        builder.Property(signal => signal.Url).HasColumnName("url").HasMaxLength(2000).IsRequired();

        // TEXT, and deliberately not a date: poc/schemas/research.schema.json admits YYYY-MM as well as
        // YYYY-MM-DD, and no date type holds a month without inventing a day. §7.6's twelve-month window
        // is therefore applied in the scorer at whole-month granularity, never as a SQL comparison on
        // this column (see the C6 decisions note).
        builder.Property(signal => signal.Date).HasColumnName("date").HasMaxLength(10).IsRequired();

        builder.HasIndex(signal => new { signal.CampaignId, signal.LeadId })
            .HasDatabaseName("ix_signals_campaign_lead");

        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(signal => new { signal.CampaignId, signal.LeadId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
