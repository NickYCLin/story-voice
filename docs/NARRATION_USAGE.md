# 配音用量與耗時紀錄

書籍的配音工作與系列批次都可展開「查看配音用量」。每次 Worker 取得工作並開始處理，
會建立一筆獨立紀錄；重試沿用同一個工作 ID，但不覆寫前一次紀錄。

目前保存實際觀察到的處理量與耗時，沒有單價、幣別或金額估算。這些資料不是供應商的
帳單，也不能作為唯一的計費或硬額度依據。套用前需先執行 `AddNarrationAttemptUsage`
migration；較早的工作不會回填猜測值。

## 數字的定義

| 欄位 | 記錄內容 |
|---|---|
| `inputCharacters` | 交給該次配音流程的文字量，按 Unicode scalar value 計數，包含標題、標點、空白與換行。多角色依最終 turn 文字加總，不保存文字本身。快取命中仍包含在這個數字中，不能視為送到遠端供應商的計費字數 |
| `completedChunks`／`totalChunks` | 該次流程最後一次有效進度回報；可能包含快取，不代表 HTTP 呼叫次數。沒有回報就維持 null，不能假設為 0 或全部完成 |
| `elapsedMs` | 從開始記錄到結束的 Worker 處理時間，包含準備、資料存取、合成與成品處理；不含排隊時間及最後一次用量寫入 |
| `synthesisElapsedMs` | 配音流程的 wall-clock 時間，包含快取、合成及音訊組合，並非純模型推論時間 |
| `audioBytes` | 確認寫入 Completed 工作的成品 bytes。失敗、中斷或未確認的成品不填入 |
| `provider` | 固定引擎識別值。尚未準備好輸入就失敗時為 null，不保存網址或聲線資料 |

結果區分 `Completed`、`Failed`、`TimedOut`、`Cancelled`、`WorkerStopped`、`LeaseLost`
與 `Unknown`。尚未結束且仍持有有效租約的紀錄顯示 `Running`；程序崩潰、租約失效或
結果寫入失敗而沒有結束紀錄時，讀取端顯示 `Unknown`，不補造結束時間、耗時或成功結果。
失敗和取消仍可能產生費用，不能因為沒有成品就推斷免費。

## 儲存與讀取

- `narration_attempt_usage` 以一次 claim 的隨機 ID 為主鍵，並以 owner／job 複合外鍵限制歸屬；刪除工作時一起刪除紀錄。
- 開始、準備完成與結束分別寫入。每次寫入最多等待 5 秒；用量儲存失敗只記錄固定警告與例外類型，不能觸發重新合成或影響已完成成品。這也表示服務中斷或資料庫故障可能留下缺漏，不能宣稱完整計費帳本。
- 片段進度只在正常結束處理時保存；崩潰前來不及保存的進度維持未知。程序內 BlueMagpie 即時 metrics 仍由 [BLUEMAGPIE_OBSERVABILITY.md](BLUEMAGPIE_OBSERVABILITY.md) 說明。
- `GET /api/narrations/{jobId}/usage` 要求登入，只供工作擁有者讀取，包含其 staged／historical 工作；封存書籍與其他帳號回傳 404。此路由不授予音訊存取權。
- 回應使用 `private, no-store`；`totalAttempts` 是已保存的筆數，`attempts` 最多回傳最近 100 筆，不公開 lease owner。舊工作沒有紀錄時回傳空陣列。
- 畫面按需載入，切換工作或關閉區塊會取消讀取，逾時或失敗可重新整理用量。顯示 null 時使用「未記錄」，不換成零。

單價版本、幣別、供應商對帳、實際 billable units、retention／archive 與金額估算仍待後續
費率和營運規則。這次沒有付費呼叫、正式資料回填或部署。
