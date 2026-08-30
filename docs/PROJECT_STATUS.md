# StoryVoice 開發進度

最後更新：2026-08-30（repository／production 唯讀狀態、前後端、CI、可在本機驗證的相依安全與文件全面稽核）

本文件記錄已由程式碼與測試證實的能力，以及接下來可直接實作的項目。
產品方向與長期資料模型仍以
[`DEVELOPMENT_PLAN.md`](../DEVELOPMENT_PLAN.md) 和
[`plans/2026-08-11-multi-character-series-cast.md`](plans/2026-08-11-multi-character-series-cast.md)
為準。

## 2026-08-30 全面稽核與修正

本輪從盤點基準 commit `f30166e`、production 唯讀 route、完整測試與實際本機 Web／API stack 重新盤點，已修正：

- Playground 重試／取消／切換欄位時的 AbortController 與舊回應競態；產生期間鎖定輸入，Blob URL 也會獨立回收。
- developer 頁面同時接受 `keyId` 與 `projectId` 時的正規化錯誤，避免 credential 下拉選單失配或 usage 被錯誤篩成 0 筆。
- credential 一次性 secret 被下一次建立／換發覆蓋、mutation 成功但 refresh 失敗卻誤報失敗、service disabled 仍可建立，以及排程撤銷時間未顯示。
- managed credential 的 `LastUsedAtUtc` 可被較舊的並行 request 倒寫；create／rotate／revoke 現在以 owner row lock 跨 process 序列化，滿額 overlap rotation 與並行換發不再突破每專案 5 組有效受管金鑰（既有 deployment-configured token 不計入此 managed 上限）。
- usage summary 原本以多個 read-committed query 計算，流量寫入期間可能出現成功數大於總數，摘要與活動也可能跨 snapshot；現改為單一 aggregate statement，並以 repeatable-read snapshot 讀取同一份活動。整合測試會在兩次 SELECT 間刻意插入並行紀錄，也把共享測試資料 assertion 改成 owner-scoped。
- Playground 與 bearer external API 原本各有一份 fixed-window bucket，可讓同一 consumer 合計超過 `RequestsPerMinute`；現在同一 process 共用同一 limiter。設定啟動時也會拒絕同 owner 的重複／歧義 project reference。
- usage 寫入已改成不等待資料庫的 bounded background queue；滿載與落盤失敗都有 log／metric，且每筆以獨立 scope 寫入。external speech POST 也在 bearer 驗證查詢資料庫前加入固定記憶體的來源／全域防濫用 limiter，輪換無效 key 或來源位址不再能無界消耗 DB／記憶體。
- developer 專案詳情、credential、Playground 與 usage 在 route／`?project=` 切換後會立即進入 transition loading，停用舊專案操作、資料與舊回應；未知 usage 專案不會退化成查詢全部 owner 資料，credential 的一次性 secret 在重新載入或錯誤畫面仍會保留到 owner 主動關閉。
- 未知 SPA route 原本只得到 nginx 200 shell 後顯示空白；React 現在顯示可返回首頁／API 文件的 404 頁。主導覽也補上核心 `/series` 入口。
- 書庫初次載入誤顯示 0 本、已移除流程仍寫「上傳並連結」、首頁把 email 分享寫成公開唯讀連結，以及首頁公開使用的舊書商同步截圖／文案均已校正；首頁兩張書庫圖已由本輪本機實際 UI 與合成資料重拍。
- BlueMagpie／local-clone gateway 的 FastAPI、Starlette、multipart 與 pytest 已升到相容版本；測試環境加入 Starlette 官方轉向的 HTTPX2、正式 local-clone client 仍保留既有 HTTPX。CI 新增 EF pending-model、NuGet／npm audit、local-clone 51 tests、兩個 gateway 已安裝 contract／test 環境的 `pip check`／`pip-audit --strict`，並實際 build local-clone production image；BlueMagpie ARM64／CUDA production image 的完整相依仍受下方硬體邊界限制。
- `DEVELOPMENT_PLAN.md` 的 Phase 1、3～9 核取狀態、舊 `talespeak-*` service 名稱、PDF 現況與歷史 bootstrap 任務，以及 UI/UX handoff 對已上線 portal／`/voices` 的矛盾敘述已同步真實狀態。

