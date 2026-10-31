# 外部語音 API 共用請求額度

`ExternalVoiceApi.SharedRateLimitEnabled` 預設為 `false`。啟用後，外部語音 API 與登入後的 Playground 都會在既有本機額度檢查後，再取得同一個 Redis consumer 額度；未取得額度就不會執行合成。

Compose 使用 `EXTERNAL_VOICE_API_SHARED_RATE_LIMIT_ENABLED=true`。所有 API 執行個體必須使用相同的 Redis database、consumer key ID 設定與 `ExternalVoiceApi.RequestsPerMinute`，才能得到一致的額度。不同環境應使用不同 Redis database，不能只靠 API process 名稱區分。

## 計數方式

- 固定視窗從該 consumer 的第一個通過請求起算 60 秒；不是任意連續 60 秒的滑動視窗，視窗交界仍可能出現兩批請求。
- Redis Lua 同時檢查計數、增加額度與設定有效期。後續通過或被拒絕的請求都不延長視窗，重啟 API 也不重設額度。
- 超過上限回 429 `rate_limited`，`Retry-After` 由 Redis 剩餘有效期向上取整，介於 1 至 60 秒。
- 以 consumer 分組，原始 key ID 經 SHA-256 後才放入 Redis key。Redis value 只有計數，不包含 credential、owner、正文、音訊或私人素材路徑。
- 外部入口須先通過 bearer 驗證；Playground 須先通過 owner session、CSRF 與專案歸屬檢查。匿名、無效權杖或未通過 CSRF 的請求不消耗這份共用 consumer 額度。
- 這是請求上限，包含重送與 idempotency replay，不是成功合成次數。既有本機額度和匿名防洪仍生效，因此特定執行個體可能比共用額度更早回覆 429。

原子腳本與剩餘時間分別使用 Redis 的 [EVAL](https://redis.io/docs/latest/commands/eval/) 與 [PTTL](https://redis.io/docs/latest/commands/pttl/)。

## 無法確認額度時

Redis 不可用、回應格式錯誤、計數損壞或計數缺少 TTL 時，回 503 `synthesis_unavailable`，`Retry-After: 30`。Redis command 最多等候 2 秒；共用連線的首次建立仍使用既有 `ConnectionStrings:Redis` 連線設定。失敗不退回本機獨立額度，也不將連線診斷傳給呼叫者。

取消或逾時不代表已送出的 Redis command 一定未執行。程式不自動重試、不返還不確定的額度，以免重複放行；此時視窗內可用次數可能減少。

Redis 重啟、資料遺失、淘汰或 failover 仍可能失去計數，因此這不是永久硬額度或計費來源。正式啟用前須確認 Redis 可用性、持久化與淘汰策略。共用 idempotency、single-flight 與公平排程仍未完成，本次不將正式部署改成多 replica。

## 驗證前的共用防洪

`ExternalVoiceApi.SharedPreAuthenticationRateLimitEnabled` 是獨立開關，預設 `false`。啟用時另須設定 `SharedPreAuthenticationHashKey`，值為私下產生的 32-byte 隨機金鑰，以 64 個十六進位字元表示；所有 API 執行個體必須使用相同值。設定驗證失敗不會回顯金鑰，請勿將實際值放進 Git、命令輸出或範例文件。

Compose 對應 `EXTERNAL_VOICE_API_SHARED_PRE_AUTHENTICATION_RATE_LIMIT_ENABLED` 與 `EXTERNAL_VOICE_API_SHARED_PRE_AUTHENTICATION_HASH_KEY`。來源與全域上限沿用 `PreAuthenticationRequestsPerMinute`／`PreAuthenticationGlobalRequestsPerMinute`。本機防洪會先執行，再向 Redis 取得共用額度，避免所有拒絕請求都必須查 Redis。

來源只採用可信代理處理後的 `RemoteIpAddress`。IPv4-mapped IPv6 與 IPv4 共用來源，原生 IPv6 按 `/64` 分組；正規化結果經 HMAC-SHA-256 對應到固定 256 個桶。不同來源可能共用桶，這是控制記憶體與 Redis key 數量的取捨。Redis key 只包含桶編號，不保存 IP 或 HMAC 金鑰。輪替雜湊金鑰會改變來源分桶，須讓所有執行個體同步切換，不能視為保留來源視窗的無縫輪替。

來源與全域計數由同一段 Lua 一次驗證及增加，任一超限就不消耗另一份額度；任一計數損壞或缺少 TTL 均回 503。兩個 key 使用同一個 Redis Cluster hash tag；目前整合測試使用單一 Redis，沒有宣稱已驗收 Cluster 或 failover。

這層在 bearer 驗證、受管金鑰查詢與 usage ledger 之前執行。它依已選中的 speech endpoint 判定，包含大小寫或尾端斜線等路由可接受的寫法；標準路徑驗證仍在原端點執行。其他頁面、Playground、公開目錄與健康檢查不使用這份匿名入口額度。無法取得 Redis 時，僅外部 speech POST 回 503，不會阻擋其他路由。

## 驗證範圍

使用真正的 Redis 7.4 測試兩條獨立連線並行送出 32 個請求只放行 3 個、不同 consumer 隔離、新連線保留額度、視窗過期、異常計數、無 TTL 與 Redis 暫停回應。另以兩個 API host 混合外部與 Playground 請求，確認共用額度及重啟後仍回 429；Redis 無法取得時兩個入口均回 503。測試合成使用固定回應，沒有呼叫真實 GPU 或付費 provider。

本次未啟用正式設定，未執行正式多 replica 驗收。

驗證前防洪另以真正的 Redis 檢查跨連線來源正規化、32 個不同來源並行共用全域上限、拒絕與計數異常時沒有部分扣額度；兩個 API host 則驗證匿名要求先回 401，來源額度用完後改回 429、路由變體同樣受限、Redis 不可用時回 503，以及健康檢查不受影響。
