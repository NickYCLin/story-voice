# StoryVoice 聲線平台 UI/UX 設計交接

- 最後盤點：2026-08-24
- 對象：UI/UX、前端、產品、後端
- 用途：說明目前實作進度、production 真實狀態、缺少畫面與下一階段設計範圍

> 本文件只描述已由原始碼、測試或 production 唯讀檢查確認的狀態。設計稿可以探索未來體驗，但不得把尚未啟用的公開聲線、訂閱、價格或付款畫成已可使用。

## 一句話結論

StoryVoice 原本的書庫、角色、系列卡司與朗讀工作台已經有完整操作介面；新的「聲線平台」目前完成三個部分：

1. **林若晴的私人跨專案 API 已在 production 啟用**。
2. **登入後的唯讀開發者總覽已上線；專案詳情已在 repository 完成、待部署確認**。兩頁都不提供完整 secret 或 credential mutation。
3. **公開聲線館的前端與後端骨架已在 repository 完成**，但 production Web 尚未部署這個新頁面，公開 catalog 也仍關閉且沒有任何公開卡片或固定示範音檔。

訂閱、方案價格、付款、帳單、公開發佈流程、owner 授權操作與管理後台仍未實作。UI/UX 的首要任務不是重做既有書庫，而是把已能使用的私人 API 做成可理解、可管理、可安全操作的開發者控制台。

## 狀態圖例

| 標記 | 意義 |
|---|---|
| ✅ LIVE | production 已部署且實際可用 |
| 🟠 BACKEND LIVE | production 後端可用，但沒有對應 UI |
| 🟡 IMPLEMENTED / OFF | repository 已有程式與測試，但 production 未部署、未啟用或沒有資料 |
| 🧱 VALIDATION SCAFFOLD | 有安全驗證骨架，但還不是完整產品流程 |
| ❌ MISSING | 畫面及其必要 backend 尚未實作 |

## 目前做到哪裡

### Production 與 source 的真實差異

| 項目 | Repository 現況 | Production 現況 | 判定 |
|---|---|---|---|
| StoryVoice 首頁、登入、書庫、書冊、分享、角色、系列卡司 | 已實作 | 已上線 | ✅ LIVE |
| 林若晴私人跨專案語音 API | 已實作 `POST /api/external/v1/speech` | 已啟用，限既定 private-development consumer | 🟠 BACKEND LIVE |
| 開發者/API 管理面板 | 總覽、專案詳情、受管金鑰生命週期與 durable audit 已完成 | migration、API 與 Web 已部署；既有 private consumer 已恢復載入 | ✅ LIVE |
| `/voices` 公開聲線館 React 頁面 | 已實作並有測試 | 新 Web bundle 已部署；catalog 關閉時安全顯示未啟用狀態 | 🟡 IMPLEMENTED / OFF |
| 公開聲線 list/demo API | 已實作、依 feature flag map | `VoiceCatalog=false`，live API 回 404，0 entries | 🟡 IMPLEMENTED / OFF |
| 周子謙／林若晴公開卡片 | UI 可接 DTO | 沒有公開 entry、沒有公開固定示範 | ❌ MISSING DATA / ACTIVATION |
| subscription-commercial 驗證鏈 | 已有授權、期限、地區、consumer 與資產驗證 | 沒有 active commercial consumer 或 catalog entry | 🧱 VALIDATION SCAFFOLD |
| 方案、訂閱、付款與帳單 | 無 | 無 | ❌ MISSING |
| owner 聲線發佈／撤銷後台 | 無自助管理 API 或 UI | 目前由受保護設定與維運流程 provision | ❌ MISSING |
| Admin 審核與營運後台 | 無 | 無 | ❌ MISSING |

Production 於 2026-08-19 的唯讀確認：

