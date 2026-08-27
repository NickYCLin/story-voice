import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const page = readFileSync(new URL('../src/pages/DeveloperConsolePage.tsx', import.meta.url), 'utf8')
const shared = readFileSync(new URL('../src/developerVoiceConsole.ts', import.meta.url), 'utf8')
const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8')
const layout = readFileSync(new URL('../src/AppLayout.tsx', import.meta.url), 'utf8')
const implementation = `${page}\n${shared}`

test('開發者總覽位於需要登入的 AppLayout 內，並可從主導覽開啟', () => {
  const consoleRoute = app.indexOf('<Route element={<DeveloperConsolePage />} path="developer" />')
  const privateLayout = app.indexOf('<Route element={<AppLayout />} path="/">')
  assert.notEqual(consoleRoute, -1)
  assert.notEqual(privateLayout, -1)
  assert.ok(consoleRoute > privateLayout, '開發者總覽必須是登入殼層內的路由')
  assert.match(layout, /to="\/developer">開發者/)
})

test('總覽本身維持唯讀，並導向獨立的 owner-scoped API 金鑰頁', () => {
  assert.match(shared, /\/api\/developer\/external-voice\/overview/)
  assert.doesNotMatch(page, /method:\s*'(POST|PUT|DELETE|PATCH)'/)
  assert.doesNotMatch(page, /csrfToken/)
  assert.match(page, /to="\/developer\/credentials">API 金鑰/)
})

test('明確告知完整 secret 只顯示一次，受管金鑰可自行換發與撤銷', () => {
  assert.match(page, /完整 secret 只在操作完成後顯示一次/)
  assert.match(page, /建立、換發或撤銷/)
})

test('呈現專案效期狀態與聲線授權狀態', () => {
  assert.match(implementation, /尚未生效/)
  assert.match(implementation, /即將到期/)
  assert.match(implementation, /已到期/)
  assert.match(implementation, /已撤銷/)
  assert.match(implementation, /可使用/)
})

test('涵蓋服務未啟用與沒有專案的空狀態', () => {
  assert.match(page, /目前未啟用/)
  assert.match(page, /目前沒有核發給你的 API 專案/)
  assert.match(page, /to="\/developers\/docs"/)
})

test('不顯示任何雜湊、內部路徑或 owner GUID 欄位', () => {
  assert.doesNotMatch(page, /Sha256|tokenSha|AuthorizationEvidence|ownerId|AssetRootPath/i)
})

test('總覽顯示真實的最近 24 小時成功、失敗、429 與耗時', () => {
  assert.match(page, /fetchDeveloperVoiceUsage/)
  assert.match(page, /24 \* 60 \* 60 \* 1000/)
  assert.match(page, /totalRequests - usage\.summary\.successfulRequests/)
  assert.match(page, /\['成功'/)
  assert.match(page, /\['失敗'/)
  assert.match(page, /\['429 次數'/)
  assert.match(page, /\['平均耗時'/)
})

test('最近用量讀取失敗時不會把專案總覽一起切成錯誤狀態', () => {
  const usageRequest = page.slice(page.indexOf('fetchDeveloperVoiceUsage({'), page.indexOf('return () => controller.abort()'))
  assert.match(usageRequest, /setUsageState\('error'\)/)
  assert.doesNotMatch(usageRequest, /setState\('error'\)/)
  assert.match(page, /最近用量暫時無法讀取，不影響下方專案與金鑰操作/)
})
