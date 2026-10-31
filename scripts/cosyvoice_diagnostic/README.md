# 離線 CosyVoice 口音診斷

這是操作者手動使用的 CLI，不是正式 StoryVoice 配音功能。不建立 listener、HTTP route、
憑證或新的 Redis 權限，不修改 Compose、正式 service 或 casting。預設只驗證私人輸入，
不連 Redis、不載入模型、不生成音訊。本工具不會認定某份錄音具有台灣口音。

## 已準備的對照

| 模式 | 呼叫 | Prompt |
| --- | --- | --- |
| A | `inference_zero_shot` | 標準 helpful-assistant 前綴、`<\|endofprompt\|>`、精確逐字稿 |
| B | `inference_instruct2` | 同一標準前綴，沒有逐字稿 |
| C | `inference_instruct2` | 同 B，只增加「請用自然的台灣國語說話。」 |

C 是探索性 instruction，沒有官方台灣國語支援保證。閩南語不等於台灣國語。
B/C 隔離 instruction 的影響；A/B 改變 conditioning 模式，不能稱為只改 prompt。
固定版本的 instruct2 會移除 LLM 參考語音 tokens，但保留 flow 參考特徵、tokens 和
speaker embeddings。三個模式都用空 `zero_shot_spk_id`，不跨模式共用 speaker cache。

每個模式先呼叫同一 frontend 取得實際目標文字分段，再停用推論內的第二次正規化。
A 的參考 prompt 也先經過原有 frontend；其特殊標記會觸發既有跳過正規化規則。
每個模式推論前重設 Python、NumPy、torch、CUDA seed，同一模式的分段持續同一 RNG 序列。
固定 seed 不保證 GPU 計算逐位元一致，也不保證口音。沒有任意移除 zh/ch/sh 或變調處理。

## 輸入及預設驗證

先由使用者聽辨確認一份具有權利依據的參考錄音：自然台灣國語、單人、字頭字尾完整，
並人工核對逐字稿。聲學檢查、人物描述或 ASR 結果不能代替這些確認。
參考檔限制為單聲道 PCM16 WAV、48 kHz、10–30 秒、最多 10 MiB。

在 repo 外的私人目錄建立 `request.json`；參考檔路徑相對於此 JSON。
下列 false 是刻意保留的阻擋值，不能未確認就改成 true：

```json
{
  "reference_path": "reference.wav",
  "reference_sha256": "填入原始 WAV 的 SHA256",
  "transcript": "填入與錄音逐字相符的文字",
  "transcript_sha256": "填入上述逐字稿 UTF-8 位元組的 SHA256，不含額外換行",
  "text": "填入三個模式共同使用的短句",
  "seed": 1986,
  "evidence_id": "填入既有授權或同意紀錄的識別碼，不填密鑰",
  "private_evaluation_allowed": false,
  "taiwan_mandarin_confirmed": false,
  "single_speaker_confirmed": false,
  "full_words_confirmed": false,
  "transcript_verified": false
}
```

```console
python3 scripts/cosyvoice_diagnostic/diagnostic.py /private/request.json
```

此命令只驗證輸入格式、確認欄位、雜湊與 WAV 結構；不驗證人的聲學判斷是否正確。
輸入不得自行夾帶 `<|...|>` 特殊 prompt 標記，首尾空白亦須先明確處理。

## 實際執行的前提與限制

本次只交付程式及 mock 測試，未執行任何真實 A/B/C。

執行 adapter 必須位於**原有且已授權**的 FaceSpeak runtime 環境，使用原有 Redis
環境設定與現成的 `gpu_executor.py`、`gpu_lock.py`、`gpu_settings.py`。
不提供 Redis URL／token 命令列參數，不匯入 `app.py`，不啟動伺服器。
`pins.json` 固定已查核的 executor 檔、四個關鍵 CosyVoice 原始碼檔及 12 個模型檔。
版本不同就停止，不能只更新雜湊來略過整合審查。

1. 操作者先依 sole-operator 協調確認沒有排程／生成工作。
2. 工具使用原有 Redis lease、續租、watchdog 與不確定狀態的 exit-70 隔離策略，
   僅等待 0.05 秒取得鎖，忙碌即停止，不排隊等待搶占。
3. 取得鎖後，以 `nvidia-smi` 確認沒有任何常駐 compute process。看不見程序亦停止。
   **即使正式服務沒有正在生成，只要仍常駐 GPU 模型，CLI 就拒絕載入另一份。**
4. 模型載入、三模式生成與 CUDA 清理全部在同一 lease 內；清理不確定就退出且不提前解鎖。

因此目前正式服務常駐模型可能阻擋真實試跑。工具不會停止它、改服務權限或建立另一個
GPU 服務。需要已協調且獨立允許的空閒診斷環境／維護時段；本次授權不包含停服務。
如果沒有此條件，就先停在輸入驗證和 mock 測試，不把 CLI 宣稱成已完成實機整合。

待這些條件及參考錄音確認後，操作者才可在上述 runtime 中手動 opt in：

```console
python3 -B /path/to/diagnostic.py /private/request.json --execute --output /private/new-run
```

`new-run` 必須不存在，父目錄須已存在；結果目錄為 0700，檔案為 0600，拒絕覆寫。
來源 WAV 先讀成快照、驗證雜湊，再放入私人臨時目錄供三個模式共用，防止中途換檔。
不下載模型、不連外部語音服務。CLI 只連既有 Redis 鎖，音訊推論在本機執行。

## 證據與失敗處理

- `started.json`：固定版本／雜湊、seed、既有授權識別碼。不是成功證明。
- `A.input.json` 等：推論前保存實際輸入，失敗時也能追查是哪一模式。
- `A.wav`／`B.wav`／`C.wav` 及各自 JSON：模式、實際正規化文字、實際 prompt、frontend
  類型、reference/transcript SHA256、seed、參數、輸出雜湊。
- `completed.json`：僅三個模式都成功後才建立。

JSON 可能包含私人逐字稿，應和音訊一樣保留在私人目錄，不提交 Git、公開或自動上傳。
原生 prompt 日誌停用，終端錯誤只顯示 exception 類型，不顯示可能含密鑰的 exception 文字。
失敗可能留下部分樣本；退出 70 可能留下臨時 reference 與待到期 lease。先核對程序與鎖，
不要自動重試、清除他人的鎖或把部分結果視為完成。工具不會刪除其他任務資料。

聽辨時分開評估捲舌、整句語調與年齡感，使用者接受前不得套用正式書籍。

## 測試

```console
python3 -m unittest discover -s tests/python -p test_cosyvoice_diagnostic.py -v
```

只使用合成靜音 WAV 和 mock；不載入模型、呼叫 Redis 或使用 GPU。
此測試已由既有 CI 的 `tests/python` discovery 納入，不需要改 CI／部署設定。