- 林若晴 alias `lin-ruo-qing` 已供一個私人開發 consumer 使用。
- 此權限為短期 private-development，效期至 2026-09-18 16:54（Asia/Taipei），不是公開、訂閱或商用方案。
- 目前限制為每分鐘 1 次、文字最多 200 字元／2,048 UTF-8 bytes、WAV 回應最多 3 MiB。
- `LocalClonePreview=false`、`VoiceCatalog=false`、`ExternalVoiceApi=true`。
- 公開 catalog API 回 404；external speech 的 GET 回 405，表示 POST-only route 已註冊。
- 私人 API 曾得到 server HTTP 200 與上游 200；目前沒有 UI 可查看這次活動、用量或剩餘效期。

### 既有產品畫面

| Route／區域 | 已有功能 | UI/UX 備註 |
|---|---|---|
| `/` | Landing、產品能力、圖片 lightbox | 可保留為 StoryVoice 內容創作入口；聲線平台可增加獨立 CTA |
| 登入／註冊 | Cookie session、登入與註冊流程 | 已可作為 creator/developer portal 的登入入口 |
| `/library`、`/library/:bookId` | EPUB/TXT、搜尋、書目、角色分析、筆記、朗讀工作與私有音訊 | 成熟內容工作區，不是本輪重做重點 |
| `/collections`、`/collections/:id` | 書冊建立、排序、描述與分享 | 已完成 |
| `/shared`、`/shared/:id` | 收到的唯讀書冊與章節 | 已完成 |
| `/characters` | 角色資料、頭像、聲線、試講、任務紀錄 | 需重整聲線區；目前仍混有舊真人錄音 Clone 欄位 |
| `/series` | 系列、角色連結、卡司、POV、固定句試音、speech plan、重建與啟用 | 已完成主要流程 |
| `/voices` | 搜尋、篩選、卡片、固定示範播放器、狀態、訂閱說明 | source 已完成；production 未真正部署／啟用 |

### 現有 `/voices` 已有的設計基礎

現有 [`PublicVoicesPage.tsx`](../src/StoryVoice.Web/src/pages/PublicVoicesPage.tsx) 已包含：

- 匿名 catalog fetch。
- 搜尋、聲音特質、應用場景與狀態篩選。
- 聲線卡、固定示範播放、播放中及失敗狀態。
- AI 合成揭露。
- loading、disabled、error、empty、no-result 狀態。
- 「查看訂閱與申請說明」CTA；原始碼已明示目前沒有即時結帳或價格。
- 暖紙色、琥珀、stone 與 serif 字體的 editorial 視覺方向。

目前 public DTO 只有：`alias`、`displayName`、`subtitle`、`disclosure`、`styles`、`useCases`、`sampleUrl`、`canPreview`、`ctaKind`、`subscriptionAvailable`、`status`。設計若需要下列內容，必須先擴充 backend contract：

- 角色主視覺／avatar。
- 語言、口音、音域或音色 metadata。
- 示範長度、波形資料。
- 聲線詳情與長文介紹。
- 方案、價格、額度與 entitlement。
- server-side 搜尋、排序與 pagination。

## 目前最重要的產品問題

### 1. API 能用，但使用者看不到也管不到

目前 consumer 只能由維運人員取得 endpoint 與 credential。產品內沒有地方回答：

- 我有哪些專案？
- 哪些聲線可以用？
- 這把 key 何時到期？
- 每分鐘能呼叫幾次？
- 最近成功或失敗幾次？
- 要怎麼輪替或撤銷 key？
- 如何送第一個 request？

這是本輪 UI/UX 的 P0。

### 2. 現有「聲線管理」仍是假設真人錄音 Clone

[`CharacterVoiceProfilesPanel.tsx`](../src/StoryVoice.Web/src/CharacterVoiceProfilesPanel.tsx) 目前要求 WAV、逐字稿、consent receipt、簽署日與真人授權 attestation。產品最新規則是：角色與聲音預設皆為使用者自行建立的合成素材，不會有真人來源。

因此不要在新合成聲線流程顯示：

- 真人姓名、聲紋本人或身份文件。
- 真人錄音同意、簽署日期、consent type。
- 「合法授權聲線」等容易暗示真人來源的預設文案。

新流程應改為「原創合成聲線」，只保留必要的角色綁定、來源工具／模型、reference、transcript、manifest、terms snapshot、固定 demo、使用範圍、有效期與撤銷狀態。若未來要加入真人來源，應新增一條明確分離的流程，不把真人欄位重新塞回預設表單。

