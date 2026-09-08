# 公開聲線詳情

`GET /api/public/v1/voices/{alias}` 與 `/voices/:alias` 提供固定示範與目前授權摘要。目錄卡片的聲線名稱可開啟詳細頁；無須登入。

## 回應契約

- `voice`：沿用 `PublicVoiceCatalogCard`，只包含 alias、displayName、subtitle、disclosure、styles、useCases、sampleUrl、canPreview、ctaKind、subscriptionAvailable、status。
- `license`：commercialUseAllowed、publicDistributionAllowed、crossProjectApiAllowed、effectiveAtUtc、expiresAtUtc、territoryMode、territoryCountryCodes。
- 三個用途欄位只會在現有驗證器確認該用途已獲准時回傳 true。這份聲線摘要不會授予呼叫者 entitlement，合成仍須通過獨立的專案與金鑰驗證。
- 時間在 JSON 中保留 UTC，詳細頁明確標示台北時間；地區來自授權中的 worldwide 或 country-list。

不公開 owner／profile ID、consumer family、project、帳號、稽核事件、撤銷聯絡人、逐字稿、素材路徑、雜湊或完整授權文件。API 的成功與失敗回應均使用 `Cache-Control: no-store`、`X-Content-Type-Options: nosniff`。

## 失效與播放

端點與 list／demo 共用 `VoiceCatalog.Enabled` 開關。關閉時不註冊 API；啟用後，未知或非標準 alias 回 404。每次 GET 都重新驗證授權雜湊、期限、撤銷狀態、生成紀錄、條款快照、參考音訊、逐字稿與固定示範，沒有保留已通過結果的快取。

詳細頁只使用 alias 對應的固定 demo URL，音訊 `preload="none"`，須使用者按播放才讀取。離開頁面或重新驗證時停止播放。資料請求逾時 10 秒後可手動重試；切換 alias 會中止舊請求，晚到的回應不會覆蓋新聲線。重新聚焦及公布效期到達時會重新讀取；這些前端行為不取代後端授權檢查，也不承諾即時推播撤銷。

## 驗證與啟用狀態

- 後端測試使用合成測試素材，驗證公開欄位白名單、無 GPU 呼叫、disabled／未知 alias、14 種授權或素材失效，以及成功讀取後修改示範檔即回 404。
- 前端測試涵蓋匿名 no-store 讀取、錯誤重試、alias 不符、外部 demo URL、切頁晚到回應、重新聚焦、讀取逾時與到期。
- 本機通過 35 項相關後端整合測試、164 項前端靜態檢查、62 項前端執行測試、lint，以及根路徑與 `/StoryVoice/` 建置。
- Chrome 以 `/StoryVoice/` 正式建置與合成資料驗證 1440／390／320 px 排版、實際固定 WAV 播放／暫停、卡片開啟詳情、返回訂閱說明的定位、下架與重試。沒有呼叫真實合成 API。

本次不建立公開 entry、不安裝正式示範、不啟用公開開關，也未部署正式環境。有效授權資料、公開發佈／撤銷流程與正式啟用仍為未完成項目。
