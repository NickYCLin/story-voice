# 版面穩定性人工驗證

啟動 `npm run dev -- --host 127.0.0.1`，開啟 `/tests/layout/index.html#/library`。
這個開發入口直接使用正式 React 元件，但攔截所有 fetch：只有合成書籍、角色及空白資料，不讀正式服務，所有寫入都回傳 405。標準 Vite build 不包含此入口。

- `?delay=1200#/library`：延遲載入、26 本書與切書。
- `#/characters`：100 名角色、長清單、選角和頁籤。
- `?scenario=empty#/library`、`?scenario=error#/library`：空白／API 失敗。
- `?scenario=anonymous#/library`：登入／註冊排版，不送出表單。
- `#/voices`、`?scenario=error#/voices`：公開聲線空白／錯誤頁。
- `?player=1&delay=100#/library`：展開右下驗證工具，以本機生成的低音量合成音調檢查播放、暫停、時間軸出現、確認視窗與焦點。

分別測試 390×844、768×1024、1440×1000，並在 639/640、899/900 px 邊界檢查溢出。開啟右下工具，按「重設量測」後操作；JSON 顯示區域頂端／高度極值及 LayoutShift 累計。只有非預期移動才計入 CLS；操作、捲動與跨斷點的移動應另外比較，不能直接當作 CLS。

正式 API、資料庫、聲線映射和部署都不包含在這套驗證中。