### 3. 角色、聲線、專案與金鑰目前混在不同概念中

設計必須固定四層：

| 物件 | 回答的問題 | 不應混入 |
|---|---|---|
| 角色 Character | 這是誰、長什麼樣、個性與故事是什麼？ | API key、方案 |
| 聲線 Voice | 聲音特質、版本、示範、來源與可用狀態是什麼？ | 專案 credential |
| 專案 Project | 哪個外部產品可以用哪些聲線、用途與期限？ | 聲線素材編輯 |
| 金鑰 Credential | 機器如何驗證、何時建立／輪替／撤銷？ | 角色內容 |

## 建議資訊架構

```text
StoryVoice
├─ 公開區
│  ├─ 聲線館 /voices
│  ├─ 聲線詳情 /voices/:alias
│  ├─ 方案與申請 /plans
│  └─ API 文件 /developers/docs
├─ 開發者控制台
│  ├─ 總覽 /developer
│  ├─ 專案 /developer/projects
│  ├─ API 金鑰 /developer/credentials
│  ├─ Playground /developer/playground
│  └─ 用量與活動 /developer/usage
├─ 建立者後台
│  ├─ 我的聲線 /creator/voices
│  ├─ 新增／編輯聲線 /creator/voices/:id
│  ├─ 公開發佈 /creator/voices/:id/publication
│  ├─ 固定示範 /creator/voices/:id/demo
│  └─ 授權與撤銷 /creator/voices/:id/access
└─ 營運後台
   ├─ 申請與審核
   ├─ Consumer／Project／Key
   ├─ 方案 entitlement
   ├─ 稽核與緊急撤銷
   └─ 服務狀態
```

公開區、登入後開發者區與建立者區要使用不同導覽層級。不要把所有功能繼續塞進 `/characters` 的單一長頁面。

## 缺少畫面與優先順序

### P0：把目前已上線的私人 API 做成可用產品

#### A. 開發者總覽 `/developer` — ✅ 唯讀版 LIVE（2026-08-21）

> 已交付唯讀第一版：`GET /api/developer/external-voice/overview`（owner-scoped、UserSession cookie 授權）
> 由 `DeveloperVoiceConsoleService` 直接投影既有 `ExternalVoiceApi` 設定，前端頁面
> `src/StoryVoice.Web/src/pages/DeveloperConsolePage.tsx` 掛在登入殼層 `/developer`。
> 涵蓋：服務啟用狀態、專案卡（名稱／ID、access tier、token prefix+keyId、效期、
> not-yet-effective／active／expiring-soon／expired 狀態）、聲線授權（active／revoked）、
> 共用限制與空狀態。刻意不含：TokenSha256、evidence 路徑／雜湊、owner GUID、
> Playground、用量資料。受管金鑰另由 `/developer/credentials` 提供。
> `ExternalVoiceAuthenticationHandler` 保留既有設定檔 token 相容路徑，並加入只存 SHA-256 的
> database credential 驗證；成功後仍映射回原 consumer/grant，不放寬聲線授權。

**使用者**：已取得私人開發權限的專案 owner／developer。

**目的**：登入後 30 秒內知道 endpoint、環境、聲線、效期與下一步。

最小內容：

- API 狀態與 environment。
- Project 名稱／ID、access tier。
- 可用聲線數與聲線卡。
- 有效起訖、剩餘天數、rate limit。
- 快速開始三步驟。
- 最近 24 小時成功／失敗摘要；若 backend 尚未提供，顯示「尚未提供用量資料」，不可造假數字。
- CTA：`前往 Playground`、`查看 API 文件`、`管理金鑰`。

必要狀態：loading、無專案、等待核准、active、即將到期、expired、revoked、service degraded。

**目前缺口**：owner-scoped projects/entitlements summary API 與受管 credential 生命週期已完成；
最近 24 小時活動、durable usage 與 Playground 仍未完成。