本節描述 repository 的本輪修正，不代表 production 已部署；正式站仍須以合併後的確切 commit、人工部署與上線 smoke 為準。

## 目前可用

- .NET 10 Clean Architecture、PostgreSQL／EF Core、React／TypeScript、Worker 與 Docker Compose 基礎架構。
- 帳號、Cookie session、CSRF、owner-scoped 書庫與私有資料邊界。
- 不綁特定書商的無 DRM EPUB／UTF-8 TXT 匯入、章節解析與原始檔安全儲存。
- 閱讀筆記與書目人工校正；擷取式摘要已從 UI、API 與狀態矩陣退場，既有資料表與資料暫留供回復。
- 本機 LLM 角色／alias 分析：`gpt-oss:20b` 逐章讀取完整合法正文，只保存名稱、alias、信心與證據次數，不保存分析用正文。前端可勾選候選、編輯 canonical 名稱、合併 alias、指定角色層級與聲線，再以單一 owner-scoped API 原子加入冊次及建立／重用系列角色；重送不會重複建立。
- 單一聲線朗讀工作：持久化、租約、重試、取消、進度、私有 MP3 與 Range 串流。
- 系列／冊次／角色／alias domain model 與 PostgreSQL 約束；canonical name 與 alias 共用唯一命名空間。
- 不可變 cast revision、staged rebuild batch 與全系列 active epoch 原子切換邊界。
- 以原文 offset 切分章名、旁白、對話與視角角色內心／文件默讀。系列可選 `IndependentNarrator` 或 `PointOfViewInnerMonologue`：後者會把章名及所有非真正對白的片段具體存成 POV 角色的 `InnerMonologue`，使用該角色中性／基礎聲線；電話、發話、朗讀給他人聽與咒語仍是對話。所有片段可無遺漏、無重排地重組原文。
- owner-scoped 系列配音 API：系列建立與查詢、冊次加入、角色與 alias 管理、固定聲線更新、系列敘述模式／POV 原子設定、伺服器 voice allowlist。
- 書冊（`BookCollection`）：與角色配音系列(`StorySeries`)各自獨立的單純書本分類收藏，可調整成員書籍排序與冊次標籤；書庫的瀏覽器「此裝置標籤」已移除，分類統一使用書冊。
- 書冊唯讀分享：owner 可依 email 把書冊分享給其他已註冊帳號，被分享者只能唯讀瀏覽書名與章節正文，看不到閱讀筆記、摘要或朗讀音訊；owner 可隨時撤銷。
- 前端已改為 React Router 多頁面架構（`/library`、`/collections`、`/shared` 等），不再是單一長頁面；`NarrationPanel` 已統一為深色主題。
- 登入後開發者入口已具備 owner-scoped 總覽、專案詳情與受管 API 金鑰頁：可查看 access
  tier、效期、rate／size limits、credential 識別摘要、last-used 及聲線授權／撤銷狀態；可為
  既有專案建立、換發（0／60／1,440 分鐘 overlap）或撤銷金鑰。完整 secret 只在建立／換發
  回應顯示一次，資料庫只保存 SHA-256；所有異動有 durable audit，跨 owner 一律回 404。
  既有部署設定金鑰維持相容但仍由維運管理。durable usage ledger 會以 best-effort 保存 external API
  與可可靠歸屬的 Playground request ID、project、credential 識別、聲線、結果、latency 與 WAV 產出秒數／bytes，並提供
  owner-scoped `/developer/usage` 篩選與活動頁；不保存輸入文字、token、冪等鍵、reference 或
  transcript。`/developer/playground` 已透過 owner-session、CSRF 與 same-origin backend-for-frontend
  安全代理既有合成服務，可選專案／聲線、檢查字元與 UTF-8 bytes、產生／取消／播放／下載 WAV，
  顯示 request ID、冪等鍵、latency、輸出大小與穩定錯誤，並寫入同一份安全 usage ledger；瀏覽器
  不取得 external bearer。ledger 透過 bounded background queue 非阻塞寫入；queue 滿載或落盤失敗會留下 log／metric、但不等待或取代合成回應，因此不是 billing／hard quota 的唯一依據。開發者總覽會以同一份 owner-scoped ledger 顯示最近 24 小時要求數、
  成功、失敗、429 與平均耗時；摘要讀取失敗時不會連帶中斷專案卡。rate limit、single-flight 與
  idempotency 仍是單一 process memory。
