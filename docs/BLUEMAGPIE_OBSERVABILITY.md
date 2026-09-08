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

目前 repository 沒有安裝常駐 metrics exporter 或儀表板。若營運環境接入
OpenTelemetry，需自行訂閱 `StoryVoice.BlueMagpie` Meter、設定儲存與告警；單元測試讀到
指標不代表正式監測已收集資料。Log 與收集檔可能包含營運識別資料，保留在 Git 外。

## 驗證邊界

測試使用合成 WAV、固定測試回應與真實檔案快取，驗證中斷後部分命中、全命中時不再呼叫
gateway、取消／失敗歸類、active gauge 以及缺少時間軸時不捏造 RTF。這些證據不包含
NVIDIA／ARM64 上的實際模型速度、GPU／LLM 共存壓力或完整書籍生成。
`BLUEMAGPIE_FORMAL_NARRATION_ENABLED` 仍預設為 `false`。
