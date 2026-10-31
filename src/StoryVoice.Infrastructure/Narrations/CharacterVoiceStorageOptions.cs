namespace StoryVoice.Infrastructure.Narrations;

public sealed class CharacterVoiceStorageOptions
{
    public const string SectionName = "CharacterVoiceStorage";

    public string RootPath { get; set; } = "storage/character-voices";
}

/// <summary>Resolves an existing recording inside its physical storage root, including symlink checks.</summary>
public static class CharacterVoiceReferenceFiles
{
    public static string Resolve(string rootPath, string relativePath) =>
        PrivateAssetFileValidator.ResolvePrivateFile(
            PrivateAssetFileValidator.ResolveExistingPhysicalPath(Path.GetFullPath(rootPath), requireDirectory: true),
            relativePath);
}
