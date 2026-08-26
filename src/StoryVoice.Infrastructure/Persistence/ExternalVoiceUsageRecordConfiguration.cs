using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StoryVoice.Domain.ExternalVoices;
using StoryVoice.Infrastructure.Identity;

namespace StoryVoice.Infrastructure.Persistence;

internal sealed class ExternalVoiceUsageRecordConfiguration
    : IEntityTypeConfiguration<ExternalVoiceUsageRecord>
{
    public void Configure(EntityTypeBuilder<ExternalVoiceUsageRecord> builder)
    {
        builder.ToTable("external_voice_usage_records", table =>
        {
            table.HasCheckConstraint(
                "CK_external_voice_usage_records_status",
                "\"StatusCode\" >= 100 AND \"StatusCode\" <= 599");
            table.HasCheckConstraint(
                "CK_external_voice_usage_records_metrics",
                "\"DurationMilliseconds\" >= 0 AND \"TextCharacters\" >= 0 "
                + "AND \"ResponseBytes\" >= 0 AND \"AudioDurationMilliseconds\" >= 0");
        });
        builder.HasKey(record => record.Id);
        builder.Property(record => record.ConsumerKeyId).HasMaxLength(64).IsRequired();
        builder.Property(record => record.CredentialKeyId).HasMaxLength(64).IsRequired();
        builder.Property(record => record.ProjectId).HasMaxLength(128).IsRequired();
        builder.Property(record => record.AccessTier).HasMaxLength(32).IsRequired();
        builder.Property(record => record.RequestId).HasMaxLength(32).IsRequired();
        builder.Property(record => record.VoiceAlias).HasMaxLength(64);
        builder.Property(record => record.OccurredAtUtc).IsRequired();
        builder.Property(record => record.Outcome).HasMaxLength(64).IsRequired();
        builder.HasIndex(record => record.RequestId).IsUnique();
        builder.HasIndex(record => new { record.OwnerId, record.OccurredAtUtc });
        builder.HasIndex(record => new
        {
            record.OwnerId,
            record.ProjectId,
            record.OccurredAtUtc,
        });
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(record => record.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
