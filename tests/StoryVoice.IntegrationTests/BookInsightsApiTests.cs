using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryVoice.Application.Books;
using StoryVoice.Application.Insights;
using StoryVoice.Application.Series;
using StoryVoice.Domain.Books;
using StoryVoice.Domain.Series;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed class BookInsightsApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Extractive_summary_endpoints_are_retired_for_imported_text()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var book = await ImportTextAsync(client, cancellationToken);

        using var putResponse = await PutWithCsrfAsync(
            client,
            $"/api/books/{book.Id}/summary",
            cancellationToken);
        using var getResponse = await client.GetAsync($"/api/books/{book.Id}/summary", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, putResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Metadata_only_book_accepts_manual_book_note()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var sessionClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var linked = await SeedLegacyLinkedBookAsync(sessionClient, cancellationToken);

        using var noteResponse = await sessionClient.PostWithCsrfAsync(
            $"/api/books/{linked.Id}/notes",
            new CreateReadingNoteRequest("這是我自己的閱讀備忘。", null),
            cancellationToken);
        var note = await noteResponse.Content.ReadFromJsonAsync<ReadingNoteResponse>(cancellationToken);

        Assert.Equal(HttpStatusCode.Created, noteResponse.StatusCode);
        Assert.NotNull(note);
        Assert.Null(note.ChapterId);
    }

    [Fact]
    public async Task Explicit_owner_scoped_link_enables_chapter_note()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var sessionClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var linked = await SeedLegacyLinkedBookAsync(sessionClient, cancellationToken);
        var content = await ImportTextAsync(sessionClient, cancellationToken);

        using var linkResponse = await PutWithCsrfAsync(
            sessionClient,
            $"/api/books/{linked.Id}/content-link",
            new SetBookContentLinkRequest(content.Id),
            cancellationToken);
        var link = await linkResponse.Content.ReadFromJsonAsync<BookContentLinkResponse>(cancellationToken);
        using var noteResponse = await sessionClient.PostWithCsrfAsync(
            $"/api/books/{linked.Id}/notes",
            new CreateReadingNoteRequest("第一章的手動筆記。", content.Chapters[0].Id),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, linkResponse.StatusCode);
        Assert.NotNull(link);
        Assert.Equal(content.Id, link.ContentBookId);
        Assert.Equal(HttpStatusCode.Created, noteResponse.StatusCode);
    }

    [Fact]
    public async Task Notes_and_content_links_are_owner_isolated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var other = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var book = await ImportTextAsync(owner, cancellationToken);
        using var createNote = await owner.PostWithCsrfAsync(
            $"/api/books/{book.Id}/notes",
            new CreateReadingNoteRequest("只有擁有者看得到。", null),
            cancellationToken);

        using var listAsOther = await other.GetAsync($"/api/books/{book.Id}/notes", cancellationToken);
        using var linkAsOther = await PutWithCsrfAsync(
            other,
            $"/api/books/{book.Id}/content-link",
            new SetBookContentLinkRequest(book.Id),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, createNote.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, listAsOther.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, linkAsOther.StatusCode);
    }

    [Fact]
    public async Task External_metadata_corrections_are_owner_scoped_and_reversible()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var other = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var linked = await SeedLegacyLinkedBookAsync(owner, cancellationToken);

        using var update = await PutWithCsrfAsync(
            owner,
            $"/api/books/{linked.Id}/metadata-corrections",
            new UpdateBookMetadataCorrectionsRequest(
                "人工校正書名",
                "人工校正作者",
                "https://example.test/corrected-cover.jpg"),
            cancellationToken);
        var corrected = await update.Content.ReadFromJsonAsync<BookDetailsResponse>(cancellationToken);
        using var otherUpdate = await PutWithCsrfAsync(
            other,
            $"/api/books/{linked.Id}/metadata-corrections",
            new UpdateBookMetadataCorrectionsRequest("越權", null, null),
            cancellationToken);
        using var clear = await PutWithCsrfAsync(
            owner,
            $"/api/books/{linked.Id}/metadata-corrections",
            new UpdateBookMetadataCorrectionsRequest(null, null, null),
            cancellationToken);
        var restored = await clear.Content.ReadFromJsonAsync<BookDetailsResponse>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.NotNull(corrected);
        Assert.Equal("人工校正書名", corrected.Title);
        Assert.Equal("人工校正作者", corrected.Author);
        Assert.Equal("https://example.test/corrected-cover.jpg", corrected.CoverImageUrl);
        Assert.Equal(HttpStatusCode.NotFound, otherUpdate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.NotNull(restored);
        Assert.Equal("外部書目", restored.Title);
        Assert.Equal("測試作者", restored.Author);
        Assert.Null(restored.TitleCorrection);
    }

    [Fact]
    public async Task Uploaded_book_accepts_manual_cover_correction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var uploaded = await ImportTextAsync(client, cancellationToken);

        using var response = await PutWithCsrfAsync(
            client,
            $"/api/books/{uploaded.Id}/metadata-corrections",
            new UpdateBookMetadataCorrectionsRequest(null, null, "https://example.test/corrected-cover.jpg"),
            cancellationToken);
        var corrected = await response.Content.ReadFromJsonAsync<BookDetailsResponse>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(corrected);
        Assert.Equal("https://example.test/corrected-cover.jpg", corrected.CoverImageUrl);
    }

    [Fact]
    public async Task Synthetic_book_chapters_cannot_receive_chapter_notes_without_upload_provenance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var create = await client.PostWithCsrfAsync(
            "/api/books/",
            new CreateBookRequest(
                "合成測試書",
                "測試作者",
                "zh-TW",
                "synthetic.txt",
                [new CreateChapterRequest(1, "第一章", "沒有上傳來源的測試文字。")]),
            cancellationToken);
        var book = await create.Content.ReadFromJsonAsync<BookDetailsResponse>(cancellationToken);
        Assert.NotNull(book);

        using var note = await client.PostWithCsrfAsync(
            $"/api/books/{book.Id}/notes",
            new CreateReadingNoteRequest("不可掛到未授權章節。", book.Chapters[0].Id),
            cancellationToken);
        using var problem = JsonDocument.Parse(await note.Content.ReadAsStreamAsync(cancellationToken));

        Assert.Equal(HttpStatusCode.Conflict, note.StatusCode);
        Assert.Equal(BookTextUnavailableException.StableCode, problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Replacing_or_removing_content_link_detaches_existing_chapter_notes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var linked = await SeedLegacyLinkedBookAsync(client, cancellationToken);
        var firstContent = await ImportTextAsync(client, cancellationToken);
        var secondContent = await ImportTextAsync(client, cancellationToken);

        using var firstLink = await PutWithCsrfAsync(
            client,
            $"/api/books/{linked.Id}/content-link",
            new SetBookContentLinkRequest(firstContent.Id),
            cancellationToken);
        firstLink.EnsureSuccessStatusCode();
        using var firstNote = await client.PostWithCsrfAsync(
            $"/api/books/{linked.Id}/notes",
            new CreateReadingNoteRequest("換綁後保留為書籍筆記。", firstContent.Chapters[0].Id),
            cancellationToken);
        firstNote.EnsureSuccessStatusCode();

        using var replacement = await PutWithCsrfAsync(
            client,
            $"/api/books/{linked.Id}/content-link",
            new SetBookContentLinkRequest(secondContent.Id),
            cancellationToken);
        replacement.EnsureSuccessStatusCode();
        using var afterReplacement = await client.GetAsync($"/api/books/{linked.Id}/notes", cancellationToken);
        var replacedNotes = await afterReplacement.Content.ReadFromJsonAsync<ReadingNoteResponse[]>(cancellationToken);
        Assert.NotNull(replacedNotes);
        Assert.All(replacedNotes, note => Assert.Null(note.ChapterId));

        using var secondNote = await client.PostWithCsrfAsync(
            $"/api/books/{linked.Id}/notes",
            new CreateReadingNoteRequest("解綁後也保留為書籍筆記。", secondContent.Chapters[0].Id),
            cancellationToken);
        secondNote.EnsureSuccessStatusCode();
        using var unlink = await DeleteWithCsrfAsync(client, $"/api/books/{linked.Id}/content-link", cancellationToken);
        using var afterUnlink = await client.GetAsync($"/api/books/{linked.Id}/notes", cancellationToken);
        var unlinkedNotes = await afterUnlink.Content.ReadFromJsonAsync<ReadingNoteResponse[]>(cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, unlink.StatusCode);
        Assert.NotNull(unlinkedNotes);
        Assert.Equal(2, unlinkedNotes.Length);
        Assert.All(unlinkedNotes, note => Assert.Null(note.ChapterId));
    }

    [Fact]
    public async Task Removing_nonexistent_link_preserves_direct_upload_chapter_notes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var uploaded = await ImportTextAsync(client, cancellationToken);

        using var note = await client.PostWithCsrfAsync(
            $"/api/books/{uploaded.Id}/notes",
            new CreateReadingNoteRequest("直傳正文的章節筆記。", uploaded.Chapters[0].Id),
            cancellationToken);
        note.EnsureSuccessStatusCode();

        using var unlink = await DeleteWithCsrfAsync(
            client,
            $"/api/books/{uploaded.Id}/content-link",
            cancellationToken);
        using var notesResponse = await client.GetAsync($"/api/books/{uploaded.Id}/notes", cancellationToken);
        var notes = await notesResponse.Content.ReadFromJsonAsync<ReadingNoteResponse[]>(cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, unlink.StatusCode);
        Assert.NotNull(notes);
        Assert.Single(notes);
        Assert.Equal(uploaded.Chapters[0].Id, notes[0].ChapterId);
    }

    [Fact]
    public async Task Local_LLM_character_analysis_is_explicitly_generated_saved_and_owner_scoped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var other = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var book = await ImportTextAsync(
            owner,
            """
            第一章 本機分析
            測試角色說：「這是可由本機模型確認的對白。」
            """,
            cancellationToken);

        using var beforeGenerate = await owner.GetAsync($"/api/books/{book.Id}/character-analysis", cancellationToken);
        using var generate = await PutWithCsrfAsync(owner, $"/api/books/{book.Id}/character-analysis", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, generate.StatusCode);
        var generated = await generate.Content.ReadFromJsonAsync<LocalLlmCharacterAnalysisResponse>(cancellationToken);
        using var cached = await owner.GetAsync($"/api/books/{book.Id}/character-analysis", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        var cachedResult = await cached.Content.ReadFromJsonAsync<LocalLlmCharacterAnalysisResponse>(cancellationToken);
        using var otherResponse = await other.GetAsync($"/api/books/{book.Id}/character-analysis", cancellationToken);
        using var retiredEndpoint = await owner.GetAsync($"/api/books/{book.Id}/character-candidates", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, beforeGenerate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, generate.StatusCode);
        Assert.NotNull(generated);
        Assert.Equal("local-ollama", generated.Generator);
        Assert.Equal("fake-local-llm", generated.Model);
        var candidate = Assert.Single(generated.Candidates);
        Assert.Equal("測試角色", candidate.Name);
        Assert.Equal("high", candidate.Confidence);
        Assert.Equal(2, candidate.DialogueEvidenceCount);
        Assert.Equal([1], candidate.EvidenceChapterNumbers);
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        Assert.NotNull(cachedResult);
        Assert.Equal(generated.SourceHash, cachedResult.SourceHash);
        Assert.Equal(HttpStatusCode.NotFound, otherResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, retiredEndpoint.StatusCode);
    }

    [Fact]
    public async Task Local_LLM_character_analysis_maps_registered_series_alias_to_canonical_name()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var book = await ImportTextAsync(
            owner,
            """
            第一章 系列別名
            隊長說：「這段對白應映射到正式角色名稱。」
            """,
            cancellationToken);

        using var createSeries = await owner.PostWithCsrfAsync(
            "/api/series",
            new CreateStorySeriesRequest(
                $"角色分析別名-{Guid.NewGuid():N}",
                "edge",
                "zh-TW-YunJheNeural",
                "+0%",
                "+0Hz",
                "+0%",
                350),
            cancellationToken);
        createSeries.EnsureSuccessStatusCode();
        var series = await createSeries.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        Assert.NotNull(series);

        using var addBook = await owner.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books",
            new AddSeriesBookRequest(book.Id, "第一冊", 1),
            cancellationToken);
        addBook.EnsureSuccessStatusCode();
        using var addCharacter = await owner.PostWithCsrfAsync(
            $"/api/series/{series.Id}/characters",
            new AddSeriesCharacterRequest(
                "艾莉絲",
                SeriesCharacterRole.Main,
                "edge",
                "zh-TW-HsiaoChenNeural",
                "+0%",
                "+0Hz",
                "+0%",
                null),
            cancellationToken);
        addCharacter.EnsureSuccessStatusCode();
        var withCharacter = await addCharacter.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        Assert.NotNull(withCharacter);
        var character = Assert.Single(withCharacter.Characters);
        using var addAlias = await owner.PostWithCsrfAsync(
            $"/api/series/{series.Id}/characters/{character.Id}/aliases",
            new AddSeriesCharacterAliasRequest("隊長"),
            cancellationToken);
        addAlias.EnsureSuccessStatusCode();

        using var generate = await PutWithCsrfAsync(
            owner,
            $"/api/books/{book.Id}/character-analysis",
            cancellationToken);
        var analysis = await generate.Content.ReadFromJsonAsync<LocalLlmCharacterAnalysisResponse>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, generate.StatusCode);
        Assert.NotNull(analysis);
        var candidate = Assert.Single(analysis.Candidates);
        Assert.Equal("艾莉絲", candidate.Name);
        Assert.Equal(3, candidate.DialogueEvidenceCount);
    }

    [Fact]
    public async Task Reviewed_character_candidates_atomically_join_book_create_cast_merge_aliases_and_are_idempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var other = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var book = await ImportTextAsync(
            owner,
            """
            第一章 角色候選
            測試角色又被稱為隊長。測試角色說：「開始建立角色表。」
            """,
            cancellationToken);
        using var generate = await PutWithCsrfAsync(
            owner,
            $"/api/books/{book.Id}/character-analysis",
            cancellationToken);
        generate.EnsureSuccessStatusCode();
        using var createSeries = await owner.PostWithCsrfAsync(
            "/api/series",
            new CreateStorySeriesRequest(
                $"候選套用-{Guid.NewGuid():N}",
                "edge",
                "zh-TW-YunJheNeural",
                "+0%",
                "+0Hz",
                "+0%",
                350),
            cancellationToken);
        createSeries.EnsureSuccessStatusCode();
        var created = await createSeries.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        Assert.NotNull(created);
        var request = new ApplyAnalyzedSeriesCharactersRequest(
            book.Id,
            [
                new ApplyAnalyzedSeriesCharacterRequest(
                    "測試角色",
                    "主角",
                    ["英雄", "隊長"],
                    SeriesCharacterRole.Main,
                    "edge",
                    "zh-TW-HsiaoChenNeural",
                    "+0%",
                    "+0Hz",
                    "+0%"),
            ]);

        using var mixedProviderApply = await owner.PostWithCsrfAsync(
            $"/api/series/{created.Id}/analyzed-characters",
            request with
            {
                Characters =
                [
                    request.Characters[0] with
                    {
                        VoiceProvider = "voai",
                        Voice = "v1:Neo:佑希:預設"
                    }
                ]
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, mixedProviderApply.StatusCode);

        using var apply = await owner.PostWithCsrfAsync(
            $"/api/series/{created.Id}/analyzed-characters",
            request,
            cancellationToken);
        using var replay = await owner.PostWithCsrfAsync(
            $"/api/series/{created.Id}/analyzed-characters",
            request,
            cancellationToken);
        using var otherApply = await other.PostWithCsrfAsync(
            $"/api/series/{created.Id}/analyzed-characters",
            request,
            cancellationToken);
        var applied = await apply.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        var replayed = await replay.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherApply.StatusCode);
        Assert.NotNull(applied);
        Assert.NotNull(replayed);
        Assert.Equal(book.Id, Assert.Single(applied.Books).BookId);
        var character = Assert.Single(applied.Characters);
        Assert.Equal("主角", character.CanonicalName);
        Assert.Equal(
            ["測試角色", "英雄", "隊長"],
            character.Aliases.Select(alias => alias.Value).Order(StringComparer.Ordinal).ToArray());
        Assert.Single(replayed.Books);
        Assert.Single(replayed.Characters);
        Assert.Equal(3, replayed.Characters[0].Aliases.Count);
    }

    [Fact]
    public async Task Local_LLM_failure_returns_503_and_does_not_save_a_partial_analysis()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var book = await ImportTextAsync(
            owner,
            """
            第一章 本機分析失敗
            模型失敗說：「這是測試用的受控 provider failure。」
            """,
            cancellationToken);

        using var generate = await PutWithCsrfAsync(owner, $"/api/books/{book.Id}/character-analysis", cancellationToken);
        using var cached = await owner.GetAsync($"/api/books/{book.Id}/character-analysis", cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, generate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cached.StatusCode);
    }

    [Fact]
    public async Task Local_LLM_character_analysis_rejects_oversized_full_context_without_truncation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var book = await ImportTextAsync(
            client,
            $"第一章 上限\n{new string('字', 8_001)}",
            cancellationToken);

        using var response = await PutWithCsrfAsync(
            client,
            $"/api/books/{book.Id}/character-analysis",
            cancellationToken);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(
            LocalLlmCharacterAnalysisInputTooLargeException.StableCode,
            problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Local_LLM_analysis_discards_result_when_authorized_content_changes_mid_analysis()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var sessionClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var content = await ImportTextAsync(
            sessionClient,
            """
            第一章 變更防護
            正文在分析中變更，這段文字只供受控 race test 使用。
            """,
            cancellationToken);
        var linked = await SeedLegacyLinkedBookAsync(sessionClient, cancellationToken);
        using var link = await PutWithCsrfAsync(
            sessionClient,
            $"/api/books/{linked.Id}/content-link",
            new SetBookContentLinkRequest(content.Id),
            cancellationToken);
        using var generate = await PutWithCsrfAsync(
            sessionClient,
            $"/api/books/{linked.Id}/character-analysis",
            cancellationToken);
        using var cached = await sessionClient.GetAsync($"/api/books/{linked.Id}/character-analysis", cancellationToken);
        using var problem = JsonDocument.Parse(await generate.Content.ReadAsStreamAsync(cancellationToken));

        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, generate.StatusCode);
        Assert.Equal(LocalLlmCharacterAnalysisSourceChangedException.StableCode, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, cached.StatusCode);
    }

    [Fact]
    public async Task Local_LLM_character_analysis_requires_processable_authorized_text()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var sessionClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var linked = await SeedLegacyLinkedBookAsync(sessionClient, cancellationToken);

        using var response = await PutWithCsrfAsync(
            sessionClient,
            $"/api/books/{linked.Id}/character-analysis",
            cancellationToken);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BookTextUnavailableException.StableCode, problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task<BookDetailsResponse> ImportTextAsync(
        HttpClient client,
        string text,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", $"authorized-{Guid.NewGuid():N}.txt");
        using var response = await client.PostMultipartWithCsrfAsync(
            "/api/books/import",
            content,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BookDetailsResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Import response did not contain a book.");
    }

    private static async Task<BookDetailsResponse> ImportTextAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("""
            第一章 起點
            月色落在窗前。這是後續句子。

            第二章 回聲
            風裡傳來回答！這是第二段。
            """));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", $"authorized-{Guid.NewGuid():N}.txt");
        using var response = await client.PostMultipartWithCsrfAsync(
            "/api/books/import",
            content,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BookDetailsResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Import response did not contain a book.");
    }

    private async Task<BookDetailsResponse> SeedLegacyLinkedBookAsync(
        HttpClient sessionClient,
        CancellationToken cancellationToken)
    {
        using var sessionResponse = await sessionClient.GetAsync(
            "/api/auth/session",
            cancellationToken);
        sessionResponse.EnsureSuccessStatusCode();
        using var session = JsonDocument.Parse(
            await sessionResponse.Content.ReadAsStreamAsync(cancellationToken));
        var email = session.RootElement.GetProperty("email").GetString()
            ?? throw new InvalidOperationException("Authenticated session did not return an email.");

        var externalId = $"legacy-{Guid.NewGuid():N}";
        Guid id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var ownerId = await dbContext.Users
                .Where(user => user.Email == email)
                .Select(user => user.Id)
                .SingleAsync(cancellationToken);
            var book = Book.CreateExternal(
                ownerId,
                "外部書目",
                "測試作者",
                "zh-TW",
                "legacy-external",
                externalId,
                $"https://example.test/books/{externalId}",
                coverImageUrl: null);
            id = book.Id;
            dbContext.Books.Add(book);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return await sessionClient.GetFromJsonAsync<BookDetailsResponse>(
                $"/api/books/{id}",
                cancellationToken)
            ?? throw new InvalidOperationException("Legacy linked book was not returned.");
    }

    private static async Task<HttpResponseMessage> PutWithCsrfAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path);
        return await client.SendWithCsrfAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> DeleteWithCsrfAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        return await client.SendWithCsrfAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> PutWithCsrfAsync<T>(
        HttpClient client,
        string path,
        T? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendWithCsrfAsync(request, cancellationToken);
    }
}
