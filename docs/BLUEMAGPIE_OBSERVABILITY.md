# BlueMagpie 合成監測

Worker 的 `StoryVoice.BlueMagpie` Meter 記錄每次合成嘗試。重新排程同一工作會產生另一次
attempt；這不是唯一工作數，也不是計費帳本。這些指標可搭配 structured logs 檢查長文
生成、途中恢復與快取成效，不需要開啟正式配音功能才能部署監測程式。

## 指標

所有名稱都以 `storyvoice.bluemagpie.` 開頭。

| 名稱 | 單位／型態 | 意義與標籤 |
|---|---|---|
| `active_attempts` | attempt／gauge | 目前進行中的嘗試；中途接上監測也會讀到目前值 |
| `attempts` | attempt／counter | 已結束嘗試，依 `outcome` 區分結果 |
| `attempt.duration` | 秒／histogram | 整次嘗試耗時，包含快取、gateway 等待、合成與檔案處理；依 `outcome` 區分 |
| `resolved_chunks` | chunk／counter | 成功取得並驗證的片段；`cache=hit` 或 `miss` |
| `resolved_audio_bytes` | byte／counter | 本次讀取／產生的 WAV 大小，包含 WAV header；依 `cache` 區分，重試可能再次計入同一片段 |
| `chunk.duration` | 秒／histogram | 成功取得一段音訊的耗時，含快取檢查、鎖與可能的 gateway 呼叫；依 `cache` 區分 |
| `provider.duration` | 秒／histogram | 真正呼叫 gateway 並驗證回應的耗時，含網路與 GPU 排隊；`outcome=success/failed/cancelled`，快取命中不計入 |
| `rendered_audio.duration` | 秒／histogram | 成功嘗試的合成 PCM 時間軸總長，含開頭與段間停頓；`reused_cache` 表示是否曾命中快取 |
| `real_time_factor` | 比率／histogram | 整次嘗試耗時除以上述音訊秒數；依 `reused_cache` 區分 |

`attempts` 與 `attempt.duration` 的 `outcome` 固定為：

- `success`：合成與快取 scope 清理完成。
- `cancelled`：收到取消，包括上層 timeout 或 Worker 停止；不表示一定由使用者取消。
- `rejected`：正式 gate、預算、輸入或固定模型／聲線 contract 未通過。
- `cache_capacity`：快取容量不足。
- `provider_unavailable`：gateway 暫時不可用。
- `failed`：其他合成、檔案或清理失敗。

失敗、取消或缺少完整有效時間軸時，不填入音訊時長與 RTF。音訊秒數來自 composer
實測的 PCM 時間軸，不是字數估算，也不是 MP3 encoder padding 後的檔案長度。
有命中快取的 RTF 包含重用成果，不能直接當作 GPU 從零生成的速度。

標籤只包含固定結果與快取狀態，不放入 owner、book、job ID、正文、聲線提示、路徑或
例外訊息。每次結束的 log 另包含 JobId、耗時、片段總數、命中／未命中數、WAV bytes、
音訊秒數及 RTF；有進度且距離上次進度 log 達 60 秒時，再輸出一次進度。若單一呼叫
卡住，這段期間不會冒出假的進度，應搭配 active count、上層 timeout 與 gateway 監測判斷。

## 讀取方式

在可存取 Worker 診斷通道的環境，用與 Worker 相同的執行帳號讀取。先取得正確的
Worker PID，再將下列 `<worker-pid>` 替換成該 PID：

```sh
dotnet-counters monitor --process-id <worker-pid> --counters StoryVoice.BlueMagpie
dotnet-counters collect --process-id <worker-pid> --counters StoryVoice.BlueMagpie --format json --output bluemagpie-counters.json
```

工具需另外備妥；容器外連線也需可達的 diagnostic socket／port 與相同權限。
參數與連線方式見 [Microsoft 的 dotnet-counters 文件](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters)。

## 匯出至監測系統

Worker 已接上 OpenTelemetry OTLP/HTTP exporter，預設關閉。準備好可接受 protobuf metrics
的內網 Collector 後，設定以下環境變數並重新啟動 Worker：

```dotenv
WORKER_METRICS_ENABLED=true
WORKER_METRICS_ENDPOINT=http://otel-collector:4318/v1/metrics
WORKER_METRICS_EXPORT_INTERVAL_SECONDS=30
WORKER_METRICS_EXPORT_TIMEOUT_SECONDS=5
```

`otel-collector` 是示意服務名稱；另外提供選用的 `compose.metrics.yml`，內含 Collector、Prometheus
與經測試的告警規則，服務名稱為 `metrics-collector`。啟動與驗證見 [METRICS_STACK.md](METRICS_STACK.md)。
端點必須完整包含 `/v1/metrics`，可帶前置路徑，但不能含帳密、query 或 fragment。
遠端傳輸可使用 HTTPS；此入口不設定授權 header，需要外送至有驗證的監測服務時，由
受信任的內網 Collector 處理。不要將正式端點或監測憑證寫入 repository。

不使用 Compose 時，對應設定為 `WorkerMetrics__Enabled`、`WorkerMetrics__Endpoint`、
`WorkerMetrics__ExportIntervalSeconds`、`WorkerMetrics__ExportTimeoutSeconds`。
匯出間隔限定 5 至 3600 秒，逾時限定 1 至 30 秒且不得長於間隔。啟用但設定無效時，
Worker 會在啟動時拒絕該設定；關閉時不註冊 OpenTelemetry pipeline，也不發送資料。

匯出只訂閱 `StoryVoice.BlueMagpie`，不另外匯出 log、trace、HTTP／資料庫 instrumentation
或 exemplar。Resource 使用固定 `service.name=storyvoice-worker` 與每次啟動產生的隨機
`service.instance.id`，讓多個 Worker 的累計值可分開辨識；不繼承環境的 resource attributes。
Exporter 的端點、protocol、header、間隔與逾時由上述程式設定決定，不套用通用
`OTEL_EXPORTER_OTLP_*` 的連線設定。

Counter 與 histogram 採 cumulative temporality；Worker 重啟後會重新計數。耗時 histogram
以秒分桶，涵蓋 0.1 秒到 3 小時以上；RTF 分桶涵蓋 0.05 至 10 以上。這些指標仍是診斷
統計，不是唯一工作數或計費來源。

匯出在背景執行，Collector 錯誤或無回應不會重新合成、停止 Worker 或跟隨 HTTP redirect。
沒有持久化的匯出佇列；程序退出前未成功匯出的資料可能遺失。營運端仍須確認 Collector
實際收到資料、設定保留期間與告警，並演練接收中斷及 Worker 重啟。

協定與 SDK 設定見 [OpenTelemetry .NET exporter 文件](https://opentelemetry.io/docs/languages/dotnet/exporters/)
及 [OTLP exporter 1.18.0 說明](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.18.0/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md)。
Log 與收集檔可能包含營運識別資料，保留在 Git 外。

## 驗證邊界

測試以本機 HTTP 接收端確認真正的 OTLP protobuf request、九項指標、累計值、標籤與
resource 範圍，並確認停用、503、redirect 與逾時行為；這不代表正式監測已收集資料。
另以合成 WAV、固定測試回應與真實檔案快取，驗證中斷後部分命中、全命中時不再呼叫
gateway、取消／失敗歸類、active gauge 以及缺少時間軸時不捏造 RTF。這些證據不包含
NVIDIA／ARM64 上的實際模型速度、GPU／LLM 共存壓力或完整書籍生成。
`BLUEMAGPIE_FORMAL_NARRATION_ENABLED` 仍預設為 `false`。
