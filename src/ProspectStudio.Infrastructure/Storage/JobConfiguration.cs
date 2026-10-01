using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProspectStudio.Core.Domain;

namespace ProspectStudio.Infrastructure.Storage;

/// <summary>
/// Maps <see cref="Job"/> to the <c>jobs</c> table from technical-design §5.2, plus the
/// <c>created_at</c> that table's column list omits: <c>list_jobs</c> is newest first, and a queued
/// job has no <c>started_at</c> to order by. No provider specific types.
/// </summary>
public sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("jobs");
        builder.HasKey(job => job.Id);

        builder.Property(job => job.Id).HasColumnName("id").HasMaxLength(16);
        builder.Property(job => job.Kind).HasColumnName("kind").HasMaxLength(40).IsRequired();
        builder.Property(job => job.CampaignId).HasColumnName("campaign_id").HasMaxLength(16);
        builder.Property(job => job.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(job => job.Progress).HasColumnName("progress");
        builder.Property(job => job.Message).HasColumnName("message").HasMaxLength(1000);
        builder.Property(job => job.ParametersJson).HasColumnName("params_json");
        builder.Property(job => job.ResultJson).HasColumnName("result_json");
        builder.Property(job => job.CreatedAt).HasColumnName("created_at");
        builder.Property(job => job.StartedAt).HasColumnName("started_at");
        builder.Property(job => job.FinishedAt).HasColumnName("finished_at");

        builder.HasIndex(job => job.CreatedAt).HasDatabaseName("ix_jobs_created_at");
        builder.HasIndex(job => new { job.CampaignId, job.Status }).HasDatabaseName("ix_jobs_campaign_status");
    }
}
