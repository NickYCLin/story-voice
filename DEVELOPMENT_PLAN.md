# StoryVoice

> AI Story Director — 將電子書轉換成具有角色、旁白、情緒與多聲線演出的 AI 有聲書。

> 文件定位（2026-08-30）：本文件是產品願景與長期工作分解，不是發佈狀態頁。
> 下方 Phase 核取方塊已依目前 repository 校正；production 狀態、已驗證範圍與刻意保留的限制
> 請以 [`docs/PROJECT_STATUS.md`](docs/PROJECT_STATUS.md) 為準。

---

## 1. 專案目標

StoryVoice 的目標不是單純把電子書轉成語音，而是讓 AI 先理解故事內容，再將小說轉換成可供語音合成的「演出劇本」。目前支援 EPUB／TXT；PDF、DOCX、MOBI 與 AZW3 仍是未來格式。

系統需要能夠：

1. 讀取使用者合法取得的電子書內容。
2. 解析章節、段落、對話與旁白。
3. 使用 LLM 判斷每一段文字是：
   - 旁白
   - 哪一位角色的台詞
   - 角色目前的情緒
   - 語氣 / 語速 / 停頓需求
4. 建立並維護 Character Bible，確保同一角色跨章節使用相同聲音。
5. 將角色與聲音 Voice Profile 綁定。
6. 呼叫 TTS 引擎生成不同角色的語音。
7. 將各段音訊依劇情順序合成。
8. 提供 Web Player 播放章節、顯示文字、角色與播放進度。
9. 未來可加入背景音效、環境音與 BGM，讓結果接近 AI 廣播劇。

---

# 2. 核心概念

StoryVoice 應被定位為：

```text
Electronic Book
      ↓
Book Parser
      ↓
Story Analyzer
      ↓
Character Bible
      ↓
AI Director
      ↓
Voice Casting
      ↓
TTS
      ↓
Audio Composer
      ↓
Audiobook Player
```

不是：

```text
Electronic Book
      ↓
Text To Speech
```

核心價值在 Story Analyzer、Character Bible 與 AI Director。

---

# 3. MVP 範圍

第一版 MVP 先完成以下功能。

## 必做

- EPUB 上傳
- TXT 上傳
- 電子書 Metadata 解析
- Chapter 解析
- Paragraph 解析
- 對話文字偵測
- LLM Speaker Detection
- Narrator Detection
- Character Detection
- Character Bible
- Voice Profile
- 自動 Voice Casting
- TTS 生成
- Chapter Audio 合成
- Web 播放器
- 播放進度保存
- 任務處理狀態
- 單段重新生成語音

## MVP 暫不做

- DRM 破解
- Kindle / Kobo DRM 解鎖
- Voice Clone（原始 MVP 不含；後續已加入受限的私人 Clone 流程，仍不等於公開產品）
- 自動 BGM
- 自動 Foley 音效
- 多人協作
- 商業化付款
- 手機 App
- 即時串流生成

---

# 4. 建議技術架構

後端：

```text
ASP.NET Core 10
Entity Framework Core
PostgreSQL
Redis
Hangfire / BackgroundService
```

前端：

```text
React
TypeScript
Vite
Tailwind CSS
```

AI：

```text
OpenAI-compatible LLM API
或
Local LLM
```

TTS：

第一階段設計成 Provider Interface，不綁死單一模型。

可支援：

```text
OpenAI TTS
Azure Speech
ElevenLabs
Qwen3-TTS
Kokoro
Local TTS
```

儲存：

```text
Local Storage
或
S3 Compatible Storage
```

音訊處理：

```text
FFmpeg
```

---

# 5. 建議 Solution Structure

```text
StoryVoice/
│
├─ src/
│  │
│  ├─ StoryVoice.Api/
│  │
│  ├─ StoryVoice.Application/
│  │
│  ├─ StoryVoice.Domain/
│  │
│  ├─ StoryVoice.Infrastructure/
│  │
│  ├─ StoryVoice.Worker/
│  │
│  └─ StoryVoice.Web/
│
├─ tests/
│  ├─ StoryVoice.UnitTests/
│  └─ StoryVoice.IntegrationTests/
│
├─ docs/
│  ├─ architecture.md
│  ├─ database.md
│  ├─ prompts.md
│  └─ tts-providers.md
│
├─ docker/
│
├─ scripts/
│
├─ README.md
│
└─ docker-compose.yml
```

