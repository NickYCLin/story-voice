using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using StoryVoice.Domain.Books;
using StoryVoice.Domain.Characters;
using StoryVoice.Domain.Collections;
using StoryVoice.Domain.ExternalVoices;
using StoryVoice.Domain.Insights;
using StoryVoice.Domain.Narrations;
using StoryVoice.Domain.Series;
using StoryVoice.Infrastructure.Identity;

namespace StoryVoice.Infrastructure.Persistence;

public sealed class StoryVoiceDbContext(DbContextOptions<StoryVoiceDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Book> Books => Set<Book>();

    public DbSet<Chapter> Chapters => Set<Chapter>();

    public DbSet<ReadingNote> ReadingNotes => Set<ReadingNote>();

    public DbSet<BookExtractiveSummary> BookExtractiveSummaries => Set<BookExtractiveSummary>();

    public DbSet<BookLocalLlmCharacterAnalysis> BookLocalLlmCharacterAnalyses =>
        Set<BookLocalLlmCharacterAnalysis>();

    public DbSet<NarrationJob> NarrationJobs => Set<NarrationJob>();

    public DbSet<NarrationCastRevision> NarrationCastRevisions => Set<NarrationCastRevision>();

    public DbSet<NarrationCastAssignment> NarrationCastAssignments => Set<NarrationCastAssignment>();

    public DbSet<CharacterVoiceProfile> CharacterVoiceProfiles => Set<CharacterVoiceProfile>();

    public DbSet<CharacterVoiceProfileOperation> CharacterVoiceProfileOperations =>
        Set<CharacterVoiceProfileOperation>();

    public DbSet<CharacterProfile> CharacterProfiles => Set<CharacterProfile>();

    public DbSet<SeriesCastRebuildBatch> SeriesCastRebuildBatches => Set<SeriesCastRebuildBatch>();

    public DbSet<SeriesCastRebuildMember> SeriesCastRebuildMembers => Set<SeriesCastRebuildMember>();

    public DbSet<StorySeries> StorySeries => Set<StorySeries>();

    public DbSet<SeriesBook> SeriesBooks => Set<SeriesBook>();

    public DbSet<SeriesCharacter> SeriesCharacters => Set<SeriesCharacter>();

    public DbSet<SeriesCharacterIdentityKey> SeriesCharacterIdentityKeys =>
        Set<SeriesCharacterIdentityKey>();

    public DbSet<BookCollection> BookCollections => Set<BookCollection>();

    public DbSet<BookCollectionBook> BookCollectionBooks => Set<BookCollectionBook>();

    public DbSet<CollectionShare> CollectionShares => Set<CollectionShare>();

    public DbSet<ExternalVoiceCredential> ExternalVoiceCredentials => Set<ExternalVoiceCredential>();

    public DbSet<ExternalVoiceCredentialAudit> ExternalVoiceCredentialAudits =>
        Set<ExternalVoiceCredentialAudit>();

    public DbSet<ChapterSpeechPlanDraft> ChapterSpeechPlanDrafts => Set<ChapterSpeechPlanDraft>();

    public DbSet<SpeechSegmentDraft> SpeechSegmentDrafts => Set<SpeechSegmentDraft>();

    public DbSet<ConfirmedSpeechPlanRevision> ConfirmedSpeechPlanRevisions => Set<ConfirmedSpeechPlanRevision>();

    public DbSet<ConfirmedSpeechSegment> ConfirmedSpeechSegments => Set<ConfirmedSpeechSegment>();

    public DbSet<NarrationJobSpeechPlan> NarrationJobSpeechPlans => Set<NarrationJobSpeechPlan>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StoryVoiceDbContext).Assembly);
    }
}