#### B. 專案列表與詳情 `/developer/projects/:id` — ✅ LIVE（2026-08-26）

> Repository 已交付唯讀專案詳情頁，從 `/developer` 的 owner-scoped 專案卡進入，沿用
> `GET /api/developer/external-voice/overview`，依目前登入帳號可見的 `projectId`／`keyId`
> 尋找專案。呈現 access tier、consumer identity、有效期間、剩餘天數、rate／size limits、
> credential prefix+keyId、聲線 active／revoked 狀態與安全快速開始；完整 secret、token hash、
> evidence、owner GUID 不進 UI。source、測試與 production Web bundle 已完成驗證。

最小內容：

- project 與 consumer identity。
- access tier：private-development 或未來 subscription-commercial。
- 允許的聲線、用途、有效期、撤銷狀態。
- rate／size limits。
- credential 摘要與 last used；不得回傳完整 secret。
- 到期續用或申請擴權 CTA。

**目前缺口**：受管 credential 已有 durable last-used；既有設定檔 credential 與用途 metadata
仍沒有 durable query，因此 UI 會誠實顯示「尚無紀錄」或由部署設定提供。

#### C. API 金鑰 `/developer/credentials` — ✅ LIVE（2026-08-26）

> Repository 已交付 owner-scoped list/create/rotate/revoke API、PostgreSQL migration、durable audit
> 與登入殼層 UI。raw token 只在 create／rotate 回應顯示一次，不進 URL、localStorage 或 log；
> database 只保存小寫 SHA-256。換發支援 0、60、1,440 分鐘 overlap，舊金鑰到期後立即失效；
> 其他 owner 對同一 credential 的操作固定回 404。production 已套用 migration 並部署新版
> API／Web；公開 route、匿名 401、consumer overlay 與容器健康狀態均已驗證。基於 secret
> 邊界，本輪未代替 owner 建立或撤銷正式金鑰。

最小內容與互動：

- 建立、命名、建立時間、last-used、狀態。
- 建立後一次性 secret modal：`複製`、`下載 .env`、`我已保存`。
- 關閉 modal 後永遠只顯示 key prefix／末四碼。
- 輪替需顯示 overlap window；撤銷需二次確認與影響範圍。
- 不得把 raw token 放在 URL、localStorage、analytics 或前端 log。

必要狀態：create success、copy success、download、lost secret、rotate pending、revoked、forbidden。

**目前缺口**：尚未用 owner 瀏覽器 session 執行建立／換發／撤銷的正式資料 smoke test；
其餘 source、CI、migration、API／Web 與匿名權限邊界皆已驗證。

#### D. API Playground `/developer/playground`

最小內容：

- 選擇專案與已授權聲線。
- 文字輸入、字元／byte 計數與 200 字限制。
- 產生、取消、播放、下載 WAV。
- request ID、idempotency key、latency、response size。
- `curl`、JavaScript server、C#、Python 範例 tabs。
- 401、404、409、429、503 的可理解錯誤說明。

安全要求：瀏覽器不得持有 external bearer。Playground 必須呼叫 owner-session 保護的 same-origin backend-for-frontend，再由伺服器代送；不可直接從 browser 打 external API。

**Backend gap**：需要 owner-session playground proxy 與安全活動摘要 API。

#### E. 用量與活動 `/developer/usage` — ✅ REPOSITORY（2026-08-26）

> 已交付 owner-scoped durable usage ledger、`GET /api/developer/external-voice/usage` 與登入後
> 使用量頁。只記錄通過 API key 驗證後的安全 metadata；可依近 24 小時／7 天／30 天、project
> 與 voice 篩選，摘要包含請求數、成功率、429、平均 latency、WAV 秒數與 bytes，最近活動只顯示
> server-generated request ID、狀態、錯誤類型與耗時。輸入文字、Bearer、冪等鍵、reference 與
> transcript 都不進資料表或 owner response。

最小內容：

- 請求數、成功率、429、latency、產出秒數／bytes。
- 依 project、voice、時間篩選。
- 最近活動只顯示 request ID、狀態、耗時與錯誤類型；不得顯示輸入文字、token、reference 或 transcript。
- 額度／速率與到期提醒。

