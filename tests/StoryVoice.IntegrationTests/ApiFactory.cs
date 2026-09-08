using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryVoice.Application.Insights;
using StoryVoice.Application.Narrations.SpeechPlanning;
using StoryVoice.Domain.Series;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    static ApiFactory()
    {
        Environment.SetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");
    }

    private readonly bool _narrationAdmissionEnabled;
    private readonly ISpeakerAttributionProvider? _attributionProvider;
    private readonly string _databaseName = $"storyvoice-tests-{Guid.NewGuid()}";
    private readonly string _storageRoot = Path.Combine(
        Path.GetTempPath(),
        "storyvoice-integration-tests",
        Guid.NewGuid().ToString("N"));

    public ApiFactory()
        : this(narrationAdmissionEnabled: true)
    {
    }

    internal ApiFactory(ISpeakerAttributionProvider attributionProvider) : this()
    {
        _attributionProvider = attributionProvider;
    }

    internal ApiFactory(bool narrationAdmissionEnabled)
    {
        _narrationAdmissionEnabled = narrationAdmissionEnabled;
    }

    public string StorageRoot => _storageRoot;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("BookStorage:RootPath", _storageRoot);
        builder.UseSetting(
            "CharacterVoiceStorage:RootPath",
            Path.Combine(_storageRoot, "character-voices"));
        builder.UseSetting(
            "CharacterAvatarStorage:RootPath",
            Path.Combine(_storageRoot, "character-avatars"));
        builder.UseSetting("Narration:AudioRootPath", Path.Combine(_storageRoot, "audio"));
        builder.UseSetting(
            "Auth:DataProtectionKeysPath",
            Path.Combine(_storageRoot, "data-protection"));
        builder.UseSetting("Narration:AdmissionEnabled", _narrationAdmissionEnabled ? "true" : "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<StoryVoiceDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<StoryVoiceDbContext>>();
            services.AddDbContext<StoryVoiceDbContext>(options =>
                options
                    .UseInMemoryDatabase(_databaseName)
                    .AddInterceptors(new ComputedCanonicalKindInterceptor()));
            services.RemoveAll<ILocalLlmCharacterAnalysisProvider>();
            services.AddSingleton<ILocalLlmCharacterAnalysisProvider, FakeLocalLlmCharacterAnalysisProvider>();
            services.RemoveAll<ISpeakerAttributionProvider>();
            services.AddSingleton<ISpeakerAttributionProvider>(_attributionProvider ?? new RuleBasedSpeakerAttributionProvider());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    private sealed class FakeLocalLlmCharacterAnalysisProvider(IServiceScopeFactory serviceScopeFactory)
        : ILocalLlmCharacterAnalysisProvider
    {
        public string Model => "fake-local-llm";

        public async Task<IReadOnlyList<LocalLlmChapterCharacterAnalysis>> AnalyzeAsync(
            LocalLlmCharacterAnalysisRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Chapters.Any(chapter => chapter.Text.Contains("模型失敗", StringComparison.Ordinal)))
            {
                throw new LocalLlmCharacterAnalysisUnavailableException();
            }

            if (request.Chapters.Any(chapter => chapter.Text.Contains("正文在分析中變更", StringComparison.Ordinal)))
            {
                await UnlinkAuthorizedContentDuringAnalysisAsync(cancellationToken);
            }

            return request.Chapters.Select(chapter => new LocalLlmChapterCharacterAnalysis(
                chapter.ChapterNumber,
                CreateCandidates(chapter.Text))).ToArray();
        }

        private static IReadOnlyList<LocalLlmCharacterCandidate> CreateCandidates(string chapterText)
        {
            var candidates = new List<LocalLlmCharacterCandidate>();
            if (chapterText.Contains("測試角色", StringComparison.Ordinal))
            {
                candidates.Add(new LocalLlmCharacterCandidate("測試角色", "high", 2, ["隊長"]));
            }

            if (chapterText.Contains("隊長", StringComparison.Ordinal))
            {
                candidates.Add(new LocalLlmCharacterCandidate("隊長", "high", 3));
            }

            return candidates;
        }

        private async Task UnlinkAuthorizedContentDuringAnalysisAsync(CancellationToken cancellationToken)
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var content = await dbContext.Books
                .Include(book => book.Chapters)
                .SingleAsync(
                    book => book.Chapters.Any(chapter => chapter.OriginalText.Contains("正文在分析中變更", StringComparison.Ordinal)),
                    cancellationToken);
            var linkedBook = await dbContext.Books
                .SingleAsync(book => book.ContentBookId == content.Id, cancellationToken);
            linkedBook.UnlinkAuthorizedContent();
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class ComputedCanonicalKindInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            SetComputedValues(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SetComputedValues(eventData.Context);
            return ValueTask.FromResult(result);
        }

        private static void SetComputedValues(DbContext? context)
        {
            if (context is null)
            {
                return;
            }

            foreach (var entry in context.ChangeTracker.Entries<SeriesCharacter>()
                         .Where(entry => entry.State == EntityState.Added))
            {
                entry.Property<string>("CanonicalIdentityKeyKind").CurrentValue = "Canonical";
            }
        }
    }
}