---

# 6. Domain Model

## Book

```text
Book
- Id
- Title
- Author
- Language
- CoverUrl
- SourceProvider
- ExternalSourceId
- SourceUrl
- SourceSyncedAt
- OriginalFileName
- FileType
- Status
- CreatedAt
```

Status：

```text
Linked
Uploaded
Parsing
Analyzing
Casting
GeneratingAudio
Ready
Failed
```

---

## Chapter

```text
Chapter
- Id
- BookId
- ChapterNumber
- Title
- OriginalText
- SortOrder
```

---

## StorySegment

整個系統最重要的資料單位。

```text
StorySegment
- Id
- ChapterId
- SortOrder

- OriginalText
- SpeakText

- SegmentType
- CharacterId

- Emotion
- Tone

- Speed
- Volume
- Pitch

- PauseBeforeMs
- PauseAfterMs

- VoiceProfileId

- AudioFileUrl
- AudioDurationMs

- AnalysisStatus
- AudioStatus
```

SegmentType：

```text
Narration
Dialogue
Thought
Description
Unknown
```

---

# 7. Character Bible

Character Bible 用來確保角色在整本書甚至整個系列中的設定一致。

## Character

```text
Character
- Id
- BookId

- Name
- Aliases

- Gender
- EstimatedAge

- Description
- Personality

- SpeakingStyle

- VoiceProfileId

- FirstAppearanceChapterId

- ConfidenceScore
```

例如：

```json
{
  "name": "林雪",
  "aliases": ["小雪", "雪兒"],
  "gender": "female",
  "estimatedAge": 24,
  "personality": [
    "冷靜",
    "理性",
    "不容易表露情緒"
  ],
  "speakingStyle": {
    "speed": "slow",
    "tone": "calm",
    "pitch": "medium"
  }
}
```

---

# 8. Voice Profile

```text
VoiceProfile
- Id

- Provider
- ProviderVoiceId

- DisplayName

- Gender
- AgeStyle
- ToneStyle

- DefaultSpeed
- DefaultPitch

- Description
```

例如：

```json
{
  "provider": "OpenAI",
  "providerVoiceId": "voice_001",
  "displayName": "Young Female Calm",
  "gender": "female",
  "ageStyle": "young",
  "toneStyle": "calm"
}
```

---

# 9. 電子書處理流程

## Step 1 — Upload

使用者上傳：

```text
EPUB
TXT
```

未來：

```text
PDF
DOCX
MOBI
AZW3
```

注意：

系統不應實作 DRM 破解功能。

---

## Step 2 — Parse Book

BookParser 負責：

```text
Metadata
Title
Author
Cover
Table Of Contents
Chapter
Paragraph
```

介面：

```csharp
public interface IBookParser
{
    bool CanHandle(string extension);

    Task<ParsedBook> ParseAsync(
        Stream file,
        CancellationToken cancellationToken);
}
```

實作：

```text
EpubBookParser
TextBookParser
PdfBookParser
```

---

# 10. Story Analyzer

Story Analyzer 為 StoryVoice 的核心。

輸入：

```text
Chapter
+
Character Bible
+
前文 Context
```

輸出：

```text
StorySegment[]
```

例如小說：

```text
小美望著門口，遲遲沒有說話。

「你真的決定了？」她問。

小明笑了笑。

「嗯，走吧。」

外面的雨越下越大。
```

輸出：

```json
[
  {
    "type": "narration",
    "speaker": "Narrator",
    "emotion": "calm",
    "text": "小美望著門口，遲遲沒有說話。"
  },
  {
    "type": "dialogue",
    "speaker": "小美",
    "emotion": "worried",
    "text": "你真的決定了？"
  },
  {
    "type": "narration",
    "speaker": "Narrator",
    "emotion": "calm",
    "text": "她問。小明笑了笑。"
  },
  {
    "type": "dialogue",
    "speaker": "小明",
    "emotion": "relaxed",
    "text": "嗯，走吧。"
  },
  {
    "type": "narration",
    "speaker": "Narrator",
    "emotion": "somber",
    "text": "外面的雨越下越大。"
  }
]
```