**剩餘營運缺口**：rate limit、single-flight 與 idempotency 仍是單一 process memory；多 replica
或正式付費訂閱前仍須改成共享協調。usage retention／歸檔政策也需在累積正式商用資料前決定。

#### F. API 文件 `/developers/docs` — ✅ LIVE（2026-08-19）

最小內容：

- authentication、request exact shape、headers、limits。
- WAV success contract。
- stable error codes。
- retry／idempotency guidance。
- 私人開發權限與公開／商用權限的差異。
- 可複製 server-side examples；明確警告不要把 token 放進瀏覽器。

這一頁已由現有 [`EXTERNAL_VOICE_API.md`](EXTERNAL_VOICE_API.md) 轉成產品版文件並上線：
[`DeveloperDocsPage.tsx`](../src/StoryVoice.Web/src/pages/DeveloperDocsPage.tsx)，路由 `/developers/docs`，
公開區、不需登入，靜態內容不呼叫任何 API。已從 `/voices` 訂閱區塊與登入後主導覽（`AppLayout`）
兩處連結，測試見 `developer-docs.test.mjs`。內容刻意省略原始 ops 文件裡的憑證核發腳本、
grant JSON 內部欄位與檔案路徑，只留下開發者實際需要的 HTTP 合約與伺服器端範例。

### P1：公開聲線館與建立者發佈流程

#### G. 公開聲線館 `/voices`

保留現有 page shell，UI/UX 需補：

- 卡片主視覺規格。
- 單一播放器互斥：播放新卡時停止上一張。
- demo duration／progress／volume。
- mobile card、loading skeleton、error recovery。
- public、private-only、coming-soon、revoked 的清楚狀態。
- 兩張卡時只做搜尋與快捷 chips；10 張以上再做完整 filter drawer。

正式上線前還需要：production 部署新 Web bundle、啟用 catalog、建立有效 entries、安裝固定 demo，並通過公開 metadata/授權檢查。

#### H. 聲線詳情 `/voices/:alias`

最小內容：

- 較大角色視覺、角色定位、聲音特質與使用場景。
- AI 合成揭露。
- 固定示範播放器，不提供公開任意文字生成。
- 可用狀態：公開播放、私人 API、可申請訂閱、即將開放、撤銷。
- 授權摘要：可否 API、公開、商用，效期與適用範圍；只顯示公開資訊。
- CTA：播放示範、申請 API、前往控制台或登記通知。

**Backend gap**：目前沒有 voice detail DTO／endpoint。

#### I. 我的聲線 `/creator/voices`

每張 owner card 應呈現：

- 角色、聲線版本與 source type `原創合成聲線`。
- reference、transcript、manifest、terms、demo readiness。
- 私人 API／公開 catalog／訂閱商用三個分離狀態。
- 到期與撤銷。
- CTA：編輯資料、管理 demo、準備公開、管理使用權。

#### J. 合成聲線建立／編輯 wizard

建議步驟：

1. 選擇角色與聲線名稱。
2. 綁定原創合成來源、工具／模型與版本。
3. 綁定 reference audio 與完全相符 transcript。
4. 綁定 generation manifest、terms snapshot 與 license identifier。
5. 上傳／產生固定 demo。
6. 設定公開 metadata：display name、subtitle、styles、use cases、AI disclosure。
7. 設定 audience、territory、有效期與撤銷方式。
8. Review：列出 private API／公開／商用各自會開放什麼，再執行 owner action。

不要讓使用者手填 hash、owner ID、audit ID、狀態、server timestamp 或權利 boolean；這些必須由受控 backend 產生或驗證。

**Backend gap**：目前沒有 owner issue/activate/revoke API、durable audit store 或 terms review workflow；此 wizard 先做 UX prototype，不能假接 production。

#### K. 固定示範管理

- 上傳／選擇 demo、播放、時長、格式、metadata 檢查。
- 公開前預覽卡片與詳情頁。
- 新 demo 草稿、目前公開版本、替換生效時間。
- 移除／撤銷後的公開影響提示。