- 受限說話者辨識：明確 reporting clause 等強規則維持最高優先，不會被模型覆蓋；其餘對話再整章交給本機 `gpt-oss:20b` 補判。模型 schema 只能輸出目前系列已知角色 ID，≥85 信心才自動確認，中／低信心留在人工審核；逾時、例外、漏答、未知 ID 或卸載失敗都安全退回規則結果／Unknown。主角視角模式只轉換非對白，不改變真正對白的 attribution 上下文。
- 逐章劇本審核 API：草稿建立／重建、逐片段確認或拒絕、確認為不可變 `ConfirmedSpeechPlanRevision`（含 canonical fingerprint），私人正文不進回應。
- 多聲線 provider registry／dispatcher：Edge TTS 以 JSON manifest 透過 stdin 傳入每個 turn 的文字／聲線／停頓，ffmpeg concat + ffprobe 驗證後才原子發布；新增供應商不用改動既有系列角色 ID。
- BlueMagpie BM1 本機正式 provider 已接上同一套不可變 speech plan、cast revision、staged rebuild 與原子 MP3 發布流程。固定句試音由 `BLUEMAGPIE_ENABLED` 控制；正式系列 catalog 與工作 admission 另由預設關閉的 `BLUEMAGPIE_FORMAL_NARRATION_ENABLED` 控制。目前只提供 `female_voice`、`hung_yi_lee` 兩個內建聲線，且固定使用中性 rate／pitch／volume，不套用 Edge 的情緒參數差值。
- Worker 已能實際 claim 並處理 `MultiCharacter` 朗讀工作：從鎖定的 speech plan 與 cast revision 組出 turn 序列（相鄰同聲線合併、章界／換人有界停頓），送出合成前重算 fingerprint 與逐片段文字雜湊，任一不符永久失敗為 `speech_plan_integrity_mismatch`。
- 建立／推進全系列 `SeriesCastRebuildBatch` 的 Application 服務已完成並串接前端：owner 可在 `/series` 頁面對已確認劇本的系列建立 staged 多角色朗讀批次，逐冊完成後原子切換 active cast epoch；重試會自動清除同系列失敗的舊批次與孤兒 draft cast revision，不會撞唯一鍵。owner 也能原子丟棄未啟用批次：排隊工作直接取消、執行中工作提出取消要求，已發布音訊與 active pointer 不受影響。
- BlueMagpie 正式工作在建立任何 cast／batch／job 前會逐冊執行保守的 chunk 與 PCM/WAV budget preflight；Worker 再以實際 chunks 與快取／gateway 音訊 bytes 重驗。合成進度只在整數百分比增加時寫入資料庫，暫時性失敗與 timeout 會等待 GPU lease 安全冷卻後才重試。
- Edge 對白依情緒（緊張／開心／生氣／難過）微調 rate/pitch/volume，規則式判斷只讀取合成當下已合法取得的正文與 reporting clause，不做情感分析宣稱；BlueMagpie 維持固定中性參數。
- 角色庫（Character Library，見下方獨立章節）：owner-scoped、跨系列共用的角色管理頁面（`/characters`），角色的基本資料（頭像、年齡、性別、生日、個性、口頭禪、人物背景、說話風格）與自訂聲線（Character Voice Studio）都掛在角色庫上，任何系列的多角色配音都能直接選用同一個角色，不用每個系列各自重建。

## 多角色系列配音進度

