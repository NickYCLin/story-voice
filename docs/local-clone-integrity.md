# 自架克隆朗讀與本人錄音授權

此文件描述 repo 的候選實作，不代表任何主機已部署或開啟功能。

## 自架朗讀

`local-clone` provider 由擁有者授權的主機既有程式整合而來；原始檔
SHA-256：`0990cc2b6bf85365345484056bbee39694dd4721f28be2688a091749e7f33e06`。
整合不包含帳號設定、token、參考錄音、書籍正文或既有合成音檔。

正式配音預設關閉。經部署核准後才可設定
`LOCAL_CLONE_FORMAL_NARRATION_ENABLED=true`；API 與 worker 必須同時使用相同版本，
沿用已存在的內部 gateway token。worker 只讀掛載角色錄音 volume。
本次變更不建立 token、不執行合成、不切換任何正式音軌。

新的不可變 provider 版本為 `local-clone-integrity-v2`，組合版本為
`local-clone-complete-turns-v2`。舊 cast 的版本不可直接改寫或冒用新版本。
每個使用中的角色必須具有同一 owner 的 Ready Clone、已確認逐字稿，以及現有正式
朗讀同意紀錄。參考錄音以實際 bytes 驗證 SHA-256，並防止路徑或符號連結逸出儲存根目錄。
旁白沿用既有策略：選取 manifest 中文字量最多的角色作為 fallback。
這是組合策略，不是自動判定故事主角；部署前必須核對角色配置。

英文、數字與符號不能因為不是 CJK 就略過；僅 Unicode 標點與空白可明確略過。
整段失敗可逐句重試，但任何有意義句子失敗都使工作失敗，不會發布部分音訊。
成功檔案旁會保存 `.integrity.json`，包含來源／cast／plan 識別、文字雜湊與
UTF-16 單位數、chunk 狀態及音訊 bytes／SHA-256；不包含原文或逐字稿。
這份收據證明程式沒有忽略必須合成的片段，不能證明模型逐字正確發音。
API 的公開音訊 route 不會公開此收據。

新 cache namespace 與所有不可變工作欄位、provider 版本、參考錄音及逐字稿雜湊綁定。
只重用 SHA-256 sidecar 相符的 chunk，不信任舊 cache。失敗時保留 cache 與失敗收據；
成功後移除 cache。朗讀時間軸介面保持相容，但此 provider 尚未回傳逐 turn timing。

## 本人錄音授權 v2

舊 `storyvoice-synthetic-voice-authorization/v1` 嚴格語義保持不變。
本人錄音使用 `storyvoice-voice-authorization/v2`，根物件增加
`sourceKind: "owner-recorded"`。`creation.generationManifestSha256` 改為
`creation.provenanceManifestSha256`，內容應如實記錄錄音來源，而非宣稱純合成。
其餘 creation 欄位記錄衍生語音使用的服務／模型與授權條款。

v2 `sourceClaims` 的完整欄位如下：

```json
{
  "performerOwnerId": "同 ownerId 的標準 UUID",
  "serviceProviderOwnerId": "同 ownerId 的標準 UUID",
  "recordedByOwner": true,
  "ownerIsVoicePerformer": true,
  "recordingRightsOwned": true,
  "noThirdPartyCharacterOrBrandClaimed": true
}
```

不得加入「未使用真人聲音」之類與本人錄音矛盾的 v1 欄位。
沿用 authenticated-owner-action、auditEventId、精確素材雜湊、有效期限、撤銷、
各用途權限、consumer family 與地區檢查；不需要另提供身分證或外部第三人授權書。
既有設定欄位名稱中的 `SyntheticVoiceAuthorization` 為向後相容保留，現在可承載 v2。
現有公開／商業入口仍要求所有列出的用途明確允許，並未擴大為可猜測或省略權限。
開發用的純合成 grant 不適用本人錄音；需使用既有商業 usage grant 綁定此授權檔雜湊。

公開卡片僅新增「本人錄音授權的 AI 合成語音」揭露，其他公開 DTO 保持白名單。
此變更不產生正式授權文件、消費者憑證或公開 demo，也不把私人角色／書籍資料列入 repo。

## Migration 驗證與回退邊界

`PlaybackAndUsagePostgreSqlMigrationTests` 在隔離 PostgreSQL 17 中，從
`20260826090725_AddExternalVoiceUsageLedger` 逐一升級 timeline、listening progress、
attempt usage；用兩位人工 owner 與六筆工作核對既有資料不變，測試進度範圍、
usage owner FK 與數量限制，並執行 Down 與再次 Up。
Down 會刪除新增表中的紀錄，測試明確確認此邊界；不可把它描述為無損回退。
正式回退優先使用與新增 schema 相容的舊映像，禁止未審核的自動 Down。
這些測試未讀取、匯出或還原正式資料庫，不能替代正式維護窗口與備份核對。

## 私人音軌重用與保留

`GET /api/books/{bookId}/narrations/tracks` 列出同 owner、同一本未封存書的所有工作，
包括待啟用與歷史版本，並檢查完成狀態、實體路徑、檔案存在和 bytes。
這不是逐字或音訊內容的完整驗證，也不把不同來源版本視為同一正文。
`GET /tracks/{jobId}/audio` 是同一書籍路徑下的私人 range 播放入口；
原本公開狀態的音訊 route 不會因此開放 staged／historical 音訊。

`PUT /tracks/selection` 提供 `jobId`、`expectedRevision` 與
`expectedArtifactVersion`。成功選擇追加紀錄，唯一索引阻止競爭寫入；
相同請求重送不追加，過期選擇回應 409。重新選回舊音軌亦追加新版本。
它只重用既有檔案，不複製或重新合成，也不改動工作、cast 或整個系列的 active pointer。
書籍頁的「私人音軌版本」提供檢視、試聽與明確儲存操作。

建立新批次不再隱式刪除失敗批次、工作或完成音檔。新 migration
`20261013042545_PreserveNarrationTracks` 允許同 cast 保留已失敗的批次，
仍限制最多一個非失敗批次；前代 epoch 只從 Activated 批次解析。
完整系列啟用仍經原有 serializable transaction 與圖形約束；API 進入該流程前，
會再檢查新音檔與原音檔存在且 bytes 相符，不會因 ReadyToActivate 就略過檢查。
私人選擇紀錄的 FK 阻止刪除其引用工作。

本 migration 的 Down 會刪除私人選擇紀錄；若存在同 cast 的保留重建歷史，
Down 會明確拒絕，避免為了回復舊唯一索引而刪除歷史。正式回退不得靠丟棄
歷史資料解決，需保留此 schema 並使用相容版本。