#### L. 授權與撤銷 timeline

- 依聲線顯示 private API、public demo、subscription commercial 的獨立事件。
- 事件包含狀態、專案／consumer、有效期、操作人、原因與影響範圍。
- 撤銷不可用「還原」假裝復原；重新開放應建立新授權版本。

### P2：訂閱與商業化

#### M. 方案與申請 `/plans`

付款尚未實作前，只能設計：

- 權限差異說明。
- API 用量與適用對象。
- `申請 API 使用`、`登記通知` 或 `聯絡合作`。

不可先放虛構價格、折扣、信用卡欄位或「立即訂閱」成功流程。

#### N. 訂閱與付款

待產品與 backend 決定後才進入：

- 方案比較、checkout、付款成功／失敗。
- subscription entitlement。
- 用量、quota、升降級、續訂、取消。
- 付款方式、帳單、發票、webhook 狀態。

這些項目目前全部是 ❌ MISSING。

### P2／P3：營運與系統畫面

- 申請審核 queue。
- Consumer／Project／Key 管理。
- Catalog publish／unpublish。
- 授權與緊急撤銷。
- 稽核事件與失敗原因。
- 服務 health、gateway、voice provider、GPU busy／degraded 狀態。
- 帳號設定、安全與通知中心。
- 404、403、expired、revoked、maintenance 等完整狀態頁；目前 App 沒有 wildcard route。

## UI/UX 第一批應交付的畫面

建議先交 8 組核心 frame，先 desktop，再補必要 mobile：

1. 開發者總覽：active private-development。
2. 開發者總覽：即將到期／expired。
3. 專案詳情與可用聲線。
4. API 金鑰列表＋一次性 secret modal。
5. Playground：idle／generating／success／429／503。
6. 公開聲線館：兩張卡、disabled、empty、error、mobile。
7. 聲線詳情：公開固定 demo＋申請 API。
8. 我的聲線＋publication readiness wizard。

第二批再做方案／申請、usage dashboard、授權 timeline、營運審核。

## 周子謙與林若晴卡片規格

卡片資料必須來自 API，不可直接 hardcode 在 React：

| 欄位 | 規則 |
|---|---|
| 角色主視覺 | 使用原創角色視覺或抽象 waveform；不要用假真人肖像 |
| 顯示名 | 使用正式核定的角色名 |
| 一句定位 | 內容待 owner 核定，不由 UI/UX 猜測 |
| AI 揭露 | 固定顯示「原創 AI 合成聲線」或核定等價文案 |
| 特質／用途 | 只顯示經 owner 核定的 tags；不要自行推測性別、年齡或人格 |
| 固定示範 | 只有已公開核准的 WAV 才能播放 |
| 狀態 | private-development、public-demo、subscription、coming-soon、expired、revoked 分開 |
| CTA | 每張卡只保留一個主要動作，依 entitlement 決定 |

目前資料判定：

- **林若晴**：私人跨專案 API 已可用；尚未公開、尚未可訂閱、沒有公開固定示範。
- **周子謙**：目前 production 沒有對外 API 或公開 catalog entry。舊資料出現「褚冥漾（周子謙授權聲線｜私人試音）」；公開卡片設計前，產品必須先確認「周子謙」是角色名、聲線名，或是褚冥漾的聲線來源名稱。因產品已改為 synthetic-only，舊「授權聲線」文案也應清理，避免暗示真人來源。

設計稿可以用兩個名稱做排版示意，但必須加上 `設計資料／尚未代表 production 已公開` 標記。

## 視覺方向

### 建議保留

- 暖紙色、琥珀、stone、深墨色與 serif 標題，延續 StoryVoice 的閱讀／聲音質感。
- 成熟、安靜、編輯感的 premium audio library；不要做成霓虹 cyberpunk AI dashboard。
- 抽象聲波、章節、聲音層次或原創角色插畫。
- 卡片以一個主 CTA、少量狀態 chips、清楚層級為主。
- 讓狀態比裝飾更醒目：公開、私人、到期、撤銷、審核中不能只靠顏色。

