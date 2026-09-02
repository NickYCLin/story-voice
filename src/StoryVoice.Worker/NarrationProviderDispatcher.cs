namespace StoryVoice.Worker;

/// <summary>
/// Routes a multi-character synthesis request to whichever <see cref="IMultiVoiceNarrationProvider"/>
/// is registered for the cast revision's <c>VoiceProvider</c> — the compatibility boundary that
/// lets a new provider (Azure Speech, a local TTS engine, ...) be added later without touching
/// any series/character/cast-revision IDs already on disk.
/// </summary>
public sealed class NarrationProviderDispatcher(INarrationProviderRegistry registry)
{
    public async Task<MultiVoiceSynthesisResult> SynthesizeAsync(
        string providerName,
        MultiVoiceNarrationRequest request,
        string outputPath,
        Func<NarrationSynthesisProgress, CancellationToken, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = registry.Resolve(providerName);
        if (provider is IVersionedMultiVoiceNarrationProvider versionedProvider)
        {
            NarrationProviderContractValidator.Validate(versionedProvider, providerName, request);
            provider = registry.ResolveExact(
                request.NarratorProvider!.ProviderName,
                request.NarratorProvider.ProviderVersion);
        }

        return await provider.SynthesizeAsync(request, outputPath, progressCallback, cancellationToken);
    }
}

internal static class NarrationProviderContractValidator
{
    public static void Validate(
        IVersionedMultiVoiceNarrationProvider provider,
        string requestedProviderName,
        MultiVoiceNarrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        var narrator = request.NarratorProvider;
        if (narrator is null
            || !Matches(provider, narrator)
            || !string.Equals(requestedProviderName, narrator.ProviderName, StringComparison.Ordinal))
        {
            ThrowMismatch();
        }

        foreach (var character in request.CharacterProviders ?? [])
        {
            if (!Matches(provider, character))
            {
                ThrowMismatch();
            }
        }
    }

    private static bool Matches(
        IVersionedMultiVoiceNarrationProvider provider,
        NarrationProviderContract contract) =>
        string.Equals(contract.ProviderName, provider.ProviderName, StringComparison.Ordinal)
        && string.Equals(contract.ProviderVersion, provider.ProviderVersion, StringComparison.Ordinal);

    private static void ThrowMismatch() =>
        throw new PermanentNarrationProviderException(
            "provider_version_mismatch",
            "The narration cast provider name or version does not match the pinned runtime.");
}
