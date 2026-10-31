using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StoryVoice.Domain.Narrations;

namespace StoryVoice.Infrastructure.Persistence;

internal sealed class NarrationTrackSelectionConfiguration : IEntityTypeConfiguration<NarrationTrackSelection>
{
    public void Configure(EntityTypeBuilder<NarrationTrackSelection> builder)
    {
        builder.ToTable("narration_track_selections", table => table.HasCheckConstraint("CK_track_selection_revision", "\"Revision\" > 0"));
        builder.HasKey(selection => selection.Id);
        builder.HasIndex(selection => new { selection.OwnerId, selection.BookId, selection.Revision }).IsUnique();
        builder.HasOne<NarrationJob>().WithMany()
            .HasForeignKey(selection => new { selection.OwnerId, selection.NarrationJobId })
            .HasPrincipalKey(job => new { job.OwnerId, job.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
