# Worker 監測收集與告警範例

`compose.metrics.yml` 提供獨立選用的 Collector 與 Prometheus，不會自動開啟 Worker 匯出或正式配音。
映像固定為 Collector Contrib 0.160.0、Prometheus 3.14.0，並鎖定下載時確認的 digest。

## 啟動與連接

在 repository 根目錄執行：

```sh
docker compose -f compose.yml -f compose.metrics.yml --profile metrics up -d metrics-collector metrics-prometheus
```

Prometheus 只綁定 `127.0.0.1:9090`，可用 `STORYVOICE_METRICS_PORT` 改埠。Collector 的 4318／9464
僅供 Compose 網路使用。介面沒有登入驗證，不應直接改成對外綁定。

要讓既有 Worker 送入這組 Collector，在部署環境設定以下值，再重建 Worker 容器：

```dotenv
WORKER_METRICS_ENABLED=true
WORKER_METRICS_ENDPOINT=http://metrics-collector:4318/v1/metrics
WORKER_METRICS_EXPORT_INTERVAL_SECONDS=30
WORKER_METRICS_EXPORT_TIMEOUT_SECONDS=5
```

Worker 預設仍關閉匯出；只啟動監測服務時不會有 Worker 指標。Collector 只接受
`storyvoice-worker`／`StoryVoice.BlueMagpie` 的指定名稱前綴，移除額外 resource／datapoint 欄位，
保留服務名稱、程序識別及 outcome／cache／reused_cache。它不收集 log、trace 或正文。

Counter 以 Worker instance 分開保存；Prometheus 每 15 秒抓取一次。Collector 對兩分鐘沒更新的
series 停止輸出，範例以 Worker 每 30 秒匯出為前提；修改間隔時須一起調整過期與告警時間。
Collector 重啟會失去尚未抓取的記憶體資料。Prometheus 使用獨立 volume，設定 24 小時及 1 GiB
的保留條件；WAL、head 與整理中的資料仍需額外磁碟空間，這不是硬性磁碟上限。
見 [Prometheus 儲存說明](https://prometheus.io/docs/prometheus/latest/storage/)。

## 查詢與告警

在 Prometheus 的 Query 頁可使用：

```promql
sum(storyvoice_bluemagpie_active_attempts{job="storyvoice-worker"})
sum by (outcome) (increase(storyvoice_bluemagpie_attempts_total{job="storyvoice-worker"}[15m]))
histogram_quantile(0.95, sum by (le) (rate(storyvoice_bluemagpie_real_time_factor_bucket{job="storyvoice-worker",reused_cache="false"}[1h])))
```

沒有資料與數值為零的意義不同；沒有完成嘗試時，不把缺少的 RTF 當成零。

| 規則 | 觸發條件 |
|---|---|
| CollectorUnavailable | Prometheus 無法抓取 Collector，持續 2 分鐘 |
| WorkerMetricsMissing | Collector 可用但沒有 Worker active gauge，持續 10 分鐘；全部 Worker 共用判斷，不能偵測單一 replica 消失 |
| BlueMagpieRepeatedFailures | 最近 15 分鐘至少 3 次 failed／provider_unavailable／cache_capacity，且占成功與上述失敗至少一半，持續 5 分鐘；cancelled／rejected 不列入 |

這些門檻是可測試的初始範例，正式使用前需依流量調整。規則會出現在 Prometheus Alerts 頁；
目前沒有設定 Alertmanager 或通知收件人，也不會寄送訊息。正式監測部署、通知與故障演練仍未完成。

## 驗證

CI 會執行 Collector validate、promtool 設定檢查、六組告警測試，以及真正 Collector → Prometheus
接收測試。`ops/metrics/compose.test.yml` 只供本機／CI 測試，將 OTLP 綁定 loopback 15318；
`tests/metrics/test_stack.py` 限制使用 loopback，檢查兩份累計值不合併、其他服務／Meter／名稱被排除，
以及額外識別欄位不進入 Prometheus。測試資料全為合成內容。

2026-09-08 本機另以兩個獨立 .NET 程序執行真正 Worker OTLP/protobuf exporter，確認九項指標、
histogram 單位、快取標籤與不同 instance；Prometheus 容器重建後仍可查到已存測試資料。
這是 Windows Docker 的 Linux x86_64 驗證，不代表 ARM64／NVIDIA、正式流量或通知已驗收。

設定格式見 [Collector 文件](https://opentelemetry.io/docs/collector/configuration/)、
[Prometheus exporter](https://github.com/open-telemetry/opentelemetry-collector-contrib/blob/v0.160.0/exporter/prometheusexporter/README.md)
與 [promtool 規則測試](https://prometheus.io/docs/prometheus/latest/configuration/unit_testing_rules/)。