| 工作 | 狀態 | 已驗證範圍 |
|---|---|---|
| Task 0：單聲線 compatibility migration | 已完成 | 舊資料回填、mode 約束、Worker claim 邊界 |
| Task 1：系列與固定角色 domain model | 已完成 | owner、角色、alias、冊次與聲線不變條件 |
| Task 2：系列與 cast EF 持久化 | 已完成 | migration、複合 FK、唯一索引與 rollback guard |
| Task 3：不可變 cast revision 與 rebuild batch | 已完成 | fingerprint、staged visibility、原子 epoch activation |
| Task 4：owner-scoped 系列配音 API | 已完成 | auth、CSRF、owner isolation、voice allowlist、不回正文 |
| Task 5：deterministic speech segmentation | 已完成 | offset、source hash、巢狀／未閉合引號、對話／內心默讀語意分類與完整重組 |
| Task 6：受限說話者辨識 | 已完成 | 規則優先、本機 LLM 補判、known identity schema、高信心自動確認、unknown／review fallback |
| Task 7：speech plan 保存與審核 | 已完成 | draft、confirmed revision、stale 與 immutable job binding |
| Task 8：多聲線 TTS provider | 已完成；BlueMagpie 限私人 staged 驗證 | Edge provider dispatcher、分段合成、ffmpeg／ffprobe 與原子發布；BlueMagpie 版本鎖定、兩聲線、120 scalar 分塊、durable cache/resume、雙層 job budget 與正式 admission gate |
| Task 9：staged multi-character jobs | 已完成 | active cast 載入、confirmed plan 載入、turn 合併與停頓、完整性重驗證、staged batch 建立與原子 epoch 切換 API |
| Task 10：系列 cast 與 speech-plan UI | 已完成 | LLM 候選勾選／alias 合併／套用、系列管理、低信心劇本審核、staged rebuild 狀態與啟用、角色自訂聲線工作室 |
| Task 11：私有書庫 backfill | 營運工作，未開始 | 必須在 Git 外執行，不可留下私人內容或識別資訊 |
| Task 12：兩階段正式發布 | 短篇與受控長文 staged 驗證已通過 | 備份、candidate、Worker restart/resume、36-chunk cold benchmark、drift check、監控與 rollback proof；未啟用測試音訊，完整書籍仍維持關閉 |

## 角色庫（Character Library）與角色自訂聲線工作室（Character Voice Studio）

`/characters` 是獨立於任何系列的 owner-scoped 角色管理頁面：可以建立角色的基本
資料（頭像、年齡、性別、生日、個性、口頭禪、人物背景、說話風格——AI 補完／AI
全部重寫按鈕先保留位置，尚未接 LLM），也可以直接在同一頁替角色建立一組基礎
聲線，以及緊張／開心／生氣／難過（加上「平常」）最多五組情境聲線。3wa 官方
manifest 已明載目前不會把 `voice_prompt` 傳給 VoxCPM2，因此「文字設計」已在 API、
試音、系列 admission 與 Worker 全部 fail closed；既有描述資料保留但不可視為可用聲線。
可用流程只剩「上傳錄音克隆」（需要選擇同意類型：本人親自錄製／已取得明確同意／
已取得合法授權，上傳後走語音辨識草稿→人工確認文字稿的流程才會就緒）。角色建好
之後，在「多角色系列配音」加入角色時可以直接從角色庫選入，同一個角色（與其聲線）
能跨多個系列重複使用，不用每個
系列各自重建一次；系列裡的角色也可以不連結角色庫、維持原本手動設定固定 Edge
聲線的舊流程。合成時依對白情緒查找對應情境聲線，找不到就退回基礎聲線；一般
對白仍可使用旁白 fallback，但 `InnerMonologue` 缺少 POV 角色聲線時會 fail closed，
不會悄悄改用另一位旁白。

資料模型上，`CharacterVoiceProfile` 掛在 owner-scoped 的 `CharacterProfile`（角色庫
條目）底下，不再綁死在單一系列的 `SeriesCharacter`；`SeriesCharacter` 有一個可選的
`CharacterProfileId` 連結欄位，FK 設 `RESTRICT`——系列還在使用中的角色庫角色不能
直接刪除，要先從系列移除。

角色管理頁面另外還有：
- **啟用／停用狀態**：`CharacterProfile.IsActive`，停用只是介面上標記淡出，不影響
  已經連結的系列配音（停用不會把角色從系列裡移除，也不會讓現有朗讀失敗）。
- **角色 ID 顯示與複製**、建立時間／最後更新時間。
- **摘要卡片**：基礎聲線狀態、情境聲線數量（已就緒／5）、樣本語料時數（所有克隆
  聲線參考音檔的 ffprobe 時長加總，API 容器已補上 ffmpeg）、最近進行中的任務。
- **試講**：針對任一已就緒（`Ready`）的 Clone 聲線，輸入一小段文字（上限 200 字）即時合成
  播放；重用既有的 3wa 合成 client 同步跑一次 submit/poll/result/artifact，不進
  Worker 的 job 佇列、不落地存檔，純粹是 UI 預覽用途。
