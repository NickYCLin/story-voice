using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StoryVoice.Domain.Narrations;
using StoryVoice.Domain.Series;

namespace StoryVoice.Infrastructure.Persistence;

internal sealed class SeriesCastRebuildBatchConfiguration : IEntityTypeConfiguration<SeriesCastRebuildBatch>
{
    public void Configure(EntityTypeBuilder<SeriesCastRebuildBatch> builder)
    {
        builder.ToTable("series_cast_rebuild_batches", table =>
        {
            table.HasCheckConstraint(
                "CK_rebuild_batches_cohort",
                "\"CohortMembershipRevision\" >= 1");
            table.HasCheckConstraint(
                "CK_rebuild_batches_revisions",
                "\"BaseActiveCastRevisionId\" IS NULL OR \"BaseActiveCastRevisionId\" <> \"DraftCastRevisionId\"");
            table.HasCheckConstraint(
                "CK_rebuild_batches_status",
                "\"Status\" IN ('Draft', 'Building', 'ReadyToActivate', 'Activated', 'Failed')");
            table.HasCheckConstraint(
                "CK_rebuild_batches_chronology",
                "\"UpdatedAt\" >= \"CreatedAt\"");
        });

        builder.HasKey(batch => batch.Id);
        builder.Property(batch => batch.Id).ValueGeneratedNever();
        builder.HasAlternateKey(batch => new { batch.OwnerId, batch.SeriesId, batch.Id })
            .HasName("AK_rebuild_batches_scope");
        builder.HasAlternateKey(batch => new
        {
            batch.OwnerId,
            batch.SeriesId,
            batch.Id,
            batch.DraftCastRevisionId
        })
            .HasName("AK_rebuild_batches_job_cast");
        builder.Property(batch => batch.CohortMembershipRevision);
        builder.Property(batch => batch.CreatedAt);
        builder.Property(batch => batch.Status)
            .HasField("_status")
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired()
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Property(batch => batch.UpdatedAt)
            .HasField("_updatedAt")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(batch => new
        {
            batch.OwnerId,
            batch.SeriesId,
            batch.BaseActiveCastRevisionId
        })
            .HasDatabaseName("IX_rebuild_batches_base_cast");
        builder.HasIndex(batch => new
        {
            batch.OwnerId,
            batch.SeriesId,
            batch.DraftCastRevisionId
        })
            .HasDatabaseName("UX_rebuild_batches_draft_cast")
            .IsUnique()
            .HasFilter("\"Status\" <> 'Failed'");

        builder.HasOne<StorySeries>()
            .WithMany()
            .HasForeignKey(batch => new { batch.OwnerId, batch.SeriesId })
            .HasPrincipalKey(series => new { series.OwnerId, series.Id })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_rebuild_batches_series_scope");
        builder.HasOne<NarrationCastRevision>()
            .WithMany()
            .HasForeignKey(batch => new
            {
                batch.OwnerId,
                batch.SeriesId,
                batch.DraftCastRevisionId
            })
            .HasPrincipalKey(revision => new { revision.OwnerId, revision.SeriesId, revision.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_rebuild_batches_draft_cast");
        builder.HasOne<NarrationCastRevision>()
            .WithMany()
            .HasForeignKey(batch => new
            {
                batch.OwnerId,
                batch.SeriesId,
                batch.BaseActiveCastRevisionId
            })
            .HasPrincipalKey(revision => new { revision.OwnerId, revision.SeriesId, revision.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_rebuild_batches_base_cast");
        builder.HasMany(batch => batch.Members)
            .WithOne()
            .HasForeignKey(member => new { member.OwnerId, member.SeriesId, member.BatchId })
            .HasPrincipalKey(batch => new { batch.OwnerId, batch.SeriesId, batch.Id })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_rebuild_members_batch_scope");
        builder.Navigation(batch => batch.Members)
            .HasField("_members")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
