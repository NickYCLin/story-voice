# 合成聲線跨專案 API

StoryVoice 的 external voice API 只接受系統擁有者自行建立、沒有真人聲音來源、也不模仿
可識別真人的合成聲線。HTTP request 固定只接受 `voice` 與 `text`，並分成兩個不可互換
的 access tier：

- `private-development`：短期、私人、非公開、非商用的第一階段開發接用。
- `subscription-commercial`：完成公開／商用權利鏈後的訂閱商用接用。

目前 repository 預設 `ExternalVoiceApi.Enabled=false`、`VoiceCatalog.Enabled=false`、
Consumers 與 catalog Entries 都是空集合，且不包含任何真實 token 或授權素材。

## HTTP contract

兩種 tier 共用：

~~~text
POST /api/external/v1/speech
Idempotency-Key: <16-64 ASCII letters, digits, underscore or hyphen>
Content-Type: application/json
~~~

request exact shape：

~~~json
{
  "voice": "<authorized-voice-alias>",
  "text": "<authorized-text>"
}
~~~

禁止 query string、Content-Encoding、未知或重複 JSON property。成功回應為
`audio/wav`，並附 private／no-store headers。

Bearer prefix 與 tier 強制綁定：

| AccessTier | Bearer prefix | 權利範圍 |
|---|---|---|
| `private-development` | `svd1.<key-id>.<43-secret>` | 最長 30 天，私人非公開非商用 |
| `subscription-commercial` | `svv1.<key-id>.<43-secret>` | 完整訂閱商用授權鏈 |

即使 token 的完整 SHA-256 與設定相符，錯誤 prefix 仍回 401；兩種 grant schema 也不能
互換。

~~~bash
curl --fail-with-body --request POST --header "Authorization: Bearer $STORYVOICE_VOICE_TOKEN" --header "Idempotency-Key: <unique-16-to-64-character-key>" --header "Content-Type: application/json" --data '{"voice":"<authorized-alias>","text":"<authorized-text>"}' --output sample.wav https://<host>/api/external/v1/speech
~~~

| HTTP | code | 意義 |
|---:|---|---|
| 400 | invalid_request | path、JSON、欄位、文字或 idempotency key 無效 |
| 401 | invalid_api_key | token、prefix、tier 或 consumer 無效 |
| 404 | voice_not_available | voice、grant、素材、owner、project 或 profile 交集失敗 |
| 409 | idempotency_conflict | 同一 key 已綁定不同 request |
| 413 | request_too_large | body 超過上限 |
| 415 | unsupported_media_type | 不是 application/json 或帶 Content-Encoding |
| 429 | rate_limited | consumer 速率／冪等視窗或 GPU 併發限制（皆按 consumer 各自計算），依 Retry-After |
| 503 | synthesis_unavailable | gateway 或合成服務暫時不可用，帶 Retry-After |

失敗的請求不會被冪等快取釘住：收到 429／503 後用同一個 Idempotency-Key 重試會真正重新執行；只有成功的音訊會在 TTL 內以同一 key 重播。

## 安全產生 credential

已登入且已有 API 專案的 owner，可從 `/developer/credentials` 建立、換發或撤銷受管
credential。完整 bearer token 只在建立或換發回應顯示一次，資料庫只保存小寫
SHA-256；換發可選擇立即停用舊金鑰，或保留 60／1,440 分鐘重疊時間。清單與異動紀錄
都以目前登入 owner 為範圍，其他 owner 的 credential ID 固定視為不存在。

下列 PowerShell 工具仍用於初次建立 consumer、離線維運或部署設定金鑰，不取代登入後的
受管 credential 流程。

[New-ExternalVoiceApiCredential.ps1](../scripts/New-ExternalVoiceApiCredential.ps1) 會依
`AccessTier` 產生正確 prefix。部署或自動化時應使用 `OutputPath` 安全模式：

