using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StoryVoice.Domain.ExternalVoices;
using StoryVoice.Infrastructure.Identity;

namespace StoryVoice.Infrastructure.Persistence;

internal sealed class ExternalVoiceCredentialConfiguration
    : IEntityTypeConfiguration<ExternalVoiceCredential>
{
    public void Configure(EntityTypeBuilder<ExternalVoiceCredential> builder)
    {
        builder.ToTable("external_voice_credentials", table =>
        {
            table.HasCheckConstraint(
                "CK_external_voice_credentials_token_sha256",
                "length(\"TokenSha256\") = 64");
            table.HasCheckConstraint(
                "CK_external_voice_credentials_expiry",
                "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
        });
        builder.HasKey(credential => credential.Id);
        builder.Property(credential => credential.ConsumerKeyId).HasMaxLength(64).IsRequired();
        builder.Property(credential => credential.KeyId).HasMaxLength(64).IsRequired();
        builder.Property(credential => credential.Name).HasMaxLength(80).IsRequired();
        builder.Property(credential => credential.TokenSha256).HasMaxLength(64).IsRequired();
        builder.Property(credential => credential.CreatedAtUtc).IsRequired();
        builder.Property(credential => credential.ExpiresAtUtc).IsRequired();
        builder.HasIndex(credential => credential.KeyId).IsUnique();
        builder.HasIndex(credential => new
        {
            credential.OwnerId,
            credential.ConsumerKeyId,
            credential.RevokedAtUtc,
        });
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(credential => credential.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ExternalVoiceCredential>()
            .WithMany()
            .HasForeignKey(credential => credential.ReplacedByCredentialId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
