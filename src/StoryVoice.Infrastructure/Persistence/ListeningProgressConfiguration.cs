using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StoryVoice.Domain.Narrations;

namespace StoryVoice.Infrastructure.Persistence;

internal sealed class ListeningProgressConfiguration : IEntityTypeConfiguration<ListeningProgress>
{
    public void Configure(EntityTypeBuilder<ListeningProgress> builder)
    {
        builder.ToTable("listening_progress", table => table.HasCheckConstraint(
            "CK_listening_progress_position",
            "\"PositionMs\" >= 0 AND \"PositionMs\" <= \"DurationMs\" " +
            $"AND \"DurationMs\" > 0 AND \"DurationMs\" <= {ListeningProgress.MaximumDurationMs}"));
        builder.HasKey(progress => new { progress.OwnerId, progress.NarrationJobId });
        builder.Property(progress => progress.Version).IsConcurrencyToken();
        builder.HasOne<NarrationJob>().WithMany()
            .HasForeignKey(progress => progress.NarrationJobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
