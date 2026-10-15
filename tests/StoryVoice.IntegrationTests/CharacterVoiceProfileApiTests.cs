using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryVoice.Application.Characters;
using StoryVoice.Application.Narrations;
using StoryVoice.Application.Series;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed class CharacterVoiceProfileApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Voice_profile_endpoints_require_authentication_and_mutations_require_csrf()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(client, cancellationToken);

        using var anonymousClient = factory.CreateClient();
        using var anonymousResponse = await anonymousClient.GetAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles",
            cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var missingCsrfResponse = await client.PostAsJsonAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/base/design",
            new { voicePrompt = "溫柔、略帶沙啞的女聲" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrfResponse.StatusCode);
    }

    [Fact]
    public async Task Design_creation_is_fail_closed_for_base_and_scene_without_persisting_a_profile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(client, cancellationToken);

        using var createResponse = await client.PostWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/base/design",
            new { voicePrompt = "溫柔、略帶沙啞的女聲" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, createResponse.StatusCode);
        Assert.Contains(
            "voice_prompt",
            await createResponse.Content.ReadAsStringAsync(cancellationToken),
            StringComparison.Ordinal);

        using var sceneResponse = await client.PostWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/scenes/angry/design",
            new { voicePrompt = "生氣、提高音量的聲音" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, sceneResponse.StatusCode);

        using var listResponse = await client.GetAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles",
            cancellationToken);
        var profiles = await listResponse.Content
            .ReadFromJsonAsync<CharacterVoiceProfileResponse[]>(cancellationToken);
        Assert.NotNull(profiles);
        Assert.Empty(profiles);
    }

    [Fact]
    public async Task An_existing_designed_profile_remains_listed_but_has_no_reference_audio()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var ownerAClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var ownerBClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(ownerAClient, cancellationToken);
        var profileId = await SeedDesignedVoiceProfileAsync(
            factory,
            characterProfileId,
            cancellationToken);

        var profiles = await ownerAClient.GetFromJsonAsync<CharacterVoiceProfileResponse[]>(
            $"/api/character-profiles/{characterProfileId}/voice-profiles",
            cancellationToken);
        Assert.Contains(Assert.IsType<CharacterVoiceProfileResponse[]>(profiles), profile => profile.Id == profileId);

        using var otherOwnerListResponse = await ownerBClient.GetAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles",
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, otherOwnerListResponse.StatusCode);

        using var referenceAudioResponse = await ownerAClient.GetAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{profileId}/reference-audio",
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, referenceAudioResponse.StatusCode);
    }

    [Fact]
    public async Task Preview_rejects_blank_or_overlong_text_and_unknown_profiles_before_ever_calling_3wa()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(client, cancellationToken);
        var profileId = await SeedDesignedVoiceProfileAsync(factory, characterProfileId, cancellationToken);

        using var blankResponse = await client.PostWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{profileId}/preview",
            new { text = "   " },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, blankResponse.StatusCode);

        using var overlongResponse = await client.PostWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{profileId}/preview",
            new { text = new string('a', 500) },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, overlongResponse.StatusCode);

        using var unknownProfileResponse = await client.PostWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{Guid.NewGuid()}/preview",
            new { text = "你好" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknownProfileResponse.StatusCode);
    }

    [Fact]
    public async Task Existing_designed_profile_preview_is_owner_scoped_CSRF_protected_and_rejected_without_calling_3wa()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaSynthesisClient(
            [0x52, 0x49, 0x46, 0x46, 0x57, 0x41, 0x56, 0x45],
            "audio/wav");
        using var previewFactory = CreatePreviewFactory(fake);
        using var owner = await previewFactory.CreateAuthenticatedClientAsync(cancellationToken);
        using var otherOwner = await previewFactory.CreateAuthenticatedClientAsync(cancellationToken);
        using var anonymous = previewFactory.CreateClient();
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        var profileId = await SeedDesignedVoiceProfileAsync(previewFactory, characterProfileId, cancellationToken);
        var previewPath = $"/api/character-profiles/{characterProfileId}/voice-profiles/{profileId}/preview";

        using var anonymousResponse = await anonymous.PostAsJsonAsync(
            previewPath,
            new { text = "你好，台灣。" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var missingCsrfResponse = await owner.PostAsJsonAsync(
            previewPath,
            new { text = "你好，台灣。" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrfResponse.StatusCode);

        using var otherOwnerResponse = await otherOwner.PostWithCsrfAsync(
            previewPath,
            new { text = "你好，台灣。" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, otherOwnerResponse.StatusCode);
        Assert.Equal(0, fake.SubmitCount);

        using var response = await owner.PostWithCsrfAsync(
            previewPath,
            new { text = "  你好，台灣。  " },
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "voice_prompt",
            await response.Content.ReadAsStringAsync(cancellationToken),
            StringComparison.Ordinal);
        Assert.Equal(0, fake.SubmitCount);
        Assert.Equal(0, fake.StatusCount);
        Assert.Equal(0, fake.ResultCount);
        Assert.Equal(0, fake.DownloadCount);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("disabled")]
    [InlineData("missing")]
    [InlineData("tampered")]
    [InlineData("unauthorized")]
    [InlineData("inactive")]
    [InlineData("invalid_pcm")]
    [InlineData("gateway")]
    public async Task Managed_preview_uses_only_local_gateway_and_never_mutates_originals(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeThreeWaSynthesisClient([], "audio/wav");
        var local = new ManagedPreviewGateway(scenario == "gateway");
        var root = Path.Combine(Path.GetTempPath(), "managed-preview-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "seeded"));
        var reference = CreatePcmWav(sampleRate: scenario == "invalid_pcm" ? 24_000 : 48_000);
        var path = Path.Combine(root, "seeded/reference.wav");
        if (scenario != "missing") await File.WriteAllBytesAsync(path, reference, ct);
        using var app = CreatePreviewFactory(external).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("LocalClonePreview:Enabled", (scenario != "disabled").ToString());
            builder.UseSetting("LocalClonePreview:InternalToken", new string('t', 32));
            builder.UseSetting("CharacterVoiceStorage:RootPath", root);
            // Legacy static previews retain their separate allowlist. Managed voice
            // previews must use the selected DB profile, never these placeholder assets.
            var prefix = $"LocalClonePreview:AllowedProfiles:{Guid.NewGuid():D}";
            builder.UseSetting($"{prefix}:Label", "legacy fixture");
            builder.UseSetting($"{prefix}:ReferenceAudioRelativePath", "legacy.wav");
            builder.UseSetting($"{prefix}:TranscriptRelativePath", "legacy.txt");
            builder.UseSetting($"{prefix}:ExpectedReferenceAudioSha256", new string('a', 64));
            builder.UseSetting($"{prefix}:ExpectedTranscriptSha256", new string('b', 64));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILocalCloneGatewayClient>();
                services.AddSingleton<ILocalCloneGatewayClient>(local);
            });
        });
        using var owner = await app.CreateAuthenticatedClientAsync(ct);
        var character = await CreateCharacterProfileAsync(owner, ct);
        var digest = Convert.ToHexString(SHA256.HashData(reference));
        var profile = await SeedReadyCloneVoiceProfileAsync(app, character, ct,
            scenario == "tampered" ? new string('a', 64) : digest);
        if (scenario == "unauthorized")
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            db.CharacterVoiceProfileOperations.RemoveRange(await db.CharacterVoiceProfileOperations
                .Where(operation => operation.NewProfileId == profile).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
        }
        if (scenario == "inactive")
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            (await db.CharacterProfiles.SingleAsync(value => value.Id == character, ct)).Deactivate(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
        }
        var profilesPath = $"/api/character-profiles/{character}/voice-profiles";
        var before = await owner.GetStringAsync(profilesPath, ct);
        await using var checkScope = app.Services.CreateAsyncScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var jobs = await checkDb.NarrationJobs.CountAsync(ct);
        using var response = await owner.PostWithCsrfAsync(
            $"{profilesPath}/{profile}/preview", new { text = "  你好，台灣。  " }, ct);
        Assert.Equal(scenario == "success" ? HttpStatusCode.OK
            : scenario == "unauthorized" ? HttpStatusCode.BadRequest
            : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(scenario is "success" or "gateway" ? 1 : 0, local.Calls);
        Assert.Equal(0, external.SubmitCount);
        Assert.Equal(0, external.StatusCount);
        Assert.Equal(0, external.ResultCount);
        Assert.Equal(0, external.DownloadCount);
        Assert.Equal(before, await owner.GetStringAsync(profilesPath, ct));
        Assert.Equal(jobs, await checkDb.NarrationJobs.CountAsync(ct));
        if (scenario != "missing") Assert.Equal(reference, await File.ReadAllBytesAsync(path, ct));
        if (local.LastRequest is { } request)
        {
            Assert.False(local.LastCancellationToken.CanBeCanceled);
            Assert.Equal(reference, request.ReferenceAudio);
            Assert.Equal("你好,台灣。", request.ReferenceTranscript);
            Assert.Equal("你好，台灣。", request.Text);
        }
        if (scenario == "success")
        {
            Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal(CreatePcmWav(1, 24_000), await response.Content.ReadAsByteArrayAsync(ct));
        }
        else if (scenario != "unauthorized")
        {
            Assert.Contains(scenario switch
            {
                "disabled" => "local_clone_preview_disabled",
                "inactive" => "local_clone_preview_not_configured",
                "gateway" => "local_clone_preview_gateway_unavailable",
                _ => "local_clone_preview_asset_invalid"
            }, await response.Content.ReadAsStringAsync(ct));
        }
    }

    private sealed class ManagedPreviewGateway(bool fail) : ILocalCloneGatewayClient
    {
        public int Calls { get; private set; }
        public LocalCloneGatewayRequest? LastRequest { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }
        public Task<LocalCloneGatewayAudio> SynthesizeAsync(LocalCloneGatewayRequest request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            LastCancellationToken = ct;
            if (fail) throw new LocalClonePreviewUnavailableException(LocalClonePreviewFailureKind.GatewayUnavailable);
            return Task.FromResult(new LocalCloneGatewayAudio(CreatePcmWav(1, 24_000), "audio/wav"));
        }
    }

    [Fact]
    public async Task Clone_upload_with_header_based_csrf_reaches_the_service_layer_instead_of_failing_form_binding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(client, cancellationToken);

        using var content = CreateCloneContent();

        using var response = await client.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/base",
            content,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ThreeWaAiHub__ApiToken", body);

        var operations = await client.GetFromJsonAsync<CharacterVoiceProfileOperationResponse[]>(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/clone-operations",
            cancellationToken);
        var rejected = Assert.Single(Assert.IsType<CharacterVoiceProfileOperationResponse[]>(operations));
        Assert.Equal("Rejected", rejected.State);
        Assert.Equal("remote_prepare_not_sent", rejected.AttentionCode);
        Assert.False(rejected.HasRemoteTask);
        Assert.Equal("attested", rejected.EvidenceStatus);
        Assert.Equal(
            [
                CharacterVoiceConsentScopes.PrivateEvaluation,
                CharacterVoiceConsentScopes.FormalNarration,
            ],
            rejected.UsageScopes);
    }

    [Fact]
    public async Task Base_design_is_replaced_only_after_prepare_succeeds_and_expected_text_reaches_the_provider()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaVoiceProfileClient
        {
            PrepareResult = new VoiceProfilePrepareResult("clone-task-1", "供應商辨識草稿。"),
        };
        using var profileFactory = CreateVoiceProfileFactory(fake);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        var designProfileId = await SeedDesignedVoiceProfileAsync(
            profileFactory,
            characterProfileId,
            cancellationToken);
        using var content = CreateCloneContent(expectedTranscript: "這是錄音中的實際內容。");

        using var response = await owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{designProfileId}/replace-with-clone",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CharacterVoiceProfileResponse>(cancellationToken);
        Assert.NotNull(created);
        Assert.Equal("Clone", created.Mode);
        Assert.Equal("AwaitingTranscriptConfirmation", created.Status);
        Assert.Equal("這是錄音中的實際內容。", created.ExpectedTranscript);
        Assert.Equal("供應商辨識草稿。", created.AsrDraftTranscript);
        Assert.Equal("供應商辨識草稿。", created.Transcript);
        Assert.NotEqual(designProfileId, created.Id);
        Assert.Equal(1, fake.PrepareCount);
        Assert.Equal("這是錄音中的實際內容。", fake.ExpectedText);
        Assert.Equal("explicit_permission", fake.ConsentType);
        // Durable path 現在帶伺服器端逾時 token（不是呼叫端的 HTTP token）：
        // 可取消，但只會因為逾時保護觸發，呼叫端斷線仍不會中斷 prepare。
        Assert.True(fake.PrepareCancellationCanBeCanceled);
        Assert.Equal(0, fake.DeleteCount);

        var operation = await LoadOnlyOperationAsync(
            profileFactory,
            characterProfileId,
            cancellationToken);
        Assert.Equal(CharacterVoiceProfileOperationState.Activated, operation.State);
        Assert.Equal(created.Id, operation.NewProfileId);
        Assert.Equal(designProfileId, operation.OldProfileId);
        Assert.Equal("clone-task-1", operation.RemoteTaskId);
        Assert.Equal("integration-test-key", operation.CredentialKeyId);
        Assert.Equal("這是錄音中的實際內容。", operation.ExpectedTranscript);
        Assert.Equal("供應商辨識草稿。", operation.AsrDraftTranscript);

        var profiles = await owner.GetFromJsonAsync<CharacterVoiceProfileResponse[]>(
            $"/api/character-profiles/{characterProfileId}/voice-profiles",
            cancellationToken);
        var onlyProfile = Assert.Single(Assert.IsType<CharacterVoiceProfileResponse[]>(profiles));
        Assert.Equal(created.Id, onlyProfile.Id);
        Assert.Equal("Clone", onlyProfile.Mode);
        Assert.DoesNotContain(profiles, profile => profile.Id == designProfileId);

        using var referenceAudio = await owner.GetAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{created.Id}/reference-audio",
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, referenceAudio.StatusCode);
    }

    [Fact]
    public async Task Staged_replacement_blocks_design_and_character_delete_while_prepare_is_in_flight()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var prepareStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrepare = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeThreeWaVoiceProfileClient
        {
            PrepareResult = new VoiceProfilePrepareResult("race-safe-task", "供應商辨識草稿。"),
            PrepareStarted = prepareStarted,
            ReleasePrepare = releasePrepare,
        };
        using var profileFactory = CreateVoiceProfileFactory(fake);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        var designProfileId = await SeedDesignedVoiceProfileAsync(
            profileFactory,
            characterProfileId,
            cancellationToken);
        using var content = CreateCloneContent();
        var replaceTask = owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{designProfileId}/replace-with-clone",
            content,
            cancellationToken);

        try
        {
            await prepareStarted.Task.WaitAsync(cancellationToken);

            using var designDelete = await owner.DeleteWithCsrfAsync(
                $"/api/character-profiles/{characterProfileId}/voice-profiles/{designProfileId}",
                cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, designDelete.StatusCode);

            using var characterDelete = await owner.DeleteWithCsrfAsync(
                $"/api/character-profiles/{characterProfileId}",
                cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, characterDelete.StatusCode);
        }
        finally
        {
            releasePrepare.TrySetResult(true);
        }

        using var replaceResponse = await replaceTask;
        Assert.Equal(HttpStatusCode.Created, replaceResponse.StatusCode);
        Assert.Equal(1, fake.PrepareCount);
    }

    [Fact]
    public async Task Replace_prepare_failure_preserves_the_design_slot_operation_and_WAV_for_reconciliation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaVoiceProfileClient
        {
            PrepareException = new ThreeWaAiHubException("離線模擬 prepare 失敗。"),
        };
        using var profileFactory = CreateVoiceProfileFactory(fake);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        var designProfileId = await SeedDesignedVoiceProfileAsync(
            profileFactory,
            characterProfileId,
            cancellationToken);
        var voiceRoot = Path.Combine(factory.StorageRoot, "character-voices");
        var wavCountBefore = Directory.Exists(voiceRoot)
            ? Directory.EnumerateFiles(voiceRoot, "*.wav", SearchOption.AllDirectories).Count()
            : 0;
        using var content = CreateCloneContent();

        using var response = await owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{designProfileId}/replace-with-clone",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, fake.PrepareCount);
        Assert.Equal(0, fake.DeleteCount);
        var profiles = await owner.GetFromJsonAsync<CharacterVoiceProfileResponse[]>(
            $"/api/character-profiles/{characterProfileId}/voice-profiles",
            cancellationToken);
        var preserved = Assert.Single(Assert.IsType<CharacterVoiceProfileResponse[]>(profiles));
        Assert.Equal(designProfileId, preserved.Id);
        Assert.Equal("Design", preserved.Mode);
        var wavCountAfter = Directory.Exists(voiceRoot)
            ? Directory.EnumerateFiles(voiceRoot, "*.wav", SearchOption.AllDirectories).Count()
            : 0;
        Assert.Equal(wavCountBefore + 1, wavCountAfter);
        var operation = await LoadOnlyOperationAsync(
            profileFactory,
            characterProfileId,
            cancellationToken);
        Assert.Equal(CharacterVoiceProfileOperationState.NeedsAttention, operation.State);
        Assert.Equal("remote_prepare_uncertain", operation.SafeErrorCode);
        Assert.Null(operation.RemoteTaskId);

        using var retryContent = CreateCloneContent();
        using var retryResponse = await owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{designProfileId}/replace-with-clone",
            retryContent,
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, retryResponse.StatusCode);
        Assert.Equal(1, fake.PrepareCount);
        Assert.Equal(wavCountAfter, Directory.EnumerateFiles(voiceRoot, "*.wav", SearchOption.AllDirectories).Count());

        using var characterDelete = await owner.DeleteWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}",
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, characterDelete.StatusCode);
    }

    [Fact]
    public async Task Replace_commit_failure_preserves_the_design_slot_task_operation_and_WAV()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaVoiceProfileClient
        {
            PrepareResult = new VoiceProfilePrepareResult("db-fail-task", "辨識草稿。"),
        };
        using var profileFactory = CreateVoiceProfileFactory(fake, failReplacementSave: true);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        var designProfileId = await SeedDesignedVoiceProfileAsync(
            profileFactory,
            characterProfileId,
            cancellationToken);
        var voiceRoot = Path.Combine(factory.StorageRoot, "character-voices");
        var wavCountBefore = Directory.Exists(voiceRoot)
            ? Directory.EnumerateFiles(voiceRoot, "*.wav", SearchOption.AllDirectories).Count()
            : 0;
        using var content = CreateCloneContent();

        using var response = await owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{designProfileId}/replace-with-clone",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, fake.PrepareCount);
        Assert.Equal(0, fake.DeleteCount);
        var profiles = await owner.GetFromJsonAsync<CharacterVoiceProfileResponse[]>(
            $"/api/character-profiles/{characterProfileId}/voice-profiles",
            cancellationToken);
        var preserved = Assert.Single(Assert.IsType<CharacterVoiceProfileResponse[]>(profiles));
        Assert.Equal(designProfileId, preserved.Id);
        Assert.Equal("Design", preserved.Mode);
        var wavCountAfter = Directory.Exists(voiceRoot)
            ? Directory.EnumerateFiles(voiceRoot, "*.wav", SearchOption.AllDirectories).Count()
            : 0;
        Assert.Equal(wavCountBefore + 1, wavCountAfter);
        var operation = await LoadOnlyOperationAsync(
            profileFactory,
            characterProfileId,
            cancellationToken);
        Assert.Equal(CharacterVoiceProfileOperationState.NeedsAttention, operation.State);
        Assert.Equal("local_activation_uncertain", operation.SafeErrorCode);
        Assert.Equal("db-fail-task", operation.RemoteTaskId);

        using var operationListResponse = await owner.GetAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/clone-operations",
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, operationListResponse.StatusCode);
        var operationListBody = await operationListResponse.Content.ReadAsStringAsync(cancellationToken);
        Assert.DoesNotContain("db-fail-task", operationListBody, StringComparison.Ordinal);
        Assert.DoesNotContain("api-test-recorder", operationListBody, StringComparison.Ordinal);
        Assert.DoesNotContain("recorderName", operationListBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("consentRecordSha256", operationListBody, StringComparison.OrdinalIgnoreCase);
        var listedOperations = await operationListResponse.Content
            .ReadFromJsonAsync<CharacterVoiceProfileOperationResponse[]>(cancellationToken);
        var listed = Assert.Single(Assert.IsType<CharacterVoiceProfileOperationResponse[]>(listedOperations));
        Assert.Equal("local_activation_uncertain", listed.AttentionCode);
        Assert.True(listed.HasRemoteTask);

        using var resumeResponse = await owner.PostWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/clone-operations/{operation.Id}/resume-activation",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, resumeResponse.StatusCode);
        Assert.Equal(1, fake.PrepareCount);
        var resumed = await resumeResponse.Content.ReadFromJsonAsync<CharacterVoiceProfileResponse>(cancellationToken);
        Assert.NotNull(resumed);
        Assert.Equal("Clone", resumed.Mode);
        var resumedOperation = await LoadOnlyOperationAsync(
            profileFactory,
            characterProfileId,
            cancellationToken);
        Assert.Equal(CharacterVoiceProfileOperationState.Activated, resumedOperation.State);
        Assert.Null(resumedOperation.SafeErrorCode);
    }

    [Fact]
    public async Task Replace_is_owner_and_base_design_scoped_before_any_provider_call()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaVoiceProfileClient();
        using var profileFactory = CreateVoiceProfileFactory(fake);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        using var otherOwner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        var designProfileId = await SeedDesignedVoiceProfileAsync(
            profileFactory,
            characterProfileId,
            cancellationToken);

        using (var otherOwnerContent = CreateCloneContent())
        using (var otherOwnerResponse = await otherOwner.PostMultipartWithCsrfAsync(
                   $"/api/character-profiles/{characterProfileId}/voice-profiles/{designProfileId}/replace-with-clone",
                   otherOwnerContent,
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, otherOwnerResponse.StatusCode);
        }

        var sceneProfileId = await SeedDesignedVoiceProfileAsync(
            profileFactory,
            characterProfileId,
            cancellationToken,
            CharacterVoiceProfileKind.Scene,
            CharacterVoiceSceneCodes.Angry);
        using (var sceneContent = CreateCloneContent())
        using (var sceneResponse = await owner.PostMultipartWithCsrfAsync(
                   $"/api/character-profiles/{characterProfileId}/voice-profiles/{sceneProfileId}/replace-with-clone",
                   sceneContent,
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, sceneResponse.StatusCode);
        }

        Assert.Equal(0, fake.PrepareCount);
    }

    [Fact]
    public async Task Clone_upload_over_10_MiB_returns_413_before_any_provider_call()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaVoiceProfileClient();
        using var profileFactory = CreateVoiceProfileFactory(fake);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        using var content = CreateCloneContent(
            new byte[checked((int)CharacterVoiceProfileLimits.MaximumReferenceAudioBytes + 1)]);

        using var response = await owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/base",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("10 MiB", await response.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        Assert.Equal(0, fake.PrepareCount);
    }

    [Fact]
    public async Task Clone_upload_requires_explicit_rights_attestation_before_any_provider_call()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaVoiceProfileClient();
        using var profileFactory = CreateVoiceProfileFactory(fake);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        using var content = CreateCloneContent(rightsAttested: false);

        using var response = await owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/base",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fake.PrepareCount);
        await using var scope = profileFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        Assert.False(await db.CharacterVoiceProfileOperations.AnyAsync(
            operation => operation.CharacterProfileId == characterProfileId,
            cancellationToken));
    }

    [Fact]
    public async Task Clone_upload_over_32_KiB_receipt_returns_413_before_any_provider_call()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaVoiceProfileClient();
        using var profileFactory = CreateVoiceProfileFactory(fake);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        using var content = new MultipartFormDataContent();
        var audio = new ByteArrayContent(CreatePcmWav());
        audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "referenceAudio", "sample.wav");
        var receipt = new ByteArrayContent(
            new byte[checked((int)CharacterVoiceProfileLimits.MaximumConsentReceiptBytes + 1)]);
        receipt.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Add(receipt, "consentReceipt", "oversize.json");
        content.Add(new StringContent("true"), "rightsAttested");
        content.Add(new StringContent("這是錄音中的實際內容。"), "expectedTranscript");

        using var response = await owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/base",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, fake.PrepareCount);
    }

    [Fact]
    public async Task Clone_upload_with_blank_expected_transcript_fails_without_a_provider_call_or_local_profile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaVoiceProfileClient();
        using var profileFactory = CreateVoiceProfileFactory(fake);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        using var content = CreateCloneContent(expectedTranscript: "   ");

        using var response = await owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/base",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fake.PrepareCount);
        var profiles = await owner.GetFromJsonAsync<CharacterVoiceProfileResponse[]>(
            $"/api/character-profiles/{characterProfileId}/voice-profiles",
            cancellationToken);
        Assert.Empty(Assert.IsType<CharacterVoiceProfileResponse[]>(profiles));
    }

    [Theory]
    [InlineData(9, 48_000)]
    [InlineData(10, 44_100)]
    public async Task Clone_upload_rejects_invalid_duration_or_PCM_format_before_any_provider_call(
        int durationSeconds,
        int sampleRate)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaVoiceProfileClient();
        using var profileFactory = CreateVoiceProfileFactory(fake);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        var voiceRoot = Path.Combine(factory.StorageRoot, "character-voices");
        var wavCountBefore = Directory.Exists(voiceRoot)
            ? Directory.EnumerateFiles(voiceRoot, "*.wav", SearchOption.AllDirectories).Count()
            : 0;
        using var content = CreateCloneContent(CreatePcmWav(durationSeconds, sampleRate));

        using var response = await owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/base",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fake.PrepareCount);
        var wavCountAfter = Directory.Exists(voiceRoot)
            ? Directory.EnumerateFiles(voiceRoot, "*.wav", SearchOption.AllDirectories).Count()
            : 0;
        Assert.Equal(wavCountBefore, wavCountAfter);
        await using var scope = profileFactory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        Assert.False(await dbContext.CharacterVoiceProfileOperations.AnyAsync(
            operation => operation.CharacterProfileId == characterProfileId,
            cancellationToken));
    }

    [Fact]
    public async Task Explicit_provider_auth_rejection_is_visible_and_does_not_permanently_block_the_slot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaVoiceProfileClient
        {
            PrepareException = new ThreeWaAiHubException(
                "3wa Cluster API request failed with HTTP 401.",
                ThreeWaAiHubFailureKind.RemoteAuthenticationRejected),
        };
        using var profileFactory = CreateVoiceProfileFactory(fake);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        using var otherOwner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        using var rejectedContent = CreateCloneContent();

        using var rejectedResponse = await owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/base",
            rejectedContent,
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, rejectedResponse.StatusCode);
        var operations = await owner.GetFromJsonAsync<CharacterVoiceProfileOperationResponse[]>(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/clone-operations",
            cancellationToken);
        var rejected = Assert.Single(Assert.IsType<CharacterVoiceProfileOperationResponse[]>(operations));
        Assert.Equal("Rejected", rejected.State);
        Assert.Equal("remote_prepare_auth_rejected", rejected.AttentionCode);
        using var otherOwnerResponse = await otherOwner.GetAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/clone-operations",
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, otherOwnerResponse.StatusCode);

        fake.PrepareException = null;
        fake.PrepareResult = new VoiceProfilePrepareResult("retry-task", "重試後草稿。");
        using var retryContent = CreateCloneContent();
        using var retryResponse = await owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/base",
            retryContent,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, retryResponse.StatusCode);
        Assert.Equal(2, fake.PrepareCount);
        var afterRetry = await owner.GetFromJsonAsync<CharacterVoiceProfileOperationResponse[]>(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/clone-operations",
            cancellationToken);
        Assert.Contains(afterRetry!, operation => operation.State == "Rejected");
        Assert.Contains(afterRetry!, operation => operation.State == "Activated");
    }

    [Theory]
    [InlineData(true, "remote_task_id_contract_mismatch")]
    [InlineData(false, "remote_draft_contract_mismatch")]
    public async Task Prepare_contract_violation_retains_remote_evidence_and_never_compensates(
        bool noncanonicalTaskId,
        string expectedSafeErrorCode)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var taskId = noncanonicalTaskId ? $"route_{new string('a', 34)}" : "draft-task";
        var fake = new FakeThreeWaVoiceProfileClient
        {
            PrepareResult = new VoiceProfilePrepareResult(
                taskId,
                noncanonicalTaskId ? "合法長度的草稿。" : new string('稿', 2_001)),
            DeleteException = new ThreeWaAiHubException("compensation-delete-failed"),
        };
        using var profileFactory = CreateVoiceProfileFactory(fake);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        var voiceRoot = Path.Combine(factory.StorageRoot, "character-voices");
        var wavCountBefore = Directory.Exists(voiceRoot)
            ? Directory.EnumerateFiles(voiceRoot, "*.wav", SearchOption.AllDirectories).Count()
            : 0;
        using var content = CreateCloneContent();

        using var response = await owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/base",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, fake.PrepareCount);
        Assert.Equal(0, fake.DeleteCount);
        Assert.DoesNotContain(
            "compensation-delete-failed",
            await response.Content.ReadAsStringAsync(cancellationToken),
            StringComparison.Ordinal);
        var profiles = await owner.GetFromJsonAsync<CharacterVoiceProfileResponse[]>(
            $"/api/character-profiles/{characterProfileId}/voice-profiles",
            cancellationToken);
        Assert.Empty(Assert.IsType<CharacterVoiceProfileResponse[]>(profiles));
        var wavCountAfter = Directory.Exists(voiceRoot)
            ? Directory.EnumerateFiles(voiceRoot, "*.wav", SearchOption.AllDirectories).Count()
            : 0;
        Assert.Equal(wavCountBefore + 1, wavCountAfter);
        var operation = await LoadOnlyOperationAsync(
            profileFactory,
            characterProfileId,
            cancellationToken);
        Assert.Equal(CharacterVoiceProfileOperationState.NeedsAttention, operation.State);
        Assert.Equal(expectedSafeErrorCode, operation.SafeErrorCode);
        Assert.Equal(taskId, operation.RemoteTaskId);
    }

    [Fact]
    public async Task Confirmation_intent_reconciles_remote_success_after_the_local_ready_save_fails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaVoiceProfileClient
        {
            PrepareResult = new VoiceProfilePrepareResult("confirm-task", "供應商 ASR 草稿。"),
        };
        using var profileFactory = CreateVoiceProfileFactory(fake, failFirstReadySave: true);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        using var createContent = CreateCloneContent();
        using var createResponse = await owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/base",
            createContent,
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CharacterVoiceProfileResponse>(cancellationToken);
        Assert.NotNull(created);

        using var confirmResponse = await owner.PostWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{created.Id}/confirm-transcript",
            new { transcript = "人工校正後的最終文字。" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, confirmResponse.StatusCode);
        Assert.Equal(1, fake.ConfirmCount);
        await using (var scope = profileFactory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var pending = await dbContext.CharacterVoiceProfiles
                .AsNoTracking()
                .SingleAsync(profile => profile.Id == created.Id, cancellationToken);
            Assert.Equal(CharacterVoiceProfileStatus.AwaitingTranscriptConfirmation, pending.Status);
            Assert.Equal("人工校正後的最終文字。", pending.ConfirmationTranscriptIntent);
            Assert.Equal("供應商 ASR 草稿。", pending.AsrDraftTranscript);
            Assert.Equal("供應商 ASR 草稿。", pending.Transcript);
        }

        fake.StatusResult = new VoiceProfileStatusResult(
            "ready",
            TranscriptConfirmed: true,
            DraftTranscript: "與人工確認意圖不同的文字。",
            TranscriptionFailed: false);
        using var mismatchedRefresh = await owner.PostWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{created.Id}/refresh-status",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, mismatchedRefresh.StatusCode);
        Assert.Contains(
            "confirmation_intent_mismatch",
            await mismatchedRefresh.Content.ReadAsStringAsync(cancellationToken),
            StringComparison.Ordinal);

        fake.StatusResult = new VoiceProfileStatusResult(
            "ready",
            TranscriptConfirmed: true,
            DraftTranscript: null,
            TranscriptionFailed: false);
        using var refreshResponse = await owner.PostWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{created.Id}/refresh-status",
            new { },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        var reconciled = await refreshResponse.Content
            .ReadFromJsonAsync<CharacterVoiceProfileResponse>(cancellationToken);
        Assert.NotNull(reconciled);
        Assert.Equal("Ready", reconciled.Status);
        Assert.True(reconciled.TranscriptConfirmed);
        Assert.Equal("人工校正後的最終文字。", reconciled.ConfirmationTranscriptIntent);
        Assert.Equal("人工校正後的最終文字。", reconciled.Transcript);
        Assert.Equal("供應商 ASR 草稿。", reconciled.AsrDraftTranscript);
        Assert.Equal(1, fake.ConfirmCount);
        Assert.Equal(2, fake.StatusCount);
    }

    [Fact]
    public async Task Remote_confirmed_status_never_invents_a_missing_human_confirmation_intent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaVoiceProfileClient
        {
            PrepareResult = new VoiceProfilePrepareResult("no-intent-task", "僅是供應商 ASR 草稿。"),
            StatusResult = new VoiceProfileStatusResult(
                "ready",
                TranscriptConfirmed: true,
                DraftTranscript: "供應商宣稱已確認的文字。",
                TranscriptionFailed: false),
        };
        using var profileFactory = CreateVoiceProfileFactory(fake);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        using var createContent = CreateCloneContent();
        using var createResponse = await owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/base",
            createContent,
            cancellationToken);
        var created = await createResponse.Content.ReadFromJsonAsync<CharacterVoiceProfileResponse>(cancellationToken);
        Assert.NotNull(created);

        using var refreshResponse = await owner.PostWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{created.Id}/refresh-status",
            new { },
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, refreshResponse.StatusCode);
        Assert.Contains(
            "confirmation_intent_missing",
            await refreshResponse.Content.ReadAsStringAsync(cancellationToken),
            StringComparison.Ordinal);
        await using var scope = profileFactory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var persisted = await dbContext.CharacterVoiceProfiles
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == created.Id, cancellationToken);
        Assert.Equal(CharacterVoiceProfileStatus.AwaitingTranscriptConfirmation, persisted.Status);
        Assert.Null(persisted.ConfirmationTranscriptIntent);
        Assert.Equal("僅是供應商 ASR 草稿。", persisted.AsrDraftTranscript);
        Assert.Equal("僅是供應商 ASR 草稿。", persisted.Transcript);
    }

    [Fact]
    public async Task Clone_delete_is_fail_closed_without_any_remote_call_and_preserves_local_evidence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaVoiceProfileClient
        {
            PrepareResult = new VoiceProfilePrepareResult("clone-task-del", "逐字稿草稿。"),
        };
        using var profileFactory = CreateVoiceProfileFactory(fake);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        using var createContent = CreateCloneContent();
        using var createResponse = await owner.PostMultipartWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/base",
            createContent,
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CharacterVoiceProfileResponse>(cancellationToken);
        Assert.NotNull(created);

        using var failedDelete = await owner.DeleteWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{created.Id}",
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, failedDelete.StatusCode);
        Assert.Equal(0, fake.DeleteCount);
        var preserved = await owner.GetFromJsonAsync<CharacterVoiceProfileResponse[]>(
            $"/api/character-profiles/{characterProfileId}/voice-profiles",
            cancellationToken);
        Assert.Contains(Assert.IsType<CharacterVoiceProfileResponse[]>(preserved), profile => profile.Id == created.Id);
        using var preservedAudio = await owner.GetAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{created.Id}/reference-audio",
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, preservedAudio.StatusCode);
        Assert.Contains("private", preservedAudio.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.True(preservedAudio.Headers.CacheControl.NoStore);
        Assert.Contains("nosniff", preservedAudio.Headers.GetValues("X-Content-Type-Options"));

        using var rebuild = await owner.PostWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{created.Id}/rebuild",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, rebuild.StatusCode);
        Assert.Equal(1, fake.PrepareCount);

        using var characterDelete = await owner.DeleteWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}",
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, characterDelete.StatusCode);

        var operation = await LoadOnlyOperationAsync(
            profileFactory,
            characterProfileId,
            cancellationToken);
        Assert.Equal(CharacterVoiceProfileOperationState.Activated, operation.State);
        Assert.Equal(created.Id, operation.NewProfileId);
    }

    [Fact]
    public async Task Design_delete_never_calls_the_remote_profile_delete_operation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaVoiceProfileClient();
        using var profileFactory = CreateVoiceProfileFactory(fake);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        var profileId = await SeedDesignedVoiceProfileAsync(profileFactory, characterProfileId, cancellationToken);

        using var response = await owner.DeleteWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{profileId}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, fake.DeleteCount);
    }

    [Fact]
    public async Task Clone_without_a_remote_task_id_is_also_fail_closed_and_preserved()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fake = new FakeThreeWaVoiceProfileClient();
        using var profileFactory = CreateVoiceProfileFactory(fake);
        using var owner = await profileFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var characterProfileId = await CreateCharacterProfileAsync(owner, cancellationToken);
        var profileId = await SeedPendingCloneWithoutTaskAsync(
            profileFactory,
            characterProfileId,
            cancellationToken);

        using var response = await owner.DeleteWithCsrfAsync(
            $"/api/character-profiles/{characterProfileId}/voice-profiles/{profileId}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fake.DeleteCount);
        var profiles = await owner.GetFromJsonAsync<CharacterVoiceProfileResponse[]>(
            $"/api/character-profiles/{characterProfileId}/voice-profiles",
            cancellationToken);
        Assert.Contains(Assert.IsType<CharacterVoiceProfileResponse[]>(profiles), profile => profile.Id == profileId);
    }

    private static async Task<Guid> CreateCharacterProfileAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostWithCsrfAsync(
            "/api/character-profiles",
            new
            {
                canonicalName = $"測試角色-{Guid.NewGuid():N}",
                age = (string?)null,
                gender = (string?)null,
                birthday = (string?)null,
                personality = (string?)null,
                catchphrase = (string?)null,
                background = (string?)null,
                speakingStyle = (string?)null
            },
            cancellationToken);
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Unexpected response: {await response.Content.ReadAsStringAsync(cancellationToken)}");
        var created = await response.Content.ReadFromJsonAsync<CharacterProfileResponse>(cancellationToken);
        Assert.NotNull(created);
        return created.Id;
    }

    private static async Task<Guid> SeedDesignedVoiceProfileAsync(
        WebApplicationFactory<Program> appFactory,
        Guid characterProfileId,
        CancellationToken cancellationToken,
        CharacterVoiceProfileKind kind = CharacterVoiceProfileKind.Base,
        string? sceneCode = null)
    {
        await using var scope = appFactory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var ownerId = await dbContext.CharacterProfiles
            .Where(profile => profile.Id == characterProfileId)
            .Select(profile => profile.OwnerId)
            .SingleAsync(cancellationToken);
        var profile = CharacterVoiceProfile.CreateDesign(
            Guid.NewGuid(),
            ownerId,
            characterProfileId,
            kind,
            sceneCode,
            "溫柔、略帶沙啞的台灣華語女聲",
            DateTimeOffset.UtcNow);
        dbContext.CharacterVoiceProfiles.Add(profile);
        await dbContext.SaveChangesAsync(cancellationToken);
        return profile.Id;
    }

    private static MultipartFormDataContent CreateCloneContent(
        byte[]? audio = null,
        string expectedTranscript = "這是錄音中的實際內容。",
        IReadOnlyList<string>? usageScopes = null,
        bool rightsAttested = true)
    {
        var audioBytes = audio ?? CreatePcmWav();
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(audioBytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        content.Add(file, "referenceAudio", "sample.wav");
        var receipt = new ByteArrayContent(CreateConsentReceipt(
            audioBytes,
            expectedTranscript,
            usageScopes ??
            [
                CharacterVoiceConsentScopes.PrivateEvaluation,
                CharacterVoiceConsentScopes.FormalNarration,
            ]));
        receipt.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Add(receipt, "consentReceipt", "sample-consent.json");
        content.Add(new StringContent(rightsAttested ? "true" : "false"), "rightsAttested");
        content.Add(new StringContent(expectedTranscript), "expectedTranscript");
        return content;
    }

    private static byte[] CreateConsentReceipt(
        byte[] audio,
        string expectedTranscript,
        IReadOnlyList<string> usageScopes)
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)).ToString("yyyy-MM-dd");
        var transcriptHash = string.IsNullOrWhiteSpace(expectedTranscript)
            ? new string('d', 64)
            : CharacterVoiceTranscriptCanonicalizer.ComputeSha256Hex(expectedTranscript);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = CharacterVoiceConsentEvidence.CurrentEvidenceVersion,
            recorderName = "api-test-recorder",
            recordingDate = date,
            consentSignedDate = date,
            consentType = CharacterVoiceConsentTypes.ExplicitPermission,
            usageScopes,
            recordingSha256 = Convert.ToHexString(SHA256.HashData(audio)).ToLowerInvariant(),
            expectedTranscriptCanonicalSha256 = transcriptHash,
            consentSha256 = new string('e', 64),
            subjectAttestationVersion = CharacterVoiceConsentEvidence.CurrentAttestationVersion,
            generatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
        });
    }

    private static byte[] CreatePcmWav(int durationSeconds = 10, int sampleRate = 48_000)
    {
        const ushort channels = 1;
        const ushort bitsPerSample = 16;
        const ushort blockAlign = channels * (bitsPerSample / 8);
        var byteRate = checked(sampleRate * blockAlign);
        var dataLength = checked(byteRate * durationSeconds);
        var wav = new byte[checked(44 + dataLength)];
        "RIFF"u8.CopyTo(wav.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4, 4), checked((uint)(wav.Length - 8)));
        "WAVE"u8.CopyTo(wav.AsSpan(8, 4));
        "fmt "u8.CopyTo(wav.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24, 4), checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28, 4), checked((uint)byteRate));
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34, 2), bitsPerSample);
        "data"u8.CopyTo(wav.AsSpan(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40, 4), checked((uint)dataLength));
        return wav;
    }

    private static async Task<CharacterVoiceProfileOperation> LoadOnlyOperationAsync(
        WebApplicationFactory<Program> appFactory,
        Guid characterProfileId,
        CancellationToken cancellationToken)
    {
        await using var scope = appFactory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        return await dbContext.CharacterVoiceProfileOperations
            .AsNoTracking()
            .SingleAsync(
                operation => operation.CharacterProfileId == characterProfileId,
                cancellationToken);
    }

    private static async Task<Guid> SeedReadyCloneVoiceProfileAsync(
        WebApplicationFactory<Program> appFactory,
        Guid characterProfileId,
        CancellationToken cancellationToken,
        string? referenceHash = null)
    {
        await using var scope = appFactory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var ownerId = await dbContext.CharacterProfiles
            .Where(profile => profile.Id == characterProfileId)
            .Select(profile => profile.OwnerId)
            .SingleAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var profile = CharacterVoiceProfile.CreateClone(
            Guid.NewGuid(),
            ownerId,
            characterProfileId,
            CharacterVoiceProfileKind.Base,
            null,
            CharacterVoiceConsentTypes.SelfRecorded,
            "seeded/reference.wav",
            referenceHash ?? new string('a', 64),
            8,
            ownerId,
            now);
        profile.AttachDraftTranscript("seeded-profile-task", "你好，台灣。", now);
        profile.ConfirmTranscript("你好，台灣。", now);
        var operation = CharacterVoiceProfileOperation.StageCreate(
            Guid.NewGuid(),
            ownerId,
            characterProfileId,
            profile.Id,
            CharacterVoiceProfileKind.Base,
            sceneCode: null,
            CreateConsentEvidence("你好，台灣。", includeFormalNarration: true),
            "你好，台灣。",
            "seeded/reference.wav",
            referenceHash ?? new string('a', 64),
            10,
            ownerId,
            "seeded-key",
            now);
        operation.MarkRemotePrepared("seeded-profile-task", "你好，台灣。", now);
        operation.MarkActivated(now);
        dbContext.CharacterVoiceProfiles.Add(profile);
        dbContext.CharacterVoiceProfileOperations.Add(operation);
        await dbContext.SaveChangesAsync(cancellationToken);
        return profile.Id;
    }

    private static CharacterVoiceConsentEvidence CreateConsentEvidence(
        string transcript,
        bool includeFormalNarration)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var scopes = new List<string> { CharacterVoiceConsentScopes.PrivateEvaluation };
        if (includeFormalNarration)
        {
            scopes.Add(CharacterVoiceConsentScopes.FormalNarration);
        }

        return CharacterVoiceConsentEvidence.Create(
            "api-test-recorder",
            today.AddDays(-1),
            today,
            CharacterVoiceConsentTypes.ExplicitPermission,
            scopes,
            new string('e', 64),
            new string('f', 64),
            CharacterVoiceTranscriptCanonicalizer.ComputeSha256Hex(transcript),
            CharacterVoiceConsentEvidence.CurrentEvidenceVersion,
            CharacterVoiceConsentEvidence.CurrentAttestationVersion,
            today);
    }

    private static async Task<Guid> SeedPendingCloneWithoutTaskAsync(
        WebApplicationFactory<Program> appFactory,
        Guid characterProfileId,
        CancellationToken cancellationToken)
    {
        await using var scope = appFactory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var ownerId = await dbContext.CharacterProfiles
            .Where(profile => profile.Id == characterProfileId)
            .Select(profile => profile.OwnerId)
            .SingleAsync(cancellationToken);
        var profile = CharacterVoiceProfile.CreateClone(
            Guid.NewGuid(),
            ownerId,
            characterProfileId,
            CharacterVoiceProfileKind.Base,
            null,
            CharacterVoiceConsentTypes.SelfRecorded,
            "seeded/no-task.wav",
            new string('b', 64),
            5,
            ownerId,
            DateTimeOffset.UtcNow);
        dbContext.CharacterVoiceProfiles.Add(profile);
        await dbContext.SaveChangesAsync(cancellationToken);
        return profile.Id;
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreatePreviewFactory(
        FakeThreeWaSynthesisClient fake,
        int maximumAudioResponseBytes = 20 * 1024 * 1024) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                $"{ThreeWaAiHubOptions.SectionName}:MaximumAudioResponseBytes",
                maximumAudioResponseBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IThreeWaSynthesisClient>();
                services.AddSingleton(fake);
                services.AddSingleton<IThreeWaSynthesisClient>(provider =>
                    provider.GetRequiredService<FakeThreeWaSynthesisClient>());
            });
        });

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreateVoiceProfileFactory(
        FakeThreeWaVoiceProfileClient fake,
        bool failReplacementSave = false,
        bool failFirstReadySave = false) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                $"{ThreeWaAiHubOptions.SectionName}:CredentialKeyId",
                "integration-test-key");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IThreeWaVoiceProfileClient>();
                services.AddSingleton(fake);
                services.AddSingleton<IThreeWaVoiceProfileClient>(provider =>
                    provider.GetRequiredService<FakeThreeWaVoiceProfileClient>());
                if (failReplacementSave)
                {
                    services.AddSingleton<ReplacementSaveFailureInterceptor>();
                    services.AddDbContext<StoryVoiceDbContext>((provider, options) =>
                        options.AddInterceptors(
                            provider.GetRequiredService<ReplacementSaveFailureInterceptor>()));
                }

                if (failFirstReadySave)
                {
                    services.AddSingleton<ReadySaveFailureInterceptor>();
                    services.AddDbContext<StoryVoiceDbContext>((provider, options) =>
                        options.AddInterceptors(
                            provider.GetRequiredService<ReadySaveFailureInterceptor>()));
                }
            });
        });

    private sealed class ReplacementSaveFailureInterceptor : SaveChangesInterceptor
    {
        private int failuresRemaining = 1;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var entries = eventData.Context?.ChangeTracker.Entries<CharacterVoiceProfile>().ToArray() ?? [];
            if (entries.Any(entry => entry.State == EntityState.Deleted
                    && entry.Entity.Mode == CharacterVoiceProfileMode.Design)
                && Interlocked.Exchange(ref failuresRemaining, 0) == 1)
            {
                throw new InvalidOperationException("離線模擬 replacement commit 失敗。");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class ReadySaveFailureInterceptor : SaveChangesInterceptor
    {
        private int failuresRemaining = 1;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var shouldFail = eventData.Context?.ChangeTracker.Entries<CharacterVoiceProfile>()
                .Any(entry => entry.State == EntityState.Modified
                    && entry.Entity.Mode == CharacterVoiceProfileMode.Clone
                    && entry.Entity.Status == CharacterVoiceProfileStatus.Ready
                    && entry.Entity.ConfirmationTranscriptIntent is not null) == true;
            if (shouldFail && Interlocked.Exchange(ref failuresRemaining, 0) == 1)
            {
                throw new InvalidOperationException("離線模擬 remote confirm 後本地 Ready commit 失敗。");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class FakeThreeWaVoiceProfileClient : IThreeWaVoiceProfileClient
    {
        public VoiceProfilePrepareResult PrepareResult { get; set; } =
            new("fake-profile-task", DraftTranscript: null);
        public Exception? PrepareException { get; set; }
        public Exception? DeleteException { get; set; }
        public int PrepareCount { get; private set; }
        public int DeleteCount { get; private set; }
        public int StatusCount { get; private set; }
        public int ConfirmCount { get; private set; }
        public string? ExpectedText { get; private set; }
        public string? ConsentType { get; private set; }
        public string? ConfirmedTranscript { get; private set; }
        public bool PrepareCancellationCanBeCanceled { get; private set; }
        public TaskCompletionSource<bool>? PrepareStarted { get; init; }
        public TaskCompletionSource<bool>? ReleasePrepare { get; init; }
        public List<string> DeletedTaskIds { get; } = [];
        public VoiceProfileStatusResult StatusResult { get; set; } =
            new("running", false, null, false);

        public async Task<VoiceProfilePrepareResult> PrepareAsync(
            Stream referenceWav,
            string fileName,
            string profileName,
            string consentType,
            string expectedText,
            CancellationToken cancellationToken)
        {
            PrepareCount++;
            ExpectedText = expectedText;
            ConsentType = consentType;
            PrepareCancellationCanBeCanceled = cancellationToken.CanBeCanceled;
            PrepareStarted?.TrySetResult(true);
            if (ReleasePrepare is not null)
            {
                await ReleasePrepare.Task.WaitAsync(cancellationToken);
            }

            if (PrepareException is not null)
            {
                throw PrepareException;
            }

            return PrepareResult;
        }

        public Task<VoiceProfileStatusResult> GetStatusAsync(
            string taskId,
            CancellationToken cancellationToken)
        {
            StatusCount++;
            return Task.FromResult(StatusResult);
        }

        public Task ConfirmAsync(
            string taskId,
            string transcript,
            CancellationToken cancellationToken)
        {
            ConfirmCount++;
            ConfirmedTranscript = transcript;
            return Task.CompletedTask;
        }

        public Task DeleteProfileAsync(string taskId, CancellationToken cancellationToken)
        {
            DeleteCount++;
            DeletedTaskIds.Add(taskId);
            return DeleteException is null
                ? Task.CompletedTask
                : Task.FromException(DeleteException);
        }
    }

    private sealed class FakeThreeWaSynthesisClient(byte[] audio, string? contentType)
        : IThreeWaSynthesisClient
    {
        public byte[] Audio { get; } = audio;
        public ThreeWaSynthesisRequest? Request { get; private set; }
        public int SubmitCount { get; private set; }
        public int StatusCount { get; private set; }
        public int ResultCount { get; private set; }
        public int DownloadCount { get; private set; }

        public Task<ThreeWaSynthesisTaskHandle> SubmitAsync(
            ThreeWaSynthesisRequest request,
            CancellationToken cancellationToken)
        {
            SubmitCount++;
            Request = request;
            return Task.FromResult(new ThreeWaSynthesisTaskHandle(
                "731245",
                "fake-status",
                "fake-result",
                "fake-artifacts/{artifact_id}"));
        }

        public Task<string> GetTaskStatusAsync(string statusUrl, CancellationToken cancellationToken)
        {
            StatusCount++;
            return Task.FromResult("completed");
        }

        public Task<IReadOnlyList<ThreeWaSynthesisArtifact>> GetResultArtifactsAsync(
            string resultUrl,
            CancellationToken cancellationToken)
        {
            ResultCount++;
            IReadOnlyList<ThreeWaSynthesisArtifact> result =
                [new ThreeWaSynthesisArtifact("90210", contentType)];
            return Task.FromResult(result);
        }

        public async Task DownloadArtifactAsync(
            string artifactUrlTemplate,
            string artifactId,
            Stream destination,
            CancellationToken cancellationToken)
        {
            DownloadCount++;
            await destination.WriteAsync(Audio, cancellationToken);
        }
    }
}
