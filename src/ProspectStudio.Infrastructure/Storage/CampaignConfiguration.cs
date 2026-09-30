using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectStudio.Core.Domain;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// Maps <see cref="Campaign"/> to the <c>campaigns</c> table from technical-design §5.2. No provider
/// specific types: the model has to survive a move to SQL Server or PostgreSQL.
/// </summary>
public sealed class CampaignConfiguration : IEntityTypeConfiguration<Campaign>
{
    public void Configure(EntityTypeBuilder<Campaign> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("campaigns");
        builder.HasKey(campaign => campaign.Id);

        builder.Property(campaign => campaign.Id).HasColumnName("id").HasMaxLength(16);
        builder.Property(campaign => campaign.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(campaign => campaign.Slug).HasColumnName("slug").HasMaxLength(200).IsRequired();
        builder.Property(campaign => campaign.Product).HasColumnName("product").HasMaxLength(200);
        builder.Property(campaign => campaign.Notes).HasColumnName("notes").HasMaxLength(4000);
        builder.Property(campaign => campaign.FolderPath).HasColumnName("folder_path").HasMaxLength(500).IsRequired();
        builder.Property(campaign => campaign.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(campaign => campaign.ProfileJson).HasColumnName("profile_json");
        builder.Property(campaign => campaign.ProfileSavedAt).HasColumnName("profile_saved_at");
        builder.Property(campaign => campaign.GeoJson).HasColumnName("geo_json");
        builder.Property(campaign => campaign.CreatedAt).HasColumnName("created_at");
        builder.Property(campaign => campaign.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(campaign => campaign.Slug).IsUnique().HasDatabaseName("ix_campaigns_slug");
    }
}
