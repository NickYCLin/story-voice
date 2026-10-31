using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StoryVoice.Domain.Narrations;

namespace StoryVoice.Infrastructure.Persistence;

internal sealed class NarrationTimelineConfiguration : IEntityTypeConfiguration<NarrationTimeline>
{
    public void Configure(EntityTypeBuilder<NarrationTimeline> builder)
    {
        builder.ToTable("narration_timelines");
        builder.HasKey(timeline => timeline.NarrationJobId);
        builder.Property(timeline => timeline.NarrationJobId).ValueGeneratedNever();
        builder.Property(timeline => timeline.TimelineJson)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.HasIndex(timeline => new { timeline.OwnerId, timeline.NarrationJobId })
            .HasDatabaseName("IX_narration_timelines_owner_job");
        builder.HasOne<NarrationJob>()
            .WithMany()
            .HasForeignKey(timeline => timeline.NarrationJobId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_narration_timelines_job");
    }
}
