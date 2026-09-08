# Worker 平行配音

`Narration:MaximumConcurrentJobs` 控制每個 Worker process 同時處理的工作數，允許 1～4，
預設為 1。Docker Compose 可在 `.env` 設定：

```dotenv
NARRATION_MAX_CONCURRENT_JOBS=2
```

重新啟動 Worker 才會套用。沒有修改既有正式環境設定；單 GPU 的 BlueMagpie 環境先保留 1，
它的 gateway 仍有 GPU 互斥鎖，增加配音工作數不代表同一 GPU 能平行生成。
提高工作數前，需確認記憶體、音訊暫存空間與 provider 的同時呼叫限制足夠。

排程器只在有空位時領取工作，不預先占用等待中的工作租約。領取與過期租約回收共用
單一循環，每份已領取工作則各自使用資料庫 scope、租約、暫存檔與取消／逾時控制。
一份完成便可補上下一份，另一份失敗不會中止整個 Worker。

Worker 停止時會通知所有執行中的工作並等待收尾。各 provider 仍沿用既有恢復規則：
BlueMagpie 可在 cooldown 後重試，其他工作可能等待租約到期後處理；VoAI 不會因此開啟
自動付費重播。完整批次仍須全部完成後由使用者啟用，平行處理不會逐冊切換目前音訊。

這是**不同配音工作之間**的平行處理，每份工作內的片段仍按原 provider 順序產生。
此上限不是跨 replica 的總額度，也不是使用者公平排程或計費 hard quota。

測試使用真實 PostgreSQL 與合成測試 provider，驗證兩份工作同時執行、第三份等待空位、
租約／音檔互不共用，以及停止後的新 Worker 透過過期租約完成剩餘工作。
這些測試沒有呼叫外部 TTS 或 GPU，也不表示某個 provider 在 2～4 份工作下已通過壓力驗收。
