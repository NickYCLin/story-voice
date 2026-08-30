using Microsoft.Extensions.Options;
using StoryVoice.Infrastructure.ExternalVoices;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Infrastructure.VoiceCatalog;

namespace StoryVoice.UnitTests;

public sealed class ExternalVoiceApiOptionsValidatorTests
{
    [Theory]
    [InlineData(0, 600)]
    [InlineData(601, 601)]
    [InlineData(60, 59)]
    [InlineData(60, 6001)]
    public void Pre_authentication_limits_must_remain_bounded_and_global_must_cover_source(
        int sourceLimit,
        int globalLimit)
    {
        var options = CreateOptions(
            ("consumer_one_01", Guid.NewGuid(), "bounded-pre-auth-project"));
        options.PreAuthenticationRequestsPerMinute = sourceLimit;
        options.PreAuthenticationGlobalRequestsPerMinute = globalLimit;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains("pre-authentication", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ExternalVoiceApiOptions.MaximumUsageLedgerQueueCapacity + 1)]
    public void Usage_ledger_queue_capacity_must_remain_bounded(int capacity)
    {
        var options = CreateOptions(
            ("consumer_one_01", Guid.NewGuid(), "bounded-project"));
        options.UsageLedgerQueueCapacity = capacity;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains("usage ledger queue", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_project_reference_for_the_same_owner_is_rejected()
    {
        var ownerId = Guid.NewGuid();
        var options = CreateOptions(
            ("consumer_one_01", ownerId, "shared-project"),
            ("consumer_two_01", ownerId, "shared-project"));

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains(
                "duplicates owner-scoped project reference 'shared-project'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Project_reference_must_not_collide_with_another_consumer_key_for_the_same_owner()
    {
        var ownerId = Guid.NewGuid();
        var options = CreateOptions(
            ("consumer_one_01", ownerId, "consumer-two-project"),
            ("consumer_two_01", ownerId, "consumer_one_01"));

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains(
                "duplicates owner-scoped project reference 'consumer_one_01'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Different_owners_may_use_the_same_project_id()
    {
        var options = CreateOptions(
            ("consumer_one_01", Guid.NewGuid(), "shared-project"),
            ("consumer_two_01", Guid.NewGuid(), "shared-project"));

        var result = CreateValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    private static ExternalVoiceApiOptionsValidator CreateValidator() =>
        new(
            Options.Create(new LocalClonePreviewOptions()),
            Options.Create(new VoiceCatalogOptions()));

    private static ExternalVoiceApiOptions CreateOptions(
        params (string ConsumerKeyId, Guid OwnerId, string ProjectId)[] consumers)
    {
        var effectiveAtUtc = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        return new ExternalVoiceApiOptions
        {
            Consumers = consumers.ToDictionary(
                consumer => consumer.ConsumerKeyId,
                consumer => new ExternalVoiceConsumerOptions
                {
                    AccessTier = ExternalVoiceAccessTiers.PrivateDevelopment,
                    DisplayName = consumer.ConsumerKeyId,
                    ProjectId = consumer.ProjectId,
                    OwnerId = consumer.OwnerId,
                    TokenSha256 = new string('a', 64),
                    EffectiveAtUtc = effectiveAtUtc,
                    ExpiresAtUtc = effectiveAtUtc.AddDays(30),
                    AllowedVoices = new Dictionary<string, ExternalVoiceGrantOptions>(
                        StringComparer.Ordinal)
                    {
                        ["private-synthetic-voice"] = new ExternalVoiceGrantOptions
                        {
                            AuthorizationEvidenceRelativePath = "evidence/grant.json",
                            AuthorizationEvidenceSha256 = new string('b', 64),
                        },
                    },
                },
                StringComparer.Ordinal),
        };
    }
}