- **任務紀錄**：以這個角色所有 `CharacterVoiceProfile`（含歷史 Pending／Failed 紀錄）
  依最後更新時間列出的簡易表格；沒有另外做一套持久化的非同步任務追蹤系統，也
  沒有 3wa 端回傳的即時進度百分比（3wa 文件雖提到 status 回應可能帶 `progress`
  欄位，但目前程式碼沒有解析、儲存它）。
- **重設按鈕**：把基本資料表單復原成上次儲存的值，捨棄尚未儲存的編輯。

實作串接的是 3wa Cluster API（`api.php?mode=voice_generate`）的 VoxCPM2
引擎，分成兩條獨立的非同步流程：`profile_prepare/status/confirm`（Application 層，
建立與確認聲線）、`synthesize` 的 submit/poll/result/artifact（Worker 層，實際產生
朗讀音訊）。2026-08-17 已使用 production token 對官方 canonical endpoint 完成
Design 模式的 submit/poll/result/artifact 短句實測：任務雖由 `running` 進入 `success`，
但 11 組不同角色描述在相同文字與固定 seed 下回傳完全相同的 WAV；官方 manifest 亦
明載 `voice_prompt` 目前不會傳入 VoxCPM2。這條路徑因此已固定停用，不能用 HTTP 200
或 `Ready` 狀態冒充角色聲線已訓練完成。回傳的 `task_id` 與 artifact ID 實際為 JSON
number，產物為可解析的 `audio/x-wav`（PCM 16-bit、48 kHz、mono）。Client 現已同時
支援 number/string ID，只會將 Bearer token 送往設定的同源 HTTPS API 目錄，並限制
JSON/音訊回應大小。
新 canonical Clone 的 profile prepare/confirm 尚需以另一份授權短 WAV 完成實測；
現存 legacy Clone task 的 ASR 轉錄失敗，不能視為可用聲線。

已知限制（刻意排除的範圍，不是漏做）：
- **旁白聲線無法克隆**：角色庫只服務 `SeriesCharacter`，系列旁白沒有對應的克隆
  流程。系列旁白 provider 設成 `3wa-voxcpm2` 時，角色可以混用 Edge 固定聲線與具備
  合法授權、已完成的 3wa Clone 聲線；旁白本身永遠只能是 Edge 聲線。任何缺少 Ready
  Clone Base 或仍含 Design profile 的 3wa 角色會在設定、staging 與 Worker 三層被拒絕。
- 角色基本資料的「AI 補完」／「AI 全部重寫」按鈕只是預留位置，沒有接 LLM（3wa
  Cluster API 目前沒有對應的 chat/生成 mode）。
- 已新增 BlueMagpie BM1 作為第二個固定聲線引擎；只有內建女聲 `female_voice` 與男聲
  `hung_yi_lee`。durable deterministic chunk cache/resume 已完成，受控 Worker restart
  canary 只重算缺少 chunks；另一次 36-chunk cold benchmark 在約 6.15 分鐘內產生
  690.58 秒 staged 音訊（RTF 0.534），沒有重啟、沒有啟用測試音訊，且測試後 formal
  flag 已關閉。完整書籍啟用前仍須補 exhausted-attempt 後的同工作恢復、結構化長跑
  metrics 與 GPU/LLM 共存壓力驗證。模型權重 license 標示為 `other`，不代表可公開、
  重新散布或商業使用；`BLUEMAGPIE_FORMAL_NARRATION_ENABLED` 預設並應持續為 `false`。
- BlueMagpie 自架 canary 不需要 VoAI；`VOAI_API_KEY` 與 `VOAI_PAID_API_KEY` 都必須保持
  空值。Worker 只認獨立 opt-in 的 `VOAI_PAID_API_KEY`，避免舊 key 意外產生付費呼叫。
- 沒有匿名／公開的角色或聲線建立入口，一律維持既有的 owner-scoped 私有資料邊界。

## 明確未完成／需要產品或營運決策

下列是已知且有意保留的工作，不應被解讀成目前功能已上線：

Repository 的 PR／main CI 與 production 人工部署是兩組獨立證據；本表不把本機或 GitHub
測試結果當成已上線，也不保存容易在下一次交付後失真的「尚未 commit」暫態描述。

