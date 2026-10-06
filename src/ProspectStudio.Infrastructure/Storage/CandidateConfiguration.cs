using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectStudio.Core.Domain;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// Maps <see cref="Company"/> to the <c>companies</c> table from technical-design §5.2.
/// </summary>
public sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("companies");
        builder.HasKey(company => company.Id);

        builder.Property(company => company.Id).HasColumnName("id").HasMaxLength(32);
        builder.Property(company => company.Name).HasColumnName("name").HasMaxLength(300).IsRequired();
        builder.Property(company => company.NameNorm).HasColumnName("name_norm").HasMaxLength(300).IsRequired();
        builder.Property(company => company.Domain).HasColumnName("domain").HasMaxLength(255);

        // All name matching happens on the normalized column (CLAUDE.md §Conventions), so that is the
        // one worth an index; dedupe, suppression and matchback all read it.
        builder.HasIndex(company => company.NameNorm).HasDatabaseName("ix_companies_name_norm");
        builder.HasIndex(company => company.Domain).HasDatabaseName("ix_companies_domain");
    }
}

/// <summary>
/// Maps <see cref="Site"/> to the <c>sites</c> table from technical-design §5.2. The unique
/// <c>overture_id</c> is what makes a <c>find_candidates</c> re-run idempotent (NFR-3).
/// </summary>
public sealed class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("sites");
        builder.HasKey(site => site.Id);

        builder.Property(site => site.Id).HasColumnName("id").HasMaxLength(32);
        builder.Property(site => site.CompanyId).HasColumnName("company_id").HasMaxLength(32).IsRequired();
        builder.Property(site => site.OvertureId).HasColumnName("overture_id").HasMaxLength(64).IsRequired();
        builder.Property(site => site.Name).HasColumnName("name").HasMaxLength(300).IsRequired();
        builder.Property(site => site.Address).HasColumnName("address").HasMaxLength(300);
        builder.Property(site => site.City).HasColumnName("city").HasMaxLength(120);
        builder.Property(site => site.State).HasColumnName("state").HasMaxLength(2);
        builder.Property(site => site.Zip).HasColumnName("zip").HasMaxLength(5);
        builder.Property(site => site.CountyFips).HasColumnName("county_fips").HasMaxLength(5).IsRequired();
        builder.Property(site => site.Lat).HasColumnName("lat");
        builder.Property(site => site.Lon).HasColumnName("lon");
        builder.Property(site => site.Phone).HasColumnName("phone").HasMaxLength(40);
        builder.Property(site => site.Website).HasColumnName("website").HasMaxLength(500);
        builder.Property(site => site.TaxonomyPrimary).HasColumnName("taxonomy_primary").HasMaxLength(120);
        builder.Property(site => site.TaxonomyPath).HasColumnName("taxonomy_path").HasMaxLength(500);
        builder.Property(site => site.BasicCategory).HasColumnName("basic_category").HasMaxLength(120);
        builder.Property(site => site.Confidence).HasColumnName("confidence");
        builder.Property(site => site.Release).HasColumnName("release").HasMaxLength(32).IsRequired();

        builder.HasIndex(site => site.OvertureId).IsUnique().HasDatabaseName("ix_sites_overture_id");
        builder.HasIndex(site => site.CountyFips).HasDatabaseName("ix_sites_county_fips");

        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(site => site.CompanyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Maps <see cref="SourceRecord"/> to the <c>source_records</c> table from technical-design §5.2. The
/// payload stays opaque TEXT (CLAUDE.md): it is never filtered inside.
/// </summary>
public sealed class SourceRecordConfiguration : IEntityTypeConfiguration<SourceRecord>
{
    public void Configure(EntityTypeBuilder<SourceRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("source_records");
        builder.HasKey(record => record.Id);

        builder.Property(record => record.Id).HasColumnName("id").HasMaxLength(32);
        builder.Property(record => record.SiteId).HasColumnName("site_id").HasMaxLength(32).IsRequired();
        builder.Property(record => record.Source).HasColumnName("source").HasMaxLength(40).IsRequired();
        builder.Property(record => record.SourceId).HasColumnName("source_id").HasMaxLength(64).IsRequired();
        builder.Property(record => record.RetrievedAt).HasColumnName("retrieved_at");
        builder.Property(record => record.License).HasColumnName("license").HasMaxLength(120);
        builder.Property(record => record.PayloadJson).HasColumnName("payload_json");

        builder.HasIndex(record => new { record.SiteId, record.Source })
            .HasDatabaseName("ix_source_records_site");

        builder.HasOne<Site>()
            .WithMany()
            .HasForeignKey(record => record.SiteId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Maps <see cref="Lead"/> to the <c>leads</c> table from technical-design §5.2, with the composite
/// key (<c>campaign_id</c>, <c>id</c>) §5.3 requires: lead ids are <c>L0001</c> per campaign, so
/// <c>id</c> alone is not unique.
/// </summary>
public sealed class LeadConfiguration : IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("leads");
        builder.HasKey(lead => new { lead.CampaignId, lead.Id });

        builder.Property(lead => lead.CampaignId).HasColumnName("campaign_id").HasMaxLength(16);
        builder.Property(lead => lead.Id).HasColumnName("id").HasMaxLength(16);
        builder.Property(lead => lead.SiteId).HasColumnName("site_id").HasMaxLength(32).IsRequired();
        builder.Property(lead => lead.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(lead => lead.SuppressionReason).HasColumnName("suppression_reason").HasMaxLength(20);

        // §7.3 stores the matching row id as well as the reason. It is a plain column, not a foreign
        // key: a re-imported list is upserted, so a lead must keep pointing at the row that matched it
        // even if that row is later edited out of the file.
        builder.Property(lead => lead.SuppressionId).HasColumnName("suppression_id").HasMaxLength(32);

        // What releasing a lead from suppression restores it to, so an approval survives the round trip.
        builder.Property(lead => lead.PreSuppressionStatus)
            .HasColumnName("pre_suppression_status")
            .HasMaxLength(20);
        builder.Property(lead => lead.DealerId).HasColumnName("dealer_id").HasMaxLength(32);
        builder.Property(lead => lead.BranchId).HasColumnName("branch_id").HasMaxLength(32);
        builder.Property(lead => lead.Assignment).HasColumnName("assignment").HasMaxLength(10);
        builder.Property(lead => lead.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(lead => new { lead.CampaignId, lead.SiteId })
            .IsUnique()
            .HasDatabaseName("ix_leads_campaign_site");
        builder.HasIndex(lead => new { lead.CampaignId, lead.Status })
            .HasDatabaseName("ix_leads_campaign_status");

        builder.HasOne<Campaign>()
            .WithMany()
            .HasForeignKey(lead => lead.CampaignId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Site>()
            .WithMany()
            .HasForeignKey(lead => lead.SiteId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
