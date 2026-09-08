using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StoryVoice.Domain.Narrations;

namespace StoryVoice.Infrastructure.Persistence;

internal sealed class NarrationAttemptUsageConfiguration : IEntityTypeConfiguration<NarrationAttemptUsage>
{
    public void Configure(EntityTypeBuilder<NarrationAttemptUsage> builder)
    {
        builder.ToTable("narration_attempt_usage", table =>
        {
            table.HasCheckConstraint("CK_narration_usage_outcome",
                "\"Outcome\" IN ('Running', 'Completed', 'Failed', 'TimedOut', 'Cancelled', 'WorkerStopped', 'LeaseLost', 'Unknown')");
            table.HasCheckConstraint("CK_narration_usage_quantities",
                "(\"InputCharacters\" IS NULL OR \"InputCharacters\" >= 0) AND " +
                "(\"ElapsedMs\" IS NULL OR \"ElapsedMs\" >= 0) AND " +
                "(\"SynthesisElapsedMs\" IS NULL OR \"SynthesisElapsedMs\" >= 0) AND " +
                "(\"AudioBytes\" IS NULL OR \"AudioBytes\" > 0) AND " +
                "((\"CompletedChunks\" IS NULL AND \"TotalChunks\" IS NULL) OR " +
                "(\"CompletedChunks\" IS NOT NULL AND \"TotalChunks\" IS NOT NULL AND " +
                "\"CompletedChunks\" >= 0 AND \"TotalChunks\" > 0 AND \"CompletedChunks\" <= \"TotalChunks\"))");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.LeaseOwner).HasMaxLength(200).IsRequired();
        builder.Property(item => item.Outcome).HasMaxLength(30).IsRequired();
        builder.Property(item => item.Provider).HasMaxLength(30);
        builder.HasIndex(item => new { item.OwnerId, item.NarrationJobId, item.StartedAt });
        builder.HasOne<NarrationJob>().WithMany()
            .HasForeignKey(item => new { item.OwnerId, item.NarrationJobId })
            .HasPrincipalKey(job => new { job.OwnerId, job.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
