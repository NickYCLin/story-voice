# StoryVoice — 自架式多角色有聲書製作平台

[![CI](https://github.com/NickYCLin/story-voice/actions/workflows/ci.yml/badge.svg)](https://github.com/NickYCLin/story-voice/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> **Self-hosted multi-character audiobook production for EPUB and TXT, with human-in-the-loop story analysis, voice casting and text-to-speech.**

StoryVoice 是開源、自架式的 EPUB／TXT 有聲書製作工具。它會整理章節、角色與對話，讓使用者
逐章確認說話者和聲線，再以多角色 TTS 產製可重試、可分段重建的朗讀音訊。重點不是一鍵把整本
書丟進語音引擎，而是保留 **Story Analyzer、Character Bible、Voice Casting、Speech Plan**
與人工審核流程。

English summary: StoryVoice is an open-source audiobook generator and voice-production platform built
with ASP.NET Core and React. It turns authorized DRM-free EPUB/TXT content into reviewable,
multi-speaker narration and supports self-hosted or pluggable speech-synthesis providers.

- 正式站：<https://aiprod.wrbtycg.tw/StoryVoice/>
- 開發進度：[docs/PROJECT_STATUS.md](docs/PROJECT_STATUS.md)
- 多角色製作規劃：[DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md)

## 可以用它做什麼

- 匯入使用者有權處理的 DRM-free EPUB／TXT，保存目錄、章節順序與原始正文。
- 以規則和本機 LLM 輔助找出角色、alias、旁白、對話及說話者，低信心結果保留人工確認。
- 維護跨冊一致的角色表、固定聲線、敘述模式與不可變 cast revision。
- 逐章審核 speech plan，再建立 staged 多角色朗讀；確認後才原子切換正式音訊。
- 透過 provider boundary 串接 Edge TTS、BlueMagpie、3wa／VoxCPM2、VoAI 或內網語音服務。
- 以 Web UI、REST API、背景 Worker、PostgreSQL、Redis 與 Docker Compose 自架完整流程。

適合想研究或實作 `EPUB-to-audiobook`、`multi-speaker TTS`、`voice casting`、
`human-in-the-loop speech synthesis`、台灣華語語音產製或長篇故事角色一致性的工程師。

## 技術棧

| 區域 | 使用技術 |
|---|---|
| Backend | .NET 10、ASP.NET Core、EF Core、PostgreSQL、Serilog、OpenAPI |
| Frontend | React 19、TypeScript、Vite、Tailwind CSS 4、React Router |
| Audio / jobs | Background Worker、Redis、FFmpeg／ffprobe、Edge TTS 與可插拔 TTS provider |
| Deployment | Docker Compose、nginx reverse proxy、GitHub Actions CI |

## 文件與程式碼入口

| 想了解的內容 | 建議先看 |
|---|---|
| 實際完成度與未完成項目 | [docs/PROJECT_STATUS.md](docs/PROJECT_STATUS.md) |
| 系列、角色、speech plan 與 staged narration 設計 | [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md) |
| External voice API、credential 與 idempotency | [docs/EXTERNAL_VOICE_API.md](docs/EXTERNAL_VOICE_API.md) |
| 聲線授權與公開／商用邊界 | [docs/VOICE_PUBLICATION_GRANT.md](docs/VOICE_PUBLICATION_GRANT.md) |
| API 啟動與 endpoint 組裝 | [src/StoryVoice.Api/Program.cs](src/StoryVoice.Api/Program.cs) |
| Domain 與 EF Core persistence | [src/StoryVoice.Domain](src/StoryVoice.Domain)、[src/StoryVoice.Infrastructure](src/StoryVoice.Infrastructure) |
| 背景朗讀 pipeline | [src/StoryVoice.Worker/StoryPipelineWorker.cs](src/StoryVoice.Worker/StoryPipelineWorker.cs) |
| React 路由與主要頁面 | [src/StoryVoice.Web/src/App.tsx](src/StoryVoice.Web/src/App.tsx) |
| 本機完整環境 | [compose.yml](compose.yml)、[.env.example](.env.example) |

## 目前狀態

目前 repository 已具備：

- .NET 10 Clean Architecture：API / Application / Domain / Infrastructure / Worker
- PostgreSQL + EF Core migration
- Redis-ready background processing boundary
- React 19 + TypeScript + Vite + Tailwind CSS 4
- Book / Chapter domain model and REST API
- EPUB / TXT multipart upload、metadata、TOC 與章節解析
- 來源不綁平台的 EPUB／TXT 檔案匯入與手動閱讀筆記（擷取式摘要入口已退場，既有資料暫留供回復）
- 單一神經語音 MVP：持久化工作、租約與重試、取消、私有 MP3 與 owner-scoped Range 串流
- 全書庫處理狀態矩陣：分開標示官方 TTS、合法正文、筆記與 StoryVoice 音訊
- 跨冊系列／固定角色／alias、不可變 cast revision 與整批原子啟用資料邊界
- 章名、獨立旁白、對話與視角角色內心／文件默讀的 deterministic offset segmentation；系列可選擇獨立旁白或「所有非對白皆由 POV 主角朗讀」
- owner-scoped 系列配音管理 API 與伺服器 voice allowlist
- 本機 LLM 角色與 alias 分析、候選勾選／合併、原子套用系列角色表
- 規則優先、本機 LLM 補判的逐句說話者草稿；只有高信心自動確認，其餘進人工審核
- 書冊（獨立於角色配音系列之外的單純書本分類收藏）與冊次排序
- 書庫分類統一使用書冊；舊的瀏覽器「此裝置標籤」已移除
- 書冊唯讀分享：依 email 分享給其他已註冊使用者，可隨時撤銷
- React Router 多頁面前端（書庫／書冊／分享給我的），取代原本的單頁式版面
- Serilog, OpenAPI, liveness/readiness health checks
- Docker Compose development stack
- Unit and integration tests + GitHub Actions CI

AI 與多角色 TTS 仍按 [`DEVELOPMENT_PLAN.md`](DEVELOPMENT_PLAN.md) 分階段落地；
目前已具備角色候選審核、說話者草稿、逐章確認與 staged 多角色產製。逐項完成度與下一個實作入口見
[`docs/PROJECT_STATUS.md`](docs/PROJECT_STATUS.md)。

## Quick start

Prerequisites: Docker 29+ with Compose v2.

```bash
cp .env.example .env
docker compose up --build
```

Open:

- Web UI: <http://localhost:3000>
- API: <http://localhost:8080>
- OpenAPI document (Development mode): `/openapi/v1.json`
- Liveness: <http://localhost:8080/health/live>
- Readiness: <http://localhost:8080/health/ready>

### Private BlueMagpie Taiwan-Mandarin preview and short canary (ARM64 + NVIDIA GPU)

The `bluemagpie` Compose profile adds a self-hosted gateway with no host port on an
internal Docker network. `BLUEMAGPIE_ENABLED=true` enables the fixed-sentence
preview. Formal series narration is a separate opt-in:
`BLUEMAGPIE_FORMAL_NARRATION_ENABLED=true` exposes exactly two built-in voices
(`female_voice` and `hung_yi_lee`) in the series voice catalog and admits staged
multi-character jobs through the local Worker.

The formal path remains an explicit, private/internal opt-in. The Worker persists
validated, deterministic WAV chunks in a separate private volume so a retry or
controlled restart only synthesizes missing chunks. The cache is regenerable, capped
at 32 GiB by default, retained for seven days, and deliberately excluded from published
audio backups. A restart/resume canary and a bounded 36-chunk cold long-form benchmark
have passed without activating their staged audio. Admission now rejects oversized jobs
before creating rows, progress writes are percentage-throttled, and owners can discard a
staged rebuild. Keep complete-book use disabled until exhausted-attempt recovery,
structured long-run metrics, and GPU/LLM coexistence are verified. The model weights are
marked with license `other`, so do not assume redistribution or commercial-use rights.

Preload the pinned model cache, create a random secret of at least 32 characters,
then set these values outside Git:

```bash
BLUEMAGPIE_ENABLED=true
BLUEMAGPIE_FORMAL_NARRATION_ENABLED=false
BLUEMAGPIE_INTERNAL_TOKEN=<random-internal-secret>
BLUEMAGPIE_CACHE_PATH=/absolute/path/to/the/preloaded/cache
VOAI_API_KEY=
VOAI_PAID_API_KEY=
```

Start the private preview with:

```bash
docker compose --profile bluemagpie up -d --build bluemagpie-gateway api worker web
```

For a short BlueMagpie canary, temporarily set the formal flag to `true`, build only
private staged audio, then return it to `false`. Paid VoAI synthesis is deliberately
separate: the Worker ignores the legacy `VOAI_API_KEY` variable and can only enable
paid calls from `VOAI_PAID_API_KEY`. Both keys must remain empty for a no-paid-API
deployment and for the BlueMagpie canary described here.

### Authorized local Clone private preview

The `local-clone` Compose profile adds an internal-only gateway to the existing
FaceSpeak/CosyVoice executor. It does not publish a host port, does not register a
narration provider, and cannot switch a series cast or active audiobook. The API reads
an explicitly allowlisted reference WAV and transcript from the read-only
`local-clone-assets` volume, verifies their configured SHA-256 values, and exposes only
an owner-scoped character preview endpoint.

Keep `LOCAL_CLONE_PREVIEW_ENABLED=false` until the FaceSpeak executor is running the
same shared GPU exclusion lock and returns the pinned source/model attestation. Then
populate a random `LOCAL_CLONE_INTERNAL_TOKEN` (at least 32 characters), the exact
reference/transcript hashes for every allowlisted profile, and provision the private
volume outside Git. Start only the preview boundary with:

```bash
docker compose --profile local-clone up -d --build redis local-clone-gateway api web
```

This path uses the self-hosted model and creates no 3wa or VoAI request. It remains a
private evaluation feature: generated previews are returned with `no-store`, while the
formal narration catalog and Worker remain unchanged.

### Cross-project and subscription voice API

StoryVoice also defines a disabled-by-default, bearer-authenticated API for approved
consumers. It is synthetic-only and accepts exactly `voice` and `text`. A short-lived
`private-development` tier uses an `svd1` credential and a single, maximum-30-day
`voice-api-synthetic-development-grant/v1`; it is private, non-public and non-commercial,
does not require a catalog entry or demo, and keeps `VoiceCatalog` disabled. The
`subscription-commercial` tier uses an `svv1` credential and retains the complete
publication, provider-rights, catalog and `voice-api-synthetic-usage-grant/v1` chain.
Token prefixes and grant schemas cannot cross tiers. Both paths re-read their exact
private assets and verify owner, active profile, project, time and hashes before GPU use.

The external API does not enable browser preview and does not change formal narration or
active audiobooks.
Provisioning, request/idempotency rules, stable errors, activation, and credential
rotation are documented in [`docs/EXTERNAL_VOICE_API.md`](docs/EXTERNAL_VOICE_API.md).
Authenticated calls also feed an owner-scoped durable usage ledger and the
`/developer/usage` dashboard without retaining request text, bearer tokens, idempotency
keys, reference audio or transcripts. Rate limiting and idempotency coordination remain
single-process until the documented multi-replica work is completed.
The unified source/publication contract is documented in
[`docs/VOICE_PUBLICATION_GRANT.md`](docs/VOICE_PUBLICATION_GRANT.md).

The names currently discussed for public cards are only synthetic candidates based on
the owner's statement. This checkout contains no real active authorization, provider
terms snapshot, generation manifest, fixed demo, usage grant, consumer token, enabled
catalog entry, or deployment for them.

Compose 只把 Web 與 API 綁在 `127.0.0.1`；對外服務應由同機 reverse proxy 提供 TLS。

要先在本機驗證預定的正式子路徑：

```bash
STORYVOICE_BASE_PATH=/StoryVoice/ docker compose up -d --build
```

接著開啟 <http://localhost:3000/StoryVoice/>。預定正式網址是
<https://aiprod.wrbtycg.tw/StoryVoice/>；host nginx location 範例放在
[`deploy/nginx-storyvoice-location.conf.example`](deploy/nginx-storyvoice-location.conf.example)，
但不會因為本機 Compose 啟動而自動公開。

Stop the stack:

```bash
docker compose down
```

Add `-v` only when you intentionally want to remove local PostgreSQL、Redis
and uploaded-book data.

## Local development

Backend:

```bash
dotnet restore StoryVoice.sln
dotnet build StoryVoice.sln
dotnet test StoryVoice.sln
dotnet run --project src/StoryVoice.Api
```

Frontend:

```bash
cd src/StoryVoice.Web
npm install
npm run dev
```

Vite proxies `/api` and `/health` to `http://localhost:8080`.

## API foundation

```text
POST /api/books
POST /api/books/import
GET  /api/books
GET  /api/books/{id}
```

Import a UTF-8 TXT or DRM-free EPUB book (10 MiB maximum):

```bash
curl -X POST \
  'http://localhost:8080/api/books/import?author=StoryVoice&language=zh-TW' \
  -F 'file=@./story.txt;type=text/plain'

curl -X POST \
  'http://localhost:8080/api/books/import' \
  -F 'file=@./story.epub;type=application/epub+zip'
```

### Book collections (書冊)

Book collections group existing owner-scoped books together — independent from the
narration-focused `StorySeries` above — and can be shared read-only by email:

```text
GET    /api/collections
GET    /api/collections/{id}
POST   /api/collections
PUT    /api/collections/{id}
DELETE /api/collections/{id}
POST   /api/collections/{id}/books
PUT    /api/collections/{id}/books/{bookId}
DELETE /api/collections/{id}/books/{bookId}
POST   /api/collections/{id}/shares
DELETE /api/collections/{id}/shares/{shareId}
GET    /api/collections/shared-with-me
GET    /api/collections/shared-with-me/{id}
GET    /api/collections/shared-with-me/{id}/books/{bookId}
```

Sharing is read-only and scoped to book titles and chapter text only — reading notes,
extractive summaries, metadata corrections and narration jobs stay private to the owner.

The TXT parser recognizes headings such as `第一章 月下相逢` and
`Chapter 1: Moonlight`; files without headings become one chapter. EPUB imports
metadata, TOC labels and spine reading order, strips executable/style markup,
and stores the original upload under a generated server-side path. EPUB archive
expansion is capped at 100 MiB and 5,000 entries.

Open `http://localhost:3000/library` to import EPUB/TXT files, switch between
books, and expand the parsed chapter text in the read-only library view. Group
books into a collection at `/collections`, and check collections other users
shared with you at `/shared`.

Example:

```json
{
  "title": "月下故事",
  "author": "StoryVoice",
  "language": "zh-TW",
  "originalFileName": "story.epub",
  "chapters": [
    {
      "chapterNumber": 1,
      "title": "序章",
      "originalText": "故事從月色裡開始。"
    }
  ]
}
```

## Architecture

```text
Electronic Book
      ↓
Book Parser
      ↓
Story Analyzer ──→ Character Bible
      ↓
AI Director ─────→ Voice Casting
      ↓
TTS Provider
      ↓
Audio Composer
      ↓
Web Player
```

```text
src/
├─ StoryVoice.Api
├─ StoryVoice.Application
├─ StoryVoice.Domain
├─ StoryVoice.Infrastructure
├─ StoryVoice.Worker
└─ StoryVoice.Web

tests/
├─ StoryVoice.UnitTests
└─ StoryVoice.IntegrationTests
```

## Roadmap

1. **Book Import** — DRM-free EPUB / UTF-8 TXT upload、TOC and chapter extraction
2. **Story Analyzer** — narrator, dialogue, speaker, emotion and confidence
3. **Character Bible** — aliases, merge, voice lock and cross-chapter consistency
4. **Voice Casting / TTS** — provider abstraction, preview, cache and segment regeneration
5. **Audio Composer / Player** — FFmpeg, chapter audio, sentence highlight and resume
6. **AI Director** — tone, speed, pause, volume, scene context
7. **Audio Drama** — ambient sound, effects and BGM

## Security and content rights

- StoryVoice **does not provide DRM circumvention**.
- Process only content you own or have the right to transform.
- 建立系列配音時，合法正文會交給該系列目前設定的語音服務；服務可能是私人本機自架或外部供應商。
- API keys belong in environment variables or a secret manager, never Git.
- 使用 VoAI 雲端 API 時，待合成文字會透過網路傳送至 VoAI；啟用前請確認內容授權、隱私需求與供應商條款。
- 對外提供 VoAI 產物時，應依適用法規與平台規範揭露該語音由 AI 生成或合成。
- VoAI 免費試用音訊含背景音樂或浮水印，只供串接測試、不可作為商用成品；商用前須購買適用方案並確認聲線授權。
- BlueMagpie 程式碼與模型權重不是同一授權；模型權重目前標示為 `other`。本專案的 BlueMagpie 路徑僅供私人內網固定句試音或短篇 staged canary，公開、重新散布或商業使用前須先取得明確授權。
- 自行產生聲線不建立第三方簽署欄位，但公開／訂閱前仍須保存實際 generation manifest、reference／transcript／fixed-demo SHA、工具／模型版本、license 與供應商條款快照，並以 authenticated owner action 啟用唯一的合成聲線授權。provider commercial/public/API/derivation rights 只能在受控條款審查後核准，不能靠使用者勾選。
- 合成聲線授權要求沒有可識別真人仿效及第三方角色／品牌主張；私人角色試音不會自動取得公開角色名、品牌或官方合作的權利。
- AI 產出是否構成受保護著作取決於個案的人類創意投入，StoryVoice 不自動判定著作權成立。參考智慧財產局[電子郵件1140516b](https://www.tipo.gov.tw/tw/copyright/692-34249.html)與[電子郵件1140522c](https://www.tipo.gov.tw/tw/copyright/692-34252.html)；商用前仍須確認供應商授權與是否容許轉授權／API 利用。
- Uploaded books, generated audio, analysis results and runtime volumes are ignored by Git.
- Generated audio is not automatically licensed for redistribution.

See [`SECURITY.md`](SECURITY.md) for responsible disclosure.

## Contributing

Issues and pull requests are welcome. Read [`CONTRIBUTING.md`](CONTRIBUTING.md) before starting a larger change.

## License

StoryVoice source code is released under the [MIT License](LICENSE). Third-party models, voices and generated content may have separate licenses and terms.
