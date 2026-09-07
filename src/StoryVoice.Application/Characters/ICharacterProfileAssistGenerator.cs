namespace StoryVoice.Application.Characters;

public interface ICharacterProfileAssistGenerator
{
    Task<GeneratedCharacterProfileAssistResponse> GenerateAsync(
        GenerateCharacterProfileAssistRequest request,
        CancellationToken cancellationToken);
}
