namespace StoryVoice.Worker;

/// <summary>
/// One synthesized turn in a multi-character narration job: either the series narrator or a
/// specific character's fixed voice fingerprint. <see cref="PauseBeforeMs"/> is the only pause
/// field — the silence that should play immediately before this turn's audio — so consecutive
/// turns never double-count a gap between them.
/// </summary>
public sealed record NarrationTurn(
    string Text,
    string Voice,
    string Rate,
    string Pitch,
    string Volume,
    int PauseBeforeMs);

/// <summary>
/// The immutable provider identity captured with a cast revision. Keeping this beside the turns
/// lets a version-pinned provider reject stale or mixed cast snapshots before it makes any
/// synthesis request.
/// </summary>
public sealed record NarrationProviderContract(string ProviderName, string ProviderVersion);

/// <summary>
/// Immutable, non-secret identity for resumable synthesis artifacts. Providers that persist
/// intermediate audio must include every field in their cache scope instead of inferring identity
/// from the attempt-specific output path.
/// </summary>
public sealed record NarrationSynthesisCacheContext(
    Guid OwnerId,
    Guid JobId,
    string SourceHash,
    Guid CastRevisionId,
    string CastFingerprint,
    string SpeechPlanFingerprint,
    string CompositionVersion,
    string FfmpegProfile);

public sealed record MultiVoiceNarrationRequest(
    IReadOnlyList<NarrationTurn> Turns,
    NarrationProviderContract? NarratorProvider = null,
    IReadOnlyList<NarrationProviderContract>? CharacterProviders = null,
    NarrationSynthesisCacheContext? CacheContext = null,
    IReadOnlyDictionary<string, LocalCloneVoiceReference>? CloneReferences = null);

/// <summary>
/// Where one turn's audible audio starts inside the finished MP3 and how long it plays.
/// <see cref="StartMs"/> points at the first audible sample of the turn — after any
/// <see cref="NarrationTurn.PauseBeforeMs"/> silence — so seeking to it never lands in a gap.
/// </summary>
public sealed record NarrationTurnTiming(int TurnIndex, long StartMs, long DurationMs);

/// <summary>
/// What a multi-voice provider can report about the audio it just produced. Timing is optional by
/// design: a provider that cannot measure per-turn durations returns <see cref="None"/> and the
/// job still completes — it just ships without a playback timeline.
/// </summary>
public sealed record MultiVoiceSynthesisResult(IReadOnlyList<NarrationTurnTiming>? TurnTimings)
{
    public static readonly MultiVoiceSynthesisResult None = new((IReadOnlyList<NarrationTurnTiming>?)null);
}

public sealed record LocalCloneVoiceReference(string ReferenceAudioPath, string ReferenceTranscript, string ReferenceAudioSha256);

public sealed class LocalCloneIntegrityReceipt(NarrationSynthesisCacheContext? context, int inputUtf16Units)
{
    public string Schema => "storyvoice-local-clone-integrity/v1";
    public NarrationSynthesisCacheContext? Context { get; } = context;
    public int InputUtf16Units { get; } = inputUtf16Units;
    public string Status { get; set; } = "building";
    public string? AudioSha256 { get; set; }
    public long? AudioBytes { get; set; }
    public List<LocalCloneChunkEvidence> Chunks { get; } = [];
}

public sealed class LocalCloneChunkEvidence(int turnIndex, int chunkIndex, int inputUtf16Units,
    string textSha256, string disposition, string? audioSha256, long? audioBytes)
{
    public int TurnIndex { get; } = turnIndex;
    public int ChunkIndex { get; } = chunkIndex;
    public int InputUtf16Units { get; } = inputUtf16Units;
    public string TextSha256 { get; } = textSha256;
    public string Disposition { get; set; } = disposition;
    public string? AudioSha256 { get; set; } = audioSha256;
    public long? AudioBytes { get; set; } = audioBytes;
}
