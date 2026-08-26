using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StoryVoice.Domain.ExternalVoices;
using StoryVoice.Infrastructure.Identity;

namespace StoryVoice.Infrastructure.Persistence;

internal sealed class ExternalVoiceCredentialAuditConfiguration
    : IEntityTypeConfiguration<ExternalVoiceCredentialAudit>
{
    public void Configure(EntityTypeBuilder<ExternalVoiceCredentialAudit> builder)
    {
        builder.ToTable("external_voice_credential_audits");
        builder.HasKey(audit => audit.Id);
        builder.Property(audit => audit.CredentialKeyId).HasMaxLength(64).IsRequired();
        builder.Property(audit => audit.Action).HasMaxLength(24).IsRequired();
        builder.Property(audit => audit.OccurredAtUtc).IsRequired();
        builder.Property(audit => audit.RelatedCredentialKeyId).HasMaxLength(64);
        builder.HasIndex(audit => new { audit.OwnerId, audit.OccurredAtUtc });
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(audit => audit.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ExternalVoiceCredential>()
            .WithMany()
            .HasForeignKey(audit => audit.CredentialId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ExternalVoiceCredential>()
            .WithMany()
            .HasForeignKey(audit => audit.RelatedCredentialId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
