import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const page = readFileSync(new URL('../src/pages/DeveloperProjectPage.tsx', import.meta.url), 'utf8')
const overview = readFileSync(new URL('../src/pages/DeveloperConsolePage.tsx', import.meta.url), 'utf8')
const shared = readFileSync(new URL('../src/developerVoiceConsole.ts', import.meta.url), 'utf8')
const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8')

test('專案詳情位於登入殼層，並由開發者總覽的 owner-scoped 專案卡進入', () => {
  const detailRoute = app.indexOf('<Route element={<DeveloperProjectPage />} path="developer/projects/:projectId" />')
  const privateLayout = app.indexOf('<Route element={<AppLayout />} path="/">')
  assert.notEqual(detailRoute, -1)
  assert.ok(detailRoute > privateLayout)
  assert.match(overview, /\/developer\/projects\/\$\{encodeURIComponent\(project\.projectId \|\| project\.keyId\)\}/)
})

test('詳情沿用唯讀 overview，沒有 credential mutation 或 external bearer', () => {
  assert.match(shared, /\/api\/developer\/external-voice\/overview/)
  assert.doesNotMatch(page, /method:\s*'(POST|PUT|DELETE|PATCH)'/)
  assert.doesNotMatch(page, /csrfToken|Authorization|Bearer/)
  assert.match(page, /完整 secret 不會在頁面重新顯示/)
})

test('使用 owner-scoped payload 尋找 projectId 或 keyId，找不到時不洩漏存在性', () => {
  assert.match(page, /candidate\.projectId === projectId \|\| candidate\.keyId === projectId/)
  assert.match(page, /專案不存在，或目前登入的帳號沒有檢視權限/)
})

test('呈現 entitlement、限制、credential 摘要與必要降級狀態', () => {
  assert.match(page, /已授權聲線/)
  assert.match(page, /撤銷狀態會在每次正式 API 呼叫前重新驗證/)
  assert.match(page, /尚未提供用量資料/)
  assert.match(page, /合成聲線 API 服務目前未啟用/)
  assert.match(page, /查看 API 文件/)
})

test('不顯示 token 雜湊、evidence、內部資產路徑或 owner GUID', () => {
  assert.doesNotMatch(page, /TokenSha256|AuthorizationEvidence|AssetRootPath|ownerId/i)
})
