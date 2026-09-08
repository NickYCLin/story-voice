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

Redis 重啟、資料遺失、淘汰或 failover 仍可能失去計數，因此這不是永久硬額度或計費來源。正式啟用前須確認 Redis 可用性、持久化與淘汰策略。共用 idempotency、single-flight、公平排程與匿名入口防洪仍未完成，本次不將正式部署改成多 replica。

## 驗證範圍

使用真正的 Redis 7.4 測試兩條獨立連線並行送出 32 個請求只放行 3 個、不同 consumer 隔離、新連線保留額度、視窗過期、異常計數、無 TTL 與 Redis 暫停回應。另以兩個 API host 混合外部與 Playground 請求，確認共用額度及重啟後仍回 429；Redis 無法取得時兩個入口均回 503。測試合成使用固定回應，沒有呼叫真實 GPU 或付費 provider。

本次未啟用正式設定，未執行正式多 replica 驗收。