### Design token 起點

可從現有 [`index.css`](../src/StoryVoice.Web/src/index.css) 與 `PublicVoicesPage` 擷取：

- Surface：warm white／paper。
- Accent：amber／rose，只用於主 CTA 或警示。
- Text：stone 900／700／500。
- Heading：serif；操作與資料：sans-serif。
- Radius：卡片大圓角、控制元件中圓角。
- Motion：短淡入與播放器狀態；支援 `prefers-reduced-motion`。

## 必畫狀態

每個新畫面不能只交 happy path，至少包含：

| 類別 | 狀態 |
|---|---|
| Data | loading／skeleton、empty、no results、network error、retry |
| Feature | disabled、coming soon、maintenance |
| Entitlement | pending、active、expiring、expired、revoked、scope mismatch |
| Credential | created once、copied、downloaded、lost、rotating、revoked |
| Synthesis | idle、validating、queued／busy、generating、success、download、cancelled、failed |
| API error | 401、404、409、413、429、503 |
| Catalog | demo unavailable、authorization pending、public、removed |
| Billing | no plan、application pending、active、payment failed、cancelled；backend 完成後才啟用 |

## 安全與內容不可妥協項目

- 公開 UI 不得出現 token、內部 path、SHA、reference、transcript、private asset 或 owner GUID。
- Token 只在建立時顯示一次；之後只能顯示 prefix／末四碼。
- raw bearer 只能放其他專案的 server-side secret store，不可放 browser、localStorage 或 client bundle。
- 公開訪客只能播放固定 demo，不開放任意文字生成。
- 公開 demo、私人 API、訂閱商用是三種權限，不得用一個「可用」布林混合。
- 未有付款與 entitlement 前，不顯示假價格、假 checkout 或「已訂閱」。
- 所有聲線公開位置都顯示 AI 合成揭露。
- 到期／撤銷必須在任何生成動作前顯示並阻擋。
- 活動 log 不顯示使用者輸入文字、token、reference 或 transcript。
- synthetic-only 是預設；未來真人來源必須是另一路徑與另一次產品／安全設計。

## Design acceptance checklist

UI/UX 交付可用以下條件驗收：

- [ ] 公開、developer、creator、admin 四種使用者的入口與導覽清楚分離。
- [ ] 角色、聲線、專案、credential 四層物件不混淆。
- [ ] 林若晴不被誤標為公開／可訂閱；周子謙命名未確認前有明確 placeholder。
- [ ] 所有公開播放都標明固定 demo 與 AI 合成。
- [ ] 每張卡只有一個 primary CTA，CTA 與 entitlement 一致。
- [ ] 金鑰一次性顯示、撤銷、輪替與遺失流程完整。
- [ ] Playground 不把 external bearer 交給瀏覽器。
- [ ] 所有 loading、empty、error、expired、revoked 與 busy 狀態都有設計。
- [ ] 沒有虛構價格、使用量、方案或付款結果。
- [ ] Desktop、tablet、mobile 與 keyboard focus 都有稿。
- [ ] 色彩不是唯一狀態提示；文字與 icon 同時表達。
- [ ] 支援 reduced motion、ARIA label、播放器鍵盤操作與可讀對比。
- [ ] 新畫面標註哪些可接現有 API、哪些需要 backend，handoff 不以 mock data 冒充完成。

## 建議實作階段

### Phase A：Private API Portal

Developer overview、project detail、credential、Playground、docs、usage shell。目標是讓目前已存在的林若晴 private-development API 可以由 owner 安全理解與操作。

### Phase B：Public Catalog

部署 `/voices`、擴充卡片 DTO、聲線詳情、固定 demo、owner publication readiness。沒有有效 public entry 時維持 disabled／empty，不硬塞兩張假卡。

### Phase C：Subscription Application

先做方案說明、申請／waitlist、審核與 entitlement；之後才接正式訂閱。

### Phase D：Billing & Operations

付款、webhook、發票、quota、durable usage、admin、auditing、shared rate limit／idempotency 與多 replica 營運。

## VOAI 公開網站參考