---

# 11. LLM 分析策略

不要一次把整本書丟給 LLM。

推薦流程：

```text
Book
 ↓
Chapter
 ↓
Chunk
 ↓
LLM
```

Chunk 可控制：

```text
2,000 ~ 5,000 tokens
```

每次分析時帶入：

```text
Current Chunk

+
Known Characters

+
Previous Summary

+
Previous 3~5 Segments
```

避免失去上下文。

---

# 12. Character Detection Prompt

LLM 必須處理：

```text
角色名稱
角色別名
代名詞
性別
年齡推測
角色身份
說話風格
```

需要避免：

同一角色產生多個 Character。

例如：

```text
林雪
小雪
雪兒
她
```

應盡量映射為：

```text
CharacterId = LIN_XUE
```

---

# 13. Speaker Detection

Speaker Detection 優先順序：

```text
明確角色名稱
↓
對話動詞
↓
代名詞
↓
對話上下文
↓
前後角色
↓
LLM 推論
```

對不確定結果應保存：

```text
ConfidenceScore
```

例如：

```json
{
  "speaker": "林雪",
  "confidence": 0.87
}
```

Confidence 過低：

```text
< 0.65
```

UI 顯示：

```text
Needs Review
```

---

# 14. Emotion Detection

第一版 Emotion 不需要太細。

建議：

```text
neutral
happy
sad
angry
afraid
surprised
excited
calm
serious
whisper
crying
```

未來再增加：

```text
sarcastic
tired
nervous
romantic
cold
confused
```

---

# 15. AI Director

AI Director 不只是決定 Emotion。

每個 StorySegment 應產生：

```text
speaker
emotion
tone
speed
volume
pitch
pauseBefore
pauseAfter
```

例如：

```json
{
  "speaker": "小明",
  "emotion": "afraid",
  "tone": "whisper",
  "speed": 0.85,
  "volume": -4,
  "pitch": -1,
  "pauseBeforeMs": 500,
  "pauseAfterMs": 800,
  "text": "千萬別回頭。"
}
```

第一版如果 Provider 不支援 pitch / emotion，可忽略不支援欄位。

---

# 16. Voice Casting

Voice Casting 根據：

```text
Gender
Age
Personality
Speaking Style
Character Role
```

推薦 Voice。

例如：

```text
Narrator
→ Mature Neutral Voice

林雪
→ Young Female Calm Voice

張浩
→ Young Male Energetic Voice

王教授
→ Mature Male Deep Voice
```

Voice Casting 必須保存。

之後不可每章重新隨機選 Voice。

---

# 17. TTS Provider Architecture

定義抽象介面：

```csharp
public interface ITtsProvider
{
    string Name { get; }

    Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(
        CancellationToken cancellationToken);

    Task<TtsResult> GenerateAsync(
        TtsRequest request,
        CancellationToken cancellationToken);
}
```

TtsRequest：

```csharp
public sealed class TtsRequest
{
    public required string Text { get; init; }

    public required string VoiceId { get; init; }

    public string? Emotion { get; init; }

    public string? Tone { get; init; }

    public double Speed { get; init; } = 1.0;

    public double? Pitch { get; init; }
}
```

Provider：

```text
OpenAiTtsProvider
AzureTtsProvider
ElevenLabsTtsProvider

未來：

QwenTtsProvider
KokoroTtsProvider
```

---

# 18. Audio Generation Flow

不要整章一次產生音訊。

應採：

```text
StorySegment
↓
Generate Audio
↓
Store Audio Segment
```

原因：

- 失敗可以單段重試
- 使用者可以單句修改
- 可以重新指定 Voice
- 不需要整章重新生成
- 容易平行處理
- 未來可以即時播放

---

# 19. Audio Composer

每個 Chapter：

```text
Segment 1.wav
Segment 2.wav
Segment 3.wav
...
```

透過 FFmpeg：

```text
Normalize
↓
Add Silence
↓
Concat
↓
Export
```

輸出：

```text
chapter_001.mp3
chapter_002.mp3
```

未來：

```text
book.m4b
```

---

# 20. Background Jobs

電子書處理不能卡在 HTTP Request。

建議：

