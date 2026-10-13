using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryVoice.Application.Characters;
using StoryVoice.Application.Series;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed class ManagedNarrationVoiceApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Catalog_is_owner_scoped_and_create_rejects_catalog_fallbacks_foreign_and_unauthorized_profiles()
    {
        var ct = TestContext.Current.CancellationToken;
        using var enabled = factory.WithWebHostBuilder(b => b.UseSetting("LocalClonePreview:FormalNarrationEnabled", "true").UseSetting("LocalClonePreview:InternalToken", new string('t', 32)));
        using var owner = await enabled.CreateAuthenticatedClientAsync(ct);
        using var other = await enabled.CreateAuthenticatedClientAsync(ct);
        var ready = await SeedAsync(enabled, owner, true, ct);
        var forbidden = await SeedAsync(enabled, owner, false, ct);
        var foreign = await SeedAsync(enabled, other, true, ct);
        var options = await owner.GetFromJsonAsync<SeriesVoiceOptionResponse[]>("/api/series/voice-options", ct);
        var option = Assert.Single(options!);
        Assert.Equal("local-clone", option.Provider);
        Assert.Equal(ManagedNarrationVoices.Reference(ready), option.Voice);
        Assert.NotNull(option.CharacterProfileId);
        foreach (var (provider, voice) in new[] {
            ("edge", "zh-TW-YunJheNeural"), ("local-clone", "local-clone-narrator"),
            ("local-clone", ManagedNarrationVoices.Reference(foreign)),
            ("local-clone", ManagedNarrationVoices.Reference(forbidden)) })
        {
            using var response = await owner.PostWithCsrfAsync("/api/series", Body(provider, voice), ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var success = await owner.PostWithCsrfAsync("/api/series", Body("local-clone", option.Voice), ct);
        Assert.Equal(HttpStatusCode.Created, success.StatusCode);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("inactive")]
    [InlineData("pending")]
    public async Task Unavailable_character_voice_is_rejected_even_after_it_was_selected(string condition)
    {
        var ct = TestContext.Current.CancellationToken;
        using var enabled = factory.WithWebHostBuilder(b => b.UseSetting("LocalClonePreview:FormalNarrationEnabled", "true").UseSetting("LocalClonePreview:InternalToken", new string('t', 32)));
        using var owner = await enabled.CreateAuthenticatedClientAsync(ct);
        var id = await SeedAsync(enabled, owner, true, ct);
        await using var scope = enabled.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var voice = await db.CharacterVoiceProfiles.SingleAsync(p => p.Id == id, ct);
        if (condition == "removed") db.CharacterVoiceProfiles.Remove(voice);
        if (condition == "inactive") (await db.CharacterProfiles.SingleAsync(c => c.Id == voice.CharacterProfileId, ct)).Deactivate(DateTimeOffset.UtcNow);
        if (condition == "pending") db.Entry(voice).Property(p => p.Status).CurrentValue = CharacterVoiceProfileStatus.Pending;
        await db.SaveChangesAsync(ct);
        using var response = await owner.PostWithCsrfAsync("/api/series", Body("local-clone", ManagedNarrationVoices.Reference(id)), ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static object Body(string provider, string voice) => new {
        name = "管理聲線測試", narratorProvider = provider, narratorVoice = voice,
        narratorRate = "+0%", narratorPitch = "+0Hz", narratorVolume = "+0%", defaultSpeakerPauseMs = 350
    };

    internal static async Task<Guid> SeedAsync(WebApplicationFactory<Program> app, HttpClient client, bool formal, CancellationToken ct)
    {
        using var response = await client.PostWithCsrfAsync("/api/character-profiles", new { canonicalName = $"學生-{Guid.NewGuid():N}" }, ct);
        response.EnsureSuccessStatusCode();
        var character = (await response.Content.ReadFromJsonAsync<CharacterProfileResponse>(ct))!;
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var ownerId = await db.CharacterProfiles.Where(p => p.Id == character.Id).Select(p => p.OwnerId).SingleAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var profile = CharacterVoiceProfile.CreateClone(Guid.NewGuid(), ownerId, character.Id, CharacterVoiceProfileKind.Base,
            null, CharacterVoiceConsentTypes.SelfRecorded, "seeded/reference.wav", new string('a', 64), 10, ownerId, now);
        const string transcript = "你好，台灣。";
        profile.AttachDraftTranscript("seed-task", transcript, now);
        profile.ConfirmTranscript(transcript, now);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var scopes = new List<string> { CharacterVoiceConsentScopes.PrivateEvaluation };
        if (formal) scopes.Add(CharacterVoiceConsentScopes.FormalNarration);
        var evidence = CharacterVoiceConsentEvidence.Create("test", today.AddDays(-1), today,
            CharacterVoiceConsentTypes.ExplicitPermission, scopes, new string('e', 64), new string('f', 64),
            CharacterVoiceTranscriptCanonicalizer.ComputeSha256Hex(transcript), CharacterVoiceConsentEvidence.CurrentEvidenceVersion,
            CharacterVoiceConsentEvidence.CurrentAttestationVersion, today);
        var operation = CharacterVoiceProfileOperation.StageCreate(Guid.NewGuid(), ownerId, character.Id, profile.Id,
            CharacterVoiceProfileKind.Base, null, evidence, transcript, "seeded/reference.wav", new string('a', 64), 10, ownerId, "seed-key", now);
        operation.MarkRemotePrepared("seed-task", transcript, now);
        operation.MarkActivated(now);
        db.CharacterVoiceProfiles.Add(profile);
        db.CharacterVoiceProfileOperations.Add(operation);
        await db.SaveChangesAsync(ct);
        return profile.Id;
    }
}