| 區域 | 尚未完成 | 完成前的邊界 |
|---|---|---|
| Production credential | owner-session 的正式 create／rotate／revoke smoke | 涉及一次性正式 secret 與資料 mutation，須由 owner 明確授權操作 |
| Public voice catalog | 核准 entry、固定 demo、detail DTO／route、creator publication／revoke workflow | `/voices` shell 已部署但 feature flag 關閉，public API 維持 404 |
| 商業化／營運 | 申請、subscription、billing、invoice、billing-grade metering、hard quota、admin、usage retention／archive | 現有 best-effort ledger 不作唯一計費／硬額度來源；不顯示假價格、假訂閱或假用量 |
| 多 replica | 共用 rate limit、idempotency、single-flight 與公平排程 | Playground 與 external API 已在同一 process 共用額度；跨 replica 尚未完成 |
| 前端 runtime regression automation | ConfirmDialog focus trap、Playground stale response／Blob URL lifecycle 的完整 DOM／browser 自動化 | 本輪有 source-level regression tests、獨立 review 與實際 Chrome 核心流程驗證，但 CI 尚未模擬這些焦點與競態時序 |
| 私有書庫 | Git 外 backfill | 不把私人正文、識別資訊或 dump 放進 repository |
| BlueMagpie 正式長篇 | exhausted-attempt recovery、結構化長跑 metrics、GPU／LLM 共存、完整書籍 gate、權重 license 決策，以及 NGC constraints／CUDA／model production image 的完整 dependency 與 vulnerability audit | formal flag 預設保持 `false`；目前 `pip-audit` 證據只涵蓋已安裝的 contract／HTTP test 環境，本機 x86_64 不能冒充 ARM64／NVIDIA production image 驗證 |
| 角色建立 | 角色基本資料的 AI 補完／全部重寫 | 目前 UI 明示尚未提供；3wa 沒有對應文字生成 mode |
| 長期有聲書 UX | automatic casting、單片段重生、平行生成、cost logging、loudness normalize、章節／句子／角色同步、播放進度與 resume | 已完成的 private audio player 與 staged narration 不等於這些後續能力 |
| AI Director／Audio Drama | whisper、完整 scene context、環境音、音效、BGM 與混音 | 目前只有 Edge 的受限規則式情緒 rate／pitch／volume 差值 |

## 公開 repository 邊界

提交前必須確認差異中沒有 API key、token、Cookie、真實書籍正文、使用者識別碼、
私人角色對照、聲音樣本、生成音訊、資料庫 dump、production runtime 檔案或私有部署資訊。
測試資料必須為合成內容；更多規則見 [`SECURITY.md`](../SECURITY.md)。

## 驗證

```bash
dotnet build StoryVoice.sln --configuration Release
dotnet test StoryVoice.sln --configuration Release --no-build
dotnet ef migrations has-pending-model-changes \
  --project src/StoryVoice.Infrastructure --startup-project src/StoryVoice.Api \
  --configuration Release --no-build
dotnet list StoryVoice.sln package \
  --vulnerable --include-transitive --no-restore
python -m unittest discover -s tests/python -v

cd services/bluemagpie-gateway
python -m venv .venv
.venv/bin/python -m pip install pip==26.2.1
.venv/bin/python -m pip install -r requirements-test.txt pip-audit==2.10.1
.venv/bin/python -m pip check
.venv/bin/python -m pytest
.venv/bin/python -m compileall -q bluemagpie_gateway tests
.venv/bin/python -m pip_audit --local --strict
cd ../..

cd services/local-clone-gateway
python -m venv .venv
.venv/bin/python -m pip install pip==26.2.1
.venv/bin/python -m pip install -r requirements-test.txt pip-audit==2.10.1
.venv/bin/python -m pip check
.venv/bin/python -m pytest
.venv/bin/python -m compileall -q local_clone_gateway tests
.venv/bin/python -m pip_audit --local --strict
docker build --tag storyvoice-local-clone-gateway:verify .
cd ../..

cd src/StoryVoice.Web
npm ci
npm test
npm run lint
npm run build
npm audit --audit-level=high
cd ../..

docker compose config --quiet
git diff --check
```

PostgreSQL constraint／migration 測試使用 Testcontainers，因此本機必須先啟動 Docker。