> 研究日期：2026-08-19。只借鏡資訊架構與互動原則，不複製 VOAI 的品牌、文案、角色、卡片造型或素材。

VOAI 將首頁、聲音目錄、API、價格與登入後管理拆開，值得 StoryVoice 借鏡的不是欄位數量，而是讓訪客快速回答「這是什麼聲音、適合什麼內容、現在能做什麼」。

- 公開資訊架構：[VOAI 首頁](https://www.voai.ai/)、[VOAI API](https://www.voai.ai/voai-api)、[價格方案](https://www.voai.ai/pricing)
- 聲音角色與篩選：[VOAI 聲優模型](https://www.voai.ai/voai-actors-models)、[公開聲優陣容](https://www.voai.ai/show_iframe_component/25965829?source=live_site)
- 開發者流程：[VoiceAPI 文件](https://connect.voai.ai/doc-vocal/index.html)

可借鏡：

- 公開卡快速表達角色、聲音特質、適用內容與固定示範。
- 「選聲音 → 輸入文字 → 生成下載」的低學習成本流程，放在有權限的登入區。
- 一般訂閱、API 與企業合作分流。
- 登入後控制台顯示 key、用量與方案。

StoryVoice 必須保留的差異：

- 全站預設為 owner-created synthetic voices，不要求真人來源欄位。
- public demo、private-development 與 subscription-commercial 權限嚴格分離。
- UI 必須突出 project、期限、公開／商用範圍與撤銷。
- 付款與 entitlement 未完成前只提供申請／通知，不模擬已可購買。

## Source anchors

- Routes：[`src/StoryVoice.Web/src/App.tsx`](../src/StoryVoice.Web/src/App.tsx)
- App shell：[`src/StoryVoice.Web/src/AppLayout.tsx`](../src/StoryVoice.Web/src/AppLayout.tsx)
- 公開聲線頁：[`src/StoryVoice.Web/src/pages/PublicVoicesPage.tsx`](../src/StoryVoice.Web/src/pages/PublicVoicesPage.tsx)
- 角色庫：[`src/StoryVoice.Web/src/pages/CharacterLibraryPage.tsx`](../src/StoryVoice.Web/src/pages/CharacterLibraryPage.tsx)
- 舊聲線管理：[`src/StoryVoice.Web/src/CharacterVoiceProfilesPanel.tsx`](../src/StoryVoice.Web/src/CharacterVoiceProfilesPanel.tsx)
- 系列卡司：[`src/StoryVoice.Web/src/SeriesCastPanel.tsx`](../src/StoryVoice.Web/src/SeriesCastPanel.tsx)
- Public catalog endpoints：[`src/StoryVoice.Api/PublicVoiceCatalogEndpoints.cs`](../src/StoryVoice.Api/PublicVoiceCatalogEndpoints.cs)
- External speech endpoint：[`src/StoryVoice.Api/ExternalVoiceEndpoints.cs`](../src/StoryVoice.Api/ExternalVoiceEndpoints.cs)
- External voice contract：[`docs/EXTERNAL_VOICE_API.md`](EXTERNAL_VOICE_API.md)
- Synthetic publication contract：[`docs/VOICE_PUBLICATION_GRANT.md`](VOICE_PUBLICATION_GRANT.md)
- Web tests：`src/StoryVoice.Web/tests/public-voice-catalog.test.mjs`、`series-cast-ui.test.mjs`、`auth-flow.test.mjs`
- Integration tests：`tests/StoryVoice.IntegrationTests/ExternalVoiceDevelopmentApiTests.cs`、`PublicVoiceCatalogApiTests.cs`、`SyntheticVoiceAuthorizationApiTests.cs`

## Handoff 結論

UI/UX 可以立即開始 Phase A 的完整設計，以及 Phase B 的公開目錄／詳情視覺探索。Phase A 除 API 文件外，多數畫面還需要新 backend；Phase B 的 catalog shell 已有 source，可直接沿用與改版，但 production 沒有公開資料。Phase C、D 先做產品流程與 prototype，不應標成工程可接或 production ready。