```text
Upload API
↓
Create Book
↓
Queue ParseBookJob

ParseBookJob
↓
Queue AnalyzeChapterJob

AnalyzeChapterJob
↓
Queue GenerateAudioJob

GenerateAudioJob
↓
Queue ComposeChapterJob
```

任務狀態：

```text
Pending
Running
Completed
Failed
Retrying
```

---

# 21. 建議 API

## Books

```text
POST   /api/books
GET    /api/books
GET    /api/books/{bookId}
DELETE /api/books/{bookId}
```

## Chapters

```text
GET /api/books/{bookId}/chapters
GET /api/chapters/{chapterId}
```

## Characters

```text
GET  /api/books/{bookId}/characters
GET  /api/characters/{characterId}
PUT  /api/characters/{characterId}
POST /api/characters/{characterId}/voice
```

## Story Segments

```text
GET /api/chapters/{chapterId}/segments

PUT /api/segments/{segmentId}

POST /api/segments/{segmentId}/reanalyze

POST /api/segments/{segmentId}/regenerate-audio
```

## Audio

```text
GET /api/chapters/{chapterId}/audio

POST /api/chapters/{chapterId}/compose
```

---

# 22. Web UI

## Library

顯示：

```text
Cover
Title
Author
Progress
Status
```

---

## Book Detail

顯示：

```text
Cover
Title
Author

Characters

Chapters

Processing Status
```

---

## Character Manager

畫面：

```text
Character

Name
Aliases
Gender
Age
Personality
Voice

[Play Voice Sample]

[Change Voice]
```

---

## Chapter Editor

這個畫面非常重要。

例如：

```text
[Narrator] [Calm]

夜色逐漸籠罩整座城市。

---------------------------------

[林雪] [Worried]

你真的決定了？

Voice:
Female Calm 03

[Play]
[Regenerate]
[Edit]
```

讓使用者能修正：

```text
Speaker
Emotion
Voice
Text
```

---

# 23. Player

播放器至少：

```text
Play
Pause

Previous Chapter
Next Chapter

Seek

Speed

Chapter List

Current Sentence Highlight

Continue Listening
```

播放時可顯示：

```text
角色名稱
+
目前句子
```

---

# 24. Progress Storage

```text
ListeningProgress
- UserId
- BookId
- ChapterId
- SegmentId
- PositionMs
- UpdatedAt
```

---

# 25. Cache

為避免重複花費 TTS 費用：

建立：

```text
AudioHash
```

Hash Input：

```text
Text
+
VoiceId
+
Emotion
+
Speed
+
Pitch
```

例如：

```text
SHA256(...)
```

如果相同內容已生成：

直接使用 Cache。

---

# 26. Cost Control

LLM：

不要整本分析。

使用：

```text
Chunk Analysis
+
Character Bible
+
Summary Memory
```

TTS：

一定要 Cache。

避免：

```text
Regenerate Entire Book
```

只重新生成：

```text
Changed Segment
```

---

# 27. 故事記憶

需要保存每章 Summary。

```text
ChapterSummary

- ChapterId
- Summary
- CharacterChanges
- Relationships
- ImportantEvents
```

例如：

```json
{
  "chapter": 12,
  "summary": "林雪發現張浩隱瞞身份。",
  "characterChanges": [
    {
      "character": "林雪",
      "change": "開始不信任張浩"
    }
  ]
}
```

下一章分析時帶入。

---

# 28. Character Relationship

未來可建立：

```text
CharacterRelationship

CharacterA
CharacterB

Type

Description
```

例如：

```text
林雪
→ 張浩

Relationship:
friend → suspicious
```

提高對話 Speaker 判斷準確度。

---

# 29. Error Handling

所有 Pipeline 都必須：

```text
Retry
Log
Status
Error Message
```

例如：

```text
LLM timeout
TTS timeout
Provider quota
Invalid EPUB
FFmpeg failure
```

---

# 30. Logging

建議 Serilog。

Log 必須包含：

```text
BookId
ChapterId
SegmentId
JobId
Provider
Duration
Token Usage
Cost
```

---

# 31. Security

API Key 不可存進前端。

使用：

```text
Environment Variables
User Secrets
Secret Manager
```

不要 Commit：

```text
OpenAI API Key
Azure Key
ElevenLabs Key
```

