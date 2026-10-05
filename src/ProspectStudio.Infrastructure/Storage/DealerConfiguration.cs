using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectStudio.Core.Domain;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// Maps <see cref="Dealer"/> to the <c>dealers</c> table from technical-design §5.2.
/// </summary>
public sealed class DealerConfiguration : IEntityTypeConfiguration<Dealer>
{
    public void Configure(EntityTypeBuilder<Dealer> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("dealers");
        builder.HasKey(dealer => dealer.Id);

        builder.Property(dealer => dealer.Id).HasColumnName("id").HasMaxLength(32);
        builder.Property(dealer => dealer.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(dealer => dealer.Website).HasColumnName("website").HasMaxLength(500);
        builder.Property(dealer => dealer.AlertEmail).HasColumnName("alert_email").HasMaxLength(255);
        builder.Property(dealer => dealer.LogoFile).HasColumnName("logo_file").HasMaxLength(500);
    }
}

/// <summary>
/// Maps <see cref="DealerBranch"/> to the <c>dealer_branches</c> table from technical-design §5.2. The
/// coordinates are what §7.4's nearest-branch tie-break measures against.
/// </summary>
public sealed class DealerBranchConfiguration : IEntityTypeConfiguration<DealerBranch>
{
    public void Configure(EntityTypeBuilder<DealerBranch> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("dealer_branches");
        builder.HasKey(branch => branch.Id);

        builder.Property(branch => branch.Id).HasColumnName("id").HasMaxLength(32);
        builder.Property(branch => branch.DealerId).HasColumnName("dealer_id").HasMaxLength(32).IsRequired();
        builder.Property(branch => branch.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(branch => branch.Address).HasColumnName("address").HasMaxLength(300);
        builder.Property(branch => branch.City).HasColumnName("city").HasMaxLength(120);
        builder.Property(branch => branch.State).HasColumnName("state").HasMaxLength(2);
        builder.Property(branch => branch.Zip).HasColumnName("zip").HasMaxLength(5);
        builder.Property(branch => branch.Lat).HasColumnName("lat");
        builder.Property(branch => branch.Lon).HasColumnName("lon");
        builder.Property(branch => branch.Phone).HasColumnName("phone").HasMaxLength(40);
        builder.Property(branch => branch.TrackingPhone).HasColumnName("tracking_phone").HasMaxLength(40);

        // An orphan branch would route leads to a dealer who does not exist.
        builder.HasOne<Dealer>()
            .WithMany()
            .HasForeignKey(branch => branch.DealerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Maps <see cref="Territory"/> to the <c>territories</c> table from technical-design §5.2.
/// </summary>
/// <remarks>
/// <c>branch_id</c> is a plain column rather than a foreign key: §7.4 treats a rule whose branch is
/// missing as a data problem that still assigns the dealer, so a mistyped branch must not stop the
/// other rows of the file from importing.
/// </remarks>
public sealed class TerritoryConfiguration : IEntityTypeConfiguration<Territory>
{
    public void Configure(EntityTypeBuilder<Territory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("territories");
        builder.HasKey(territory => territory.Id);

        builder.Property(territory => territory.Id).HasColumnName("id").HasMaxLength(32);
        builder.Property(territory => territory.DealerId).HasColumnName("dealer_id").HasMaxLength(32).IsRequired();
        builder.Property(territory => territory.BranchId).HasColumnName("branch_id").HasMaxLength(32);
        builder.Property(territory => territory.Level).HasColumnName("level").HasMaxLength(10).IsRequired();
        builder.Property(territory => territory.Code).HasColumnName("code").HasMaxLength(5).IsRequired();
        builder.Property(territory => territory.Priority).HasColumnName("priority");

        // §7.4 looks rules up by level and code for every lead in the campaign.
        builder.HasIndex(territory => new { territory.Level, territory.Code })
            .HasDatabaseName("ix_territories_level_code");

        builder.HasOne<Dealer>()
            .WithMany()
            .HasForeignKey(territory => territory.DealerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Maps <see cref="SuppressionRow"/> to the <c>suppression</c> table from technical-design §5.2. §7.3
/// compares <c>name_norm</c> and the registrable <c>domain</c>, never the raw name, which is also what
/// keeps the matching off the provider's text comparison (CLAUDE.md).
/// </summary>
public sealed class SuppressionRowConfiguration : IEntityTypeConfiguration<SuppressionRow>
{
    public void Configure(EntityTypeBuilder<SuppressionRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("suppression");
        builder.HasKey(row => row.Id);

        builder.Property(row => row.Id).HasColumnName("id").HasMaxLength(32);
        builder.Property(row => row.CompanyName).HasColumnName("company_name").HasMaxLength(300).IsRequired();
        builder.Property(row => row.NameNorm).HasColumnName("name_norm").HasMaxLength(300).IsRequired();
        builder.Property(row => row.Domain).HasColumnName("domain").HasMaxLength(255);
        builder.Property(row => row.AddressNorm).HasColumnName("address_norm").HasMaxLength(300);
        builder.Property(row => row.Zip).HasColumnName("zip").HasMaxLength(5);
        builder.Property(row => row.Reason).HasColumnName("reason").HasMaxLength(20).IsRequired();
        builder.Property(row => row.SourceFile).HasColumnName("source_file").HasMaxLength(500);

        builder.HasIndex(row => row.NameNorm).HasDatabaseName("ix_suppression_name_norm");
        builder.HasIndex(row => row.Domain).HasDatabaseName("ix_suppression_domain");
    }
}
