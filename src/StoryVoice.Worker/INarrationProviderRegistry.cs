namespace StoryVoice.Worker;

public interface IMultiVoiceNarrationProvider
{
    string ProviderName { get; }

    Task<MultiVoiceSynthesisResult> SynthesizeAsync(
        MultiVoiceNarrationRequest request,
        string outputPath,
        Func<NarrationSynthesisProgress, CancellationToken, Task>? progressCallback,
        CancellationToken cancellationToken);
}

/// <summary>A provider whose model/runtime identity is part of the durable cast contract.</summary>
public interface IVersionedMultiVoiceNarrationProvider : IMultiVoiceNarrationProvider
{
    string ProviderVersion { get; }
}

public interface INarrationProviderRegistry
{
    IMultiVoiceNarrationProvider Resolve(string providerName);

    IMultiVoiceNarrationProvider ResolveExact(string providerName, string providerVersion);
}

public sealed class NarrationProviderRegistry : INarrationProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IMultiVoiceNarrationProvider> _providersByName;

    public NarrationProviderRegistry(IEnumerable<IMultiVoiceNarrationProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providersByName = providers.ToDictionary(
            provider => provider.ProviderName,
            StringComparer.OrdinalIgnoreCase);
    }

    public IMultiVoiceNarrationProvider Resolve(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName)
            || !_providersByName.TryGetValue(providerName, out var provider))
        {
            throw new InvalidOperationException($"未知的多角色語音 provider：{providerName}。");
        }

        return provider;
    }

    public IMultiVoiceNarrationProvider ResolveExact(string providerName, string providerVersion)
    {
        var provider = Resolve(providerName);
        if (provider is IVersionedMultiVoiceNarrationProvider versionedProvider
            && (!string.Equals(providerName, versionedProvider.ProviderName, StringComparison.Ordinal)
                || !string.Equals(providerVersion, versionedProvider.ProviderVersion, StringComparison.Ordinal)))
        {
            throw new PermanentNarrationProviderException(
                "provider_version_mismatch",
                "The narration provider name or version does not match the pinned runtime.");
        }

        return provider;
    }
}
