using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.Insights;
using StoryVoice.Application.Series;
using StoryVoice.Domain.Books;
using StoryVoice.Domain.Narrations;
using StoryVoice.Domain.Series;
using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.Infrastructure.Persistence;

internal sealed class SeriesService(
    StoryVoiceDbContext dbContext,
    ICurrentUser currentUser,
    IStorySeriesRepository repository,
    IOptions<BlueMagpieOptions> blueMagpieOptions,
    IOptions<LocalClonePreviewOptions> localCloneOptions) : ISeriesService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string NeutralRate = "+0%";
    private const string NeutralPitch = "+0Hz";
    private const string NeutralVolume = "+0%";
    private readonly bool _blueMagpieFormalNarrationEnabled =
        blueMagpieOptions.Value.FormalNarrationEnabled;

    public async Task<IReadOnlyList<StorySeriesSummaryResponse>> ListAsync(
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        return await dbContext.StorySeries
            .AsNoTracking()
            .Where(series => series.OwnerId == ownerId)
            .OrderBy(series => series.Name)
            .Select(series => new StorySeriesSummaryResponse(
                series.Id,
                series.Name,
                series.NarratorProvider,
                series.Books.Count,
                series.Characters.Count,
                series.ActiveCastRevisionId,
                series.CreatedAt,
                series.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<StorySeriesDetailsResponse?> GetAsync(
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        EnsureId(seriesId, nameof(seriesId));
        var ownerId = EnsureCurrentOwnerId();
        var series = await dbContext.StorySeries
            .AsNoTracking()
            .AsSplitQuery()
            .Include(candidate => candidate.Books)
            .Include(candidate => candidate.Characters)
            .Include(candidate => candidate.IdentityKeys)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == seriesId && candidate.OwnerId == ownerId,
                cancellationToken);
        return series is null
            ? null
            : await ToDetailsAsync(series, cancellationToken);
    }

    public async Task<StorySeriesDetailsResponse> CreateAsync(
        CreateStorySeriesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ownerId = EnsureCurrentOwnerId();
        var narratorVoice = await ResolveVoiceAsync(request.NarratorProvider, request.NarratorVoice, cancellationToken);
        EnsureFormalNarrationAvailable(narratorVoice.Provider);
        EnsureSupportedSynthesisParameters(
            narratorVoice.Provider,
            request.NarratorRate,
            request.NarratorPitch,
            request.NarratorVolume);
        var series = StorySeries.Create(
            ownerId,
            request.Name,
            narratorVoice.Provider,
            narratorVoice.Voice,
            request.NarratorRate,
            request.NarratorPitch,
            request.NarratorVolume,
            request.DefaultSpeakerPauseMs);
        if (await dbContext.StorySeries.AsNoTracking().AnyAsync(
                candidate => candidate.OwnerId == ownerId
                    && candidate.NormalizedName == series.NormalizedName,
                cancellationToken))
        {
            throw new InvalidOperationException("同一位使用者不可建立名稱相同的系列。");
        }

        await repository.AddAsync(series, cancellationToken);
        await SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(series, cancellationToken);
    }

    public async Task<StorySeriesDetailsResponse?> AddBookAsync(
        Guid seriesId,
        AddSeriesBookRequest request,
        CancellationToken cancellationToken)
    {
        EnsureId(seriesId, nameof(seriesId));
        ArgumentNullException.ThrowIfNull(request);
        EnsureId(request.BookId, nameof(request.BookId));
        var ownerId = EnsureCurrentOwnerId();
        var series = await repository.GetForMutationAsync(seriesId, cancellationToken);
        if (series is null)
        {
            return null;
        }

        var book = await dbContext.Books.SingleOrDefaultAsync(
            candidate => candidate.Id == request.BookId && candidate.OwnerId == ownerId,
            cancellationToken);
        if (book is null || book.Status == BookStatus.Linked || book.IsArchived)
        {
            return null;
        }

        series.AddBook(book, request.VolumeLabel, request.SortOrder);
        await InvalidatePendingRebuildsAsync(series, cancellationToken);
        await SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(series, cancellationToken);
    }

    public async Task<StorySeriesDetailsResponse?> AddCharacterAsync(
        Guid seriesId,
        AddSeriesCharacterRequest request,
        CancellationToken cancellationToken)
    {
        EnsureId(seriesId, nameof(seriesId));
        ArgumentNullException.ThrowIfNull(request);
        var ownerId = EnsureCurrentOwnerId();
        var series = await repository.GetForMutationAsync(seriesId, cancellationToken);
        if (series is null)
        {
            return null;
        }

        var voice = await ResolveVoiceAsync(request.VoiceProvider, request.Voice, cancellationToken);
        EnsureFormalNarrationAvailable(voice.Provider);
        EnsureSupportedSynthesisParameters(voice.Provider, request.Rate, request.Pitch, request.Volume);

        await EnsureCharacterProfileLinkAllowedAsync(
            ownerId,
            request.CharacterProfileId,
            currentCharacterProfileId: null,
            cancellationToken);
        EnsureCharacterProfileNotLinkedElsewhere(
            series,
            ignoredCharacterId: null,
            request.CharacterProfileId);
        EnsureSingleSynthesisProvider(series.NarratorProvider, [voice.Provider]);
        series.AddCharacter(
            request.CanonicalName,
            request.Role,
            voice.Provider,
            voice.Voice,
            request.Rate,
            request.Pitch,
            request.Volume,
            request.Notes,
            request.CharacterProfileId);
        await InvalidatePendingRebuildsAsync(series, cancellationToken);
        await SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(series, cancellationToken);
    }

    public async Task<StorySeriesDetailsResponse?> UpdateCharacterAsync(
        Guid seriesId,
        Guid characterId,
        UpdateSeriesCharacterRequest request,
        CancellationToken cancellationToken)
    {
        EnsureId(seriesId, nameof(seriesId));
        EnsureId(characterId, nameof(characterId));
        ArgumentNullException.ThrowIfNull(request);
        var ownerId = EnsureCurrentOwnerId();
        var series = await repository.GetForMutationAsync(seriesId, cancellationToken);
        var character = series?.Characters.SingleOrDefault(candidate => candidate.Id == characterId);
        if (series is null || character is null)
        {
            return null;
        }

        var voice = await ResolveVoiceAsync(request.VoiceProvider, request.Voice, cancellationToken);
        EnsureFormalNarrationAvailable(voice.Provider);
        EnsureSupportedSynthesisParameters(voice.Provider, request.Rate, request.Pitch, request.Volume);

        await EnsureCharacterProfileLinkAllowedAsync(
            ownerId,
            request.CharacterProfileId,
            character.CharacterProfileId,
            cancellationToken);
        EnsureCharacterProfileNotLinkedElsewhere(series, characterId, request.CharacterProfileId);
        EnsureSingleSynthesisProvider(series.NarratorProvider, [voice.Provider]);
        series.UpdateCharacter(
            characterId,
            request.CanonicalName,
            request.Role,
            voice.Provider,
            voice.Voice,
            request.Rate,
            request.Pitch,
            request.Volume,
            request.Notes,
            request.CharacterProfileId);
        await InvalidatePendingRebuildsAsync(series, cancellationToken);
        await SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(series, cancellationToken);
    }

    public async Task<StorySeriesDetailsResponse?> SetCharacterProfileAsync(
        Guid seriesId,
        Guid characterId,
        SetSeriesCharacterProfileRequest request,
        CancellationToken cancellationToken)
    {
        EnsureId(seriesId, nameof(seriesId));
        EnsureId(characterId, nameof(characterId));
        ArgumentNullException.ThrowIfNull(request);
        if (request.CharacterProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "角色庫識別碼在有值時不可為空白 Guid。",
                nameof(request));
        }

        var ownerId = EnsureCurrentOwnerId();
        var series = await repository.GetForMutationAsync(seriesId, cancellationToken);
        var character = series?.Characters.SingleOrDefault(candidate => candidate.Id == characterId);
        if (series is null || character is null)
        {
            return null;
        }

        EnsureCharacterProfileNotLinkedElsewhere(series, characterId, request.CharacterProfileId);
        if (character.CharacterProfileId == request.CharacterProfileId)
        {
            // A no-op remains legal even when a previously linked profile has since been
            // deactivated. This does not create a new inactive link or invalidate staged work.
            return await ToDetailsAsync(series, cancellationToken);
        }

        await EnsureCharacterProfileLinkAllowedAsync(
            ownerId,
            request.CharacterProfileId,
            character.CharacterProfileId,
            cancellationToken);
        series.SetCharacterProfile(characterId, request.CharacterProfileId);
        await InvalidatePendingRebuildsAsync(series, cancellationToken);
        await SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(series, cancellationToken);
    }

    public async Task<StorySeriesDetailsResponse?> AddAliasAsync(
        Guid seriesId,
        Guid characterId,
        AddSeriesCharacterAliasRequest request,
        CancellationToken cancellationToken)
    {
        EnsureId(seriesId, nameof(seriesId));
        EnsureId(characterId, nameof(characterId));
        ArgumentNullException.ThrowIfNull(request);
        var series = await repository.GetForMutationAsync(seriesId, cancellationToken);
        if (series is null || series.Characters.All(character => character.Id != characterId))
        {
            return null;
        }

        series.AddAlias(characterId, request.Alias);
        await InvalidatePendingRebuildsAsync(series, cancellationToken);
        await SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(series, cancellationToken);
    }

    public async Task<StorySeriesDetailsResponse?> SetPointOfViewCharacterAsync(
        Guid seriesId,
        SetSeriesPointOfViewCharacterRequest request,
        CancellationToken cancellationToken)
    {
        EnsureId(seriesId, nameof(seriesId));
        ArgumentNullException.ThrowIfNull(request);
        var series = await repository.GetForMutationAsync(seriesId, cancellationToken);
        if (series is null)
        {
            return null;
        }

        await ConfigureNarrativeVoiceAsync(
            series,
            series.NarrativeVoiceMode,
            request.CharacterId,
            cancellationToken);
        return await ToDetailsAsync(series, cancellationToken);
    }

    public async Task<StorySeriesDetailsResponse?> ConfigureNarrativeVoiceAsync(
        Guid seriesId,
        ConfigureSeriesNarrativeVoiceRequest request,
        CancellationToken cancellationToken)
    {
        EnsureId(seriesId, nameof(seriesId));
        ArgumentNullException.ThrowIfNull(request);
        var series = await repository.GetForMutationAsync(seriesId, cancellationToken);
        if (series is null)
        {
            return null;
        }

        await ConfigureNarrativeVoiceAsync(
            series,
            request.Mode,
            request.PointOfViewCharacterId,
            cancellationToken);
        return await ToDetailsAsync(series, cancellationToken);
    }

    public async Task<StorySeriesDetailsResponse?> ConfigureVoicesAsync(
        Guid seriesId,
        ConfigureSeriesVoicesRequest request,
        CancellationToken cancellationToken)
    {
        EnsureId(seriesId, nameof(seriesId));
        ArgumentNullException.ThrowIfNull(request);
        if (request.Characters is null)
        {
            throw new ArgumentException("角色聲線清單不可為空值。", nameof(request));
        }

        var series = await repository.GetForMutationAsync(seriesId, cancellationToken);
        if (series is null)
        {
            return null;
        }

        if (request.Characters.Count != series.Characters.Count)
        {
            throw new ArgumentException("切換系列聲線時必須包含每一位現有角色。", nameof(request));
        }

        var narratorVoice = await ResolveVoiceAsync(request.NarratorProvider, request.NarratorVoice, cancellationToken);
        EnsureFormalNarrationAvailable(narratorVoice.Provider);
        var assignments = new Dictionary<Guid, SeriesVoiceCatalogEntry>();
        foreach (var assignment in request.Characters)
        {
            ArgumentNullException.ThrowIfNull(assignment);
            EnsureId(assignment.CharacterId, nameof(assignment.CharacterId));
            if (!assignments.TryAdd(
                    assignment.CharacterId,
                    await ResolveVoiceAsync(assignment.VoiceProvider, assignment.Voice, cancellationToken)))
            {
                throw new ArgumentException("同一位角色不可重複指定聲線。", nameof(request));
            }
        }

        if (series.Characters.Any(character => !assignments.ContainsKey(character.Id)))
        {
            throw new ArgumentException("角色聲線清單包含不屬於這個系列的角色。", nameof(request));
        }

        EnsureSingleSynthesisProvider(
            narratorVoice.Provider,
            assignments.Values.Select(voice => voice.Provider));
        foreach (var provider in assignments.Values.Select(voice => voice.Provider).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            EnsureFormalNarrationAvailable(provider);
        }
        await EnsureManagedCharacterVoicesAvailableAsync(series, assignments, cancellationToken);

        var narratorParameters = ResolveEffectiveSynthesisParameters(
            narratorVoice.Provider,
            series.NarratorRate,
            series.NarratorPitch,
            series.NarratorVolume);

        var changed = !string.Equals(
                series.NarratorProvider,
                narratorVoice.Provider,
                StringComparison.Ordinal)
            || !string.Equals(series.NarratorVoice, narratorVoice.Voice, StringComparison.Ordinal)
            || !string.Equals(series.NarratorRate, narratorParameters.Rate, StringComparison.Ordinal)
            || !string.Equals(series.NarratorPitch, narratorParameters.Pitch, StringComparison.Ordinal)
            || !string.Equals(series.NarratorVolume, narratorParameters.Volume, StringComparison.Ordinal)
            || series.Characters.Any(character =>
            {
                var voice = assignments[character.Id];
                var parameters = ResolveEffectiveSynthesisParameters(
                    voice.Provider,
                    character.Rate,
                    character.Pitch,
                    character.Volume);
                return !string.Equals(character.VoiceProvider, voice.Provider, StringComparison.Ordinal)
                    || !string.Equals(character.Voice, voice.Voice, StringComparison.Ordinal)
                    || !string.Equals(character.Rate, parameters.Rate, StringComparison.Ordinal)
                    || !string.Equals(character.Pitch, parameters.Pitch, StringComparison.Ordinal)
                    || !string.Equals(character.Volume, parameters.Volume, StringComparison.Ordinal);
            });
        if (!changed)
        {
            return await ToDetailsAsync(series, cancellationToken);
        }

        // Every catalog entry and the complete provider set is validated before the first domain
        // mutation. SaveChanges then persists the whole cast switch and pending-batch invalidation
        // atomically; confirmed speech-plan/cast revisions and active audio pointers are untouched.
        series.SetNarratorVoice(
            narratorVoice.Provider,
            narratorVoice.Voice,
            narratorParameters.Rate,
            narratorParameters.Pitch,
            narratorParameters.Volume);
        foreach (var character in series.Characters)
        {
            var voice = assignments[character.Id];
            var parameters = ResolveEffectiveSynthesisParameters(
                voice.Provider,
                character.Rate,
                character.Pitch,
                character.Volume);
            series.SetCharacterVoice(
                character.Id,
                voice.Provider,
                voice.Voice,
                parameters.Rate,
                parameters.Pitch,
                parameters.Volume);
        }

        await InvalidatePendingRebuildsAsync(series, cancellationToken);
        await SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(series, cancellationToken);
    }

    private Task EnsureManagedCharacterVoicesAvailableAsync(StorySeries series,
        IReadOnlyDictionary<Guid, SeriesVoiceCatalogEntry> assignments, CancellationToken ct) =>
        ManagedNarrationVoices.RequireAsync(dbContext, series.OwnerId,
            assignments.Values.Select(v => (v.Provider, v.Voice)), ct);

    private async Task ConfigureNarrativeVoiceAsync(
        StorySeries series,
        NarrativeVoiceMode mode,
        Guid? pointOfViewCharacterId,
        CancellationToken cancellationToken)
    {
        var changed = series.NarrativeVoiceMode != mode
            || series.PointOfViewCharacterId != pointOfViewCharacterId;
        series.ConfigureNarrativeVoice(mode, pointOfViewCharacterId);
        if (!changed)
        {
            return;
        }

        var drafts = await dbContext.ChapterSpeechPlanDrafts
            .Where(draft => draft.OwnerId == series.OwnerId && draft.SeriesId == series.Id)
            .ToListAsync(cancellationToken);
        foreach (var draft in drafts)
        {
            draft.MarkStale();
        }

        await InvalidatePendingRebuildsAsync(series, cancellationToken);
        await SaveChangesAsync(cancellationToken);
    }

    private async Task InvalidatePendingRebuildsAsync(
        StorySeries series,
        CancellationToken cancellationToken)
    {
        var batches = await dbContext.SeriesCastRebuildBatches
            .Where(batch => batch.OwnerId == series.OwnerId
                && batch.SeriesId == series.Id
                && (batch.Status == SeriesCastRebuildBatchStatus.Draft
                    || batch.Status == SeriesCastRebuildBatchStatus.Building
                    || batch.Status == SeriesCastRebuildBatchStatus.ReadyToActivate))
            .ToListAsync(cancellationToken);
        var batchIds = batches.Select(batch => batch.Id).ToArray();
        if (batchIds.Length > 0)
        {
            var jobs = await dbContext.NarrationJobs
                .Where(job => job.OwnerId == series.OwnerId
                    && job.SeriesId == series.Id
                    && job.RebuildBatchId != null
                    && batchIds.Contains(job.RebuildBatchId.Value))
                .ToListAsync(cancellationToken);
            foreach (var job in jobs)
            {
                job.RequestCancellation();
            }

            var invalidatedAt = DateTimeOffset.UtcNow;
            foreach (var batch in batches)
            {
                batch.Invalidate(invalidatedAt);
            }
        }
    }

    public async Task<StorySeriesDetailsResponse?> ApplyAnalyzedCharactersAsync(
        Guid seriesId,
        ApplyAnalyzedSeriesCharactersRequest request,
        CancellationToken cancellationToken)
    {
        EnsureId(seriesId, nameof(seriesId));
        ArgumentNullException.ThrowIfNull(request);
        EnsureId(request.BookId, nameof(request.BookId));
        if (request.Characters is null || request.Characters.Count is < 1 or > 60)
        {
            throw new ArgumentException("每次至少選擇 1 位、最多 60 位角色候選。", nameof(request));
        }

        var ownerId = EnsureCurrentOwnerId();
        var seriesExists = await dbContext.StorySeries.AsNoTracking().AnyAsync(
            candidate => candidate.OwnerId == ownerId && candidate.Id == seriesId,
            cancellationToken);
        if (!seriesExists)
        {
            return null;
        }

        var analysis = await dbContext.BookLocalLlmCharacterAnalyses
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.OwnerId == ownerId && item.BookId == request.BookId,
                cancellationToken);
        if (analysis is null)
        {
            throw new InvalidOperationException("請先完成這本書的本機 LLM 角色分析。");
        }

        var analyzedCandidates = JsonSerializer.Deserialize<LocalLlmCharacterAnalysisCandidateResponse[]>(
            analysis.CandidatesJson,
            JsonOptions) ?? [];
        var candidatesByName = analyzedCandidates.ToDictionary(
            candidate => NormalizeIdentityKey(candidate.Name),
            candidate => candidate,
            StringComparer.Ordinal);
        foreach (var selection in request.Characters)
        {
            ArgumentNullException.ThrowIfNull(selection);
            if (!candidatesByName.ContainsKey(NormalizeIdentityKey(selection.SourceName)))
            {
                throw new ArgumentException("角色候選已過期，請重新執行分析後再套用。", nameof(request));
            }

            if (selection.Aliases is null || selection.Aliases.Count > 24)
            {
                throw new ArgumentException("單一角色最多可套用 24 個 alias。", nameof(request));
            }
        }

        var contentBookId = analysis.ContentBookId;
        var book = await dbContext.Books.SingleOrDefaultAsync(
            candidate => candidate.Id == contentBookId && candidate.OwnerId == ownerId,
            cancellationToken);
        if (book is null || book.Status == BookStatus.Linked || book.IsArchived)
        {
            return null;
        }

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var series = await repository.GetForMutationAsync(seriesId, cancellationToken);
            if (series is null)
            {
                return null;
            }

            var originalConcurrencyStamp = series.ConcurrencyStamp;

            if (series.Books.All(member => member.BookId != contentBookId))
            {
                var nextSortOrder = series.Books.Count == 0
                    ? 1
                    : checked(series.Books.Max(member => member.SortOrder) + 1);
                series.AddBook(book, book.Title, nextSortOrder);
            }

            foreach (var selection in request.Characters)
            {
                var voice = await ResolveVoiceAsync(selection.VoiceProvider, selection.Voice, cancellationToken);
                EnsureFormalNarrationAvailable(voice.Provider);
                EnsureSingleSynthesisProvider(series.NarratorProvider, [voice.Provider]);
                EnsureSupportedSynthesisParameters(
                    voice.Provider,
                    selection.Rate,
                    selection.Pitch,
                    selection.Volume);
                var canonicalKey = NormalizeIdentityKey(selection.CanonicalName);
                var existingIdentity = series.IdentityKeys.SingleOrDefault(
                    key => string.Equals(key.NormalizedValue, canonicalKey, StringComparison.Ordinal));
                var character = existingIdentity is null
                    ? series.AddCharacter(
                        selection.CanonicalName,
                        selection.Role,
                        voice.Provider,
                        voice.Voice,
                        selection.Rate,
                        selection.Pitch,
                        selection.Volume,
                        "由本機 LLM 角色候選建立")
                    : series.Characters.Single(item => item.Id == existingIdentity.CharacterId);

                var aliases = new[] { selection.SourceName }
                    .Concat(selection.Aliases)
                    .Select(alias => alias?.Trim() ?? string.Empty)
                    .Where(alias => alias.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                foreach (var alias in aliases)
                {
                    var aliasKey = NormalizeIdentityKey(alias);
                    var identity = series.IdentityKeys.SingleOrDefault(
                        key => string.Equals(key.NormalizedValue, aliasKey, StringComparison.Ordinal));
                    if (identity is not null)
                    {
                        if (identity.CharacterId != character.Id)
                        {
                            throw new InvalidOperationException($"「{alias}」已屬於系列內另一位角色，無法自動合併。");
                        }

                        continue;
                    }

                    series.AddAlias(character.Id, alias);
                }
            }

            if (series.ConcurrencyStamp != originalConcurrencyStamp)
            {
                await InvalidatePendingRebuildsAsync(series, cancellationToken);
            }

            await SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return await ToDetailsAsync(series, cancellationToken);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<SeriesVoiceOptionResponse>> ListVoiceOptionsAsync(CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var profiles = await (from voice in ManagedNarrationVoices.Eligible(dbContext, ownerId)
                              join character in dbContext.CharacterProfiles on voice.CharacterProfileId equals character.Id
                              orderby character.CanonicalName
                              select new { voice.Id, character.CanonicalName, character.Gender, CharacterProfileId = character.Id }).ToListAsync(cancellationToken);
        return profiles.Select(p => new SeriesVoiceOptionResponse(CharacterVoiceProviders.LocalClone,
            ManagedNarrationVoices.Reference(p.Id), p.CanonicalName, "zh-TW",
            localCloneOptions.Value.FormalNarrationEnabled, "private-self-hosted", p.Gender, null, null, [], p.CharacterProfileId)).ToArray();
    }

    private async Task<SeriesVoiceCatalogEntry> ResolveVoiceAsync(string provider, string voice, CancellationToken ct)
    {
        await ManagedNarrationVoices.RequireAsync(dbContext, EnsureCurrentOwnerId(), [(provider, voice)], ct);
        return new SeriesVoiceCatalogEntry { Provider = CharacterVoiceProviders.LocalClone, Voice = ManagedNarrationVoices.Reference(ManagedNarrationVoices.Parse(provider, voice)) };
    }

    private void EnsureFormalNarrationAvailable(string provider)
    {
        if (string.Equals(provider, CharacterVoiceProviders.LocalClone, StringComparison.OrdinalIgnoreCase)
            && !localCloneOptions.Value.FormalNarrationEnabled)
            throw new InvalidOperationException("自架克隆聲線的正式配音尚未由管理員啟用。");
        if (string.Equals(provider, CharacterVoiceProviders.BlueMagpie, StringComparison.OrdinalIgnoreCase)
            && !_blueMagpieFormalNarrationEnabled)
        {
            throw new InvalidOperationException(
                "BlueMagpie 目前只開放固定句試音；正式小說配音尚未由管理員啟用。");
        }

    }

    private static void EnsureSingleSynthesisProvider(
        string narratorProvider,
        IEnumerable<string> characterProviders)
    {
        if (characterProviders.Any(provider => !string.Equals(provider, narratorProvider, StringComparison.Ordinal)))
            throw new ArgumentException("旁白與人物必須使用相同的角色管理聲線供應方式。", nameof(characterProviders));
    }

    private static void EnsureSupportedSynthesisParameters(
        string provider,
        string rate,
        string pitch,
        string volume)
    {
        if (string.Equals(provider, CharacterVoiceProviders.BlueMagpie, StringComparison.OrdinalIgnoreCase)
            && (!string.Equals(rate?.Trim(), NeutralRate, StringComparison.Ordinal)
                || !string.Equals(pitch?.Trim(), NeutralPitch, StringComparison.Ordinal)
                || !string.Equals(volume?.Trim(), NeutralVolume, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "BlueMagpie BM1 目前只支援中性的 +0% 語速、+0Hz 音高與 +0% 音量。",
                nameof(provider));
        }
    }

    private static (string Rate, string Pitch, string Volume) ResolveEffectiveSynthesisParameters(
        string provider,
        string rate,
        string pitch,
        string volume) =>
        string.Equals(provider, CharacterVoiceProviders.BlueMagpie, StringComparison.OrdinalIgnoreCase)
            ? (NeutralRate, NeutralPitch, NeutralVolume)
            : (rate, pitch, volume);

    private async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new InvalidOperationException("系列資料與既有資料衝突，請重新整理後再試。", exception);
        }
    }

    private async Task<StorySeriesDetailsResponse> ToDetailsAsync(
        StorySeries series,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var bookIds = series.Books.Select(book => book.BookId).ToArray();
        var bookTitles = await dbContext.Books
            .AsNoTracking()
            .Where(book => book.OwnerId == ownerId && bookIds.Contains(book.Id))
            .Select(book => new { book.Id, book.Title })
            .ToDictionaryAsync(book => book.Id, book => book.Title, cancellationToken);
        var aliasesByCharacter = series.IdentityKeys
            .Where(key => key.Kind == SeriesCharacterIdentityKeyKind.Alias)
            .GroupBy(key => key.CharacterId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<StorySeriesAliasResponse>)group
                    .OrderBy(key => key.Value, StringComparer.Ordinal)
                    .Select(key => new StorySeriesAliasResponse(key.Id, key.Value))
                    .ToArray());

        return new StorySeriesDetailsResponse(
            series.Id,
            series.Name,
            series.NarratorProvider,
            series.NarratorVoice,
            series.NarratorRate,
            series.NarratorPitch,
            series.NarratorVolume,
            series.DefaultSpeakerPauseMs,
            series.ActiveCastRevisionId,
            series.PointOfViewCharacterId,
            series.NarrativeVoiceMode.ToString(),
            series.Books
                .OrderBy(book => book.SortOrder)
                .Select(book => new StorySeriesBookResponse(
                    book.Id,
                    book.BookId,
                    bookTitles.GetValueOrDefault(book.BookId, "未知書籍"),
                    book.VolumeLabel,
                    book.SortOrder,
                    book.MembershipRevision,
                    book.ActiveNarrationJobId))
                .ToArray(),
            series.Characters
                .OrderBy(character => character.CanonicalName, StringComparer.Ordinal)
                .Select(character => new StorySeriesCharacterResponse(
                    character.Id,
                    character.CanonicalName,
                    character.Role.ToString(),
                    character.VoiceProvider,
                    character.Voice,
                    character.Rate,
                    character.Pitch,
                    character.Volume,
                    character.Notes,
                    character.CharacterProfileId,
                    aliasesByCharacter.GetValueOrDefault(
                        character.Id,
                        Array.Empty<StorySeriesAliasResponse>()),
                    character.CreatedAt,
                    character.UpdatedAt))
                .ToArray(),
            series.CreatedAt,
            series.UpdatedAt);
    }

    private Guid EnsureCurrentOwnerId()
    {
        if (currentUser.UserId == Guid.Empty)
        {
            throw new InvalidOperationException("系列管理需要已驗證的使用者。");
        }

        return currentUser.UserId;
    }

    private async Task EnsureCharacterProfileLinkAllowedAsync(
        Guid ownerId,
        Guid? characterProfileId,
        Guid? currentCharacterProfileId,
        CancellationToken cancellationToken)
    {
        if (characterProfileId is null || characterProfileId == currentCharacterProfileId)
        {
            return;
        }

        var profile = await dbContext.CharacterProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(
                profile => profile.OwnerId == ownerId && profile.Id == characterProfileId,
                cancellationToken);
        if (profile is null)
        {
            throw new InvalidOperationException("找不到指定的角色庫角色。");
        }

        if (!profile.IsActive)
        {
            throw new InvalidOperationException("無法連結已停用的角色庫角色。");
        }
    }

    private static void EnsureCharacterProfileNotLinkedElsewhere(
        StorySeries series,
        Guid? ignoredCharacterId,
        Guid? characterProfileId)
    {
        if (characterProfileId is Guid profileId
            && series.Characters.Any(character =>
                character.Id != ignoredCharacterId
                && character.CharacterProfileId == profileId))
        {
            throw new InvalidOperationException("同一系列內的角色庫角色不可重複連結。");
        }
    }

    private static void EnsureId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("識別碼不可為空白。", parameterName);
        }
    }

    private static string NormalizeIdentityKey(string? value)
    {
        var display = (value ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
        if (display.Length == 0)
        {
            throw new ArgumentException("角色名稱不可空白。", nameof(value));
        }

        return string.Join(' ', display.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
    }
}