~~~powershell
./scripts/New-ExternalVoiceApiCredential.ps1 `
  -KeyId '<canonical-consumer-key-id>' `
  -AccessTier 'private-development' `
  -OutputPath '<new-private-credential.json>'
~~~

`OutputPath` 必須不存在。Windows 檔案 ACL 只允許目前使用者、SYSTEM 與
Administrators；Linux mode 固定 0600。檔案內才包含 raw bearer token，console result
只包含 path、key ID、tier 與 SHA-256，不回傳 token。

未提供 `OutputPath` 是人工互動模式，會在 stdout 顯示 token 一次：

~~~powershell
./scripts/New-ExternalVoiceApiCredential.ps1 `
  -KeyId '<canonical-consumer-key-id>' `
  -AccessTier 'subscription-commercial'
~~~

raw token 只能交付 consumer secret store；設定只保存完整 token 的 SHA-256。

## 第一階段：private-development

private tier 不需要 VoiceCatalog entry、固定公開 demo、provider commercial rights、公開
authorization、consumer family 或 territory。`VoiceCatalog.Enabled` 應維持 `false`，公開
catalog 與 demo route 不會註冊；帶 private token 呼叫公開 route 仍是 404。

每個 private consumer 只允許一個 voice grant，consumer 與 grant window 都不得超過
30 天。grant exact schema 是 `voice-api-synthetic-development-grant/v1`；schema 與固定
origin 本身表示「擁有者自建的純合成聲線、沒有真人來源、不模仿可識別真人」，不加入
未經系統驗真的 activation 或稽核欄位：

~~~json
{
  "schema": "voice-api-synthetic-development-grant/v1",
  "grantId": "<generated-canonical-id>",
  "consumerKeyId": "<consumer-key-id>",
  "ownerId": "<owner-guid>",
  "voiceAlias": "<voice-alias>",
  "characterProfileId": "<active-profile-guid>",
  "referenceAudioSha256": "<exact-reference-wav-sha256>",
  "expectedTranscriptCanonicalSha256": "<canonical-transcript-sha256>",
  "projectId": "<canonical-project-id>",
  "effectiveAtUtc": "2026-08-19T00:00:00.0000000+00:00",
  "expiresAtUtc": "2026-08-26T00:00:00.0000000+00:00",
  "revokedAtUtc": null,
  "origin": "owner-created-synthetic-no-human-source-no-identifiable-imitation"
}
~~~

用實際 reference WAV 與已確認逐字稿產生 exact artifact：

~~~powershell
$parameters = @{
  ConsumerKeyId = '<consumer-key-id>'
  ProjectId = '<canonical-project-id>'
  OwnerId = '<owner-guid>'
  VoiceAlias = '<voice-alias>'
  CharacterProfileId = '<profile-guid>'
  ReferenceAudioPath = '<private-reference.wav>'
  ExpectedTranscriptCanonicalPath = '<private-transcript.txt>'
  EffectiveAtUtc = '<UTC-time>'
  ExpiresAtUtc = '<UTC-time-within-30-days>'
  OutputPath = '<new-private-development-grant.json>'
}
./scripts/New-ExternalSyntheticVoiceDevelopmentGrant.ps1 @parameters
~~~

private consumer 設定不包含商用欄位：

~~~json
{
  "ExternalVoiceApi": {
    "Enabled": false,
    "Consumers": {
      "<consumer-key-id>": {
        "AccessTier": "private-development",
        "DisplayName": "<private-project-name>",
        "ProjectId": "<same-project-id-as-grant>",
        "OwnerId": "<same-owner-guid-as-grant-and-profile>",
        "TokenSha256": "<sha256-of-full-svd1-token>",
        "EffectiveAtUtc": "<UTC-containing-grant-window>",
        "ExpiresAtUtc": "<UTC-containing-grant-window-within-30-days>",
        "AllowedVoices": {
          "<voice-alias>": {
            "AuthorizationEvidenceRelativePath": "<development-grant.json>",
            "AuthorizationEvidenceSha256": "<exact-grant-bytes-sha256>",
            "RevokedAtUtc": null
          }
        }
      }
    }
  }
}
~~~

grant 位於 `LocalClonePreview.AssetRootPath` 下。runtime 在 GPU 呼叫前逐次驗證 token tier、
consumer、owner、project、alias、profile、30 天期間、撤銷狀態、grant bytes／SHA、實際
reference WAV bytes／SHA、canonical transcript bytes／SHA，以及資料庫中的 owner-scoped
active profile。任一失敗統一隱藏為 404，gateway 呼叫數保持 0。

`LocalClonePreview.Enabled` 可以維持 `false`；external API 只重用其私有 asset root、
profile allowlist、internal gateway token 與 synthesizer，不會開啟瀏覽器試音 route。

## 第二階段：subscription-commercial

commercial tier 保留完整既有鏈：

1. active `storyvoice-synthetic-voice-authorization/v1`；
2. 有效 VoiceCatalog entry、generation manifest、terms snapshot 與 fixed demo；
3. active `voice-api-synthetic-usage-grant/v1`；
4. consumer family、territory、owner、project、profile、reference、transcript 與期間交集。

commercial consumer 必須明確設定 `AccessTier=subscription-commercial`，並保留
`ConsumerFamilyId` 與 `TerritoryCountryCode`：

~~~json
{
  "AccessTier": "subscription-commercial",
  "DisplayName": "<consumer-name>",
  "ProjectId": "<project-id>",
  "ConsumerFamilyId": "<authorized-family>",
  "TerritoryCountryCode": "TW",
  "OwnerId": "<owner-guid>",
  "TokenSha256": "<sha256-of-full-svv1-token>",
  "EffectiveAtUtc": "<UTC-containing-usage-window>",
  "ExpiresAtUtc": "<UTC-containing-usage-window>",
  "AllowedVoices": {
    "<voice-alias>": {
      "AuthorizationEvidenceRelativePath": "<commercial-usage-grant.json>",
      "AuthorizationEvidenceSha256": "<exact-usage-grant-bytes-sha256>",
      "RevokedAtUtc": null
    }
  }
}
~~~

`VoiceCatalog.Enabled=false` 可以隱藏公開 route，但 commercial runtime 仍要求 catalog
Entries 與全部 supporting files；private grant 不能拿來繞過這條鏈。commercial usage draft
仍由 [New-ExternalSyntheticVoiceUsageGrantDraft.ps1](../scripts/New-ExternalSyntheticVoiceUsageGrantDraft.ps1)
產生。

## 啟用與撤銷

1. 先以安全 helper 建立 tier 對應 token，將 raw token 留在權限受控 secret file。
2. 建立 exact grant，保存其完整 bytes SHA-256。
3. 以受保護設定注入 consumer；先保持 global `Enabled=false` 完成離線驗證。
4. 原子部署設定後才開啟 ExternalVoiceApi；private 階段保持 VoiceCatalog false／Entries 空。
5. 緊急停止可設定 voice grant 的 `RevokedAtUtc`、移除 grant／consumer，或關閉 global
   `Enabled`，再確認舊呼叫回 401 或 404。

目前 rate limit、single-flight 與 idempotency cache 是單一 API process 內的有界狀態，
只支援一個 API replica。正式多 replica 或付費訂閱前，必須改用共享協調／entitlement
storage，並完成實際 provider terms 與商用權利審查。