---

# 32. DRM 與著作權

StoryVoice 不提供 DRM 破解。

允許來源：

```text
DRM-free EPUB
TXT
使用者自行建立內容
使用者合法取得且有權處理的內容
```

README 應明確註明：

```text
StoryVoice does not provide DRM circumvention features.
Users are responsible for ensuring they have the rights to process uploaded content.
```

---

# 33. Docker Compose

開發環境：

```text
api
web
worker
postgres
redis
```

---

# 34. Phase 1 — Project Foundation

目前已完成：

- [x] 建立 .NET Solution
- [x] 建立 Domain / Application / Infrastructure / API / Worker
- [x] 建立 React Frontend
- [x] PostgreSQL
- [x] EF Core
- [x] Docker Compose
- [x] Serilog
- [x] ASP.NET Core OpenAPI document
- [x] Health Check
- [x] Migration
- [x] Base Exception Handling

完成後：

```text
docker compose up
```

可以啟動整套開發環境。

---

# 35. Phase 2 — Book Import

- [x] Upload API（TXT / EPUB）
- [x] File Storage
- [x] Book Entity
- [x] Chapter Entity
- [x] EPUB Parser
- [x] TXT Parser
- [x] Metadata
- [x] TOC
- [x] Chapter Extraction（TXT / EPUB）
- [x] Book Library UI
- [x] 移除特定書商書櫃同步、Companion 與專用金鑰，書籍來源改以使用者自行匯入檔案為主

Acceptance Criteria：

```text
上傳 EPUB
↓
資料庫出現 Book
↓
解析出 Chapters
↓
前端可看到章節列表

```

---

# 36. Phase 3 — Story Analyzer

- [x] 本機 LLM Provider Interface
- [ ] OpenAI-compatible Provider
- [x] Deterministic speech segmentation（不是通用 LLM chunking）
- [x] Draft／confirmed speech-plan segment model
- [x] Narrator Detection
- [x] Dialogue Detection
- [x] Speaker Detection（規則優先、本機 LLM 補判）
- [x] 合成階段的受限規則式情緒分類（不宣稱通用情感分析）
- [x] Structured JSON Output
- [x] Confidence Score
- [x] Speech-plan review UI

Acceptance Criteria：

輸入：

```text
「你回來了？」小美問。
```

輸出：

```json
{
  "speaker": "小美",
  "type": "dialogue",
  "emotion": "neutral",
  "text": "你回來了？"
}
```

---

# 37. Phase 4 — Character Bible

- [x] Character Entity
- [x] Character Alias
- [x] Character Extraction
- [x] Character Merge
- [x] Character Detail
- [x] Character Manager UI
- [x] Manual Merge
- [x] Manual Rename
- [x] Character Voice Binding

Acceptance Criteria：

```text
小雪
雪兒
林雪
```

AI 能合理判斷是否同一角色。

使用者可以手動 Merge。

---

# 38. Phase 5 — Voice Casting

- [x] VoiceProfile Entity
- [x] TTS Provider Interface／registry
- [x] Voice Listing
- [x] Voice Sample
- [ ] Automatic Casting
- [x] Manual Casting
- [x] Character Voice Lock（不可變 cast revision）

Acceptance Criteria：

同一角色：

```text
Chapter 1
Chapter 20
Chapter 50
```

都必須使用同一 VoiceProfile。

---

# 39. Phase 6 — TTS

- [x] TTS Queue
- [x] Segment Audio
- [x] Audio Cache（BlueMagpie durable chunk cache）
- [x] Retry
- [x] Regenerate / Preview Segment（單段語音試聽與生成效果預覽）
- [ ] Parallel Generation
- [ ] Cost Logging

Acceptance Criteria：

每個 StorySegment 都能：

```text
Generate
Play
Regenerate
```

---

# 40. Phase 7 — Audio Composer

- [x] FFmpeg Integration
- [x] Silence／speaker and chapter pauses
- [x] Volume Normalize（EBU R128 loudnorm 響度標準化）
- [x] MP3 Output
- [x] Book／series narration audio composition
- [x] Duration validation

Acceptance Criteria：

一整章可以輸出：

```text
chapter_001.mp3
```

---

# 41. Phase 8 — Player

- [x] Private HTML audio player
- [ ] Chapter List
- [ ] Current Sentence
- [ ] Current Character
- [x] Seek（browser native control）
- [x] 明確的播放倍速控制（0.75x ~ 2.0x 顯式倍速切換控制項）
- [x] Listening Progress（播放時間記憶與進度持久化）
- [x] Resume（自動續播與接續上次進度）

---

# 42. Phase 9 — AI Director

目前只有合成參數與受限的規則式情緒差值；完整導演語意仍是後續工作。

- [x] Tone／pitch parameter
- [x] Speed／rate parameter
- [x] Pause parameter
- [x] Volume parameter
- [ ] Whisper
- [x] 受限規則式 Emotional Context（Edge only）
- [ ] Dialogue Scene Context

---

# 43. Phase 10 — AI Audio Drama

未來版本。

```text
Scene Detection
↓
Ambient Sound
↓
Sound Effect
↓
BGM
↓
Dialogue
↓
Mix
```

例如：

小說：

```text
門突然被推開。
```

AI Director：

```json
{
  "soundEffect": "door_open"
}
```

小說：

```text
雨越下越大。
```

輸出：

```json
{
  "ambient": "heavy_rain"
}
```

---

# 44. 開發原則

Codex 開發時遵守：

1. Domain 不依賴 Infrastructure。
2. 所有外部 AI / TTS 都使用 Interface。
3. 不在 Controller 放 Business Logic。
4. Background Job 必須 Idempotent。
5. 所有 Provider 必須支援 CancellationToken。
6. 所有外部 API 必須有 Retry / Timeout。
7. 不允許 API Key 寫死。
8. 每個重要功能都需要 Test。
9. 不一次生成整本書。
10. 所有 AI 結果都必須可人工修改。

---

# 45. 優先級

最高：

```text
Book Parser
Story Analyzer
Character Bible
Voice Casting
TTS
Player
```

第二：

```text
Emotion
AI Director
Cost Control
Cache
```

第三：

```text
Background Music
Sound Effects
Voice Clone
Mobile
```

---

# 46. 最終 MVP 使用流程

```text
使用者登入

↓
Upload EPUB

↓
Book Parser

↓
章節解析

↓
LLM Story Analyzer

↓
找出角色

↓
建立 Character Bible

↓
AI Voice Casting

↓
使用者確認角色聲音

↓
Generate Audiobook

↓
Segment TTS

↓
Chapter Compose

↓
Player

↓
聽小說
```

---

# 47. 歷史上的第一個 Codex 任務（已完成）

以下保留最初的 bootstrap 任務作為歷史紀錄；目前 Foundation 與其後多個 Phase 均已實作，不能把這段當成待辦。

第一個 Task：

```text
Create the initial StoryVoice solution architecture.

Backend:
- ASP.NET Core
- Clean Architecture
- PostgreSQL
- EF Core
- Serilog
- OpenAPI document
- Health Checks

Projects:
- StoryVoice.Api
- StoryVoice.Application
- StoryVoice.Domain
- StoryVoice.Infrastructure
- StoryVoice.Worker

Frontend:
- React
- TypeScript
- Vite

Infrastructure:
- PostgreSQL
- Redis
- Docker Compose

Add basic Book and Chapter entities.

Implement:
POST /api/books
GET /api/books
GET /api/books/{id}

Do not implement AI or TTS yet.

The application must run using:

docker compose up
```

Foundation 已完成；目前缺口已直接標在上述 Phase 2～10 清單，並由 `docs/PROJECT_STATUS.md` 記錄驗證邊界。

---

# 48. Repository Description

GitHub Description：

```text
AI Story Director that turns ebooks into multi-character, emotionally narrated audiobooks.
```

---

# 49. Project Tagline

```text
Let AI bring every story to life.
```

或：

```text
Turn books into performances.
```

---

# 50. Long-term Vision

StoryVoice 最終希望做到：

```text
Book
↓
Understand Story
↓
Understand Characters
↓
Understand Emotion
↓
Direct Performance
↓
Generate Voices
↓
Generate Soundscape
↓
AI Audio Drama
```

最終定位：

> StoryVoice is not a text-to-speech tool.
> StoryVoice is an AI Story Director.
