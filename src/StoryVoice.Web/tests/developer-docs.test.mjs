import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const page = readFileSync(new URL('../src/pages/DeveloperDocsPage.tsx', import.meta.url), 'utf8')
const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8')
const layout = readFileSync(new URL('../src/AppLayout.tsx', import.meta.url), 'utf8')
const publicVoicesPage = readFileSync(new URL('../src/pages/PublicVoicesPage.tsx', import.meta.url), 'utf8')

test('API 文件位於登入殼層外，並可從公開聲線館與私人導覽開啟', () => {
  const docsRoute = app.indexOf('<Route element={<DeveloperDocsPage />} path="/developers/docs" />')
  const privateLayout = app.indexOf('<Route element={<AppLayout />} path="/">')
  assert.notEqual(docsRoute, -1)
  assert.notEqual(privateLayout, -1)
  assert.ok(docsRoute < privateLayout, 'API 文件路由必須獨立於需要登入的 AppLayout')
  assert.match(layout, /to="\/developers\/docs">\{t\('API 文件', 'API docs'\)\}/)
  assert.match(publicVoicesPage, /to="\/developers\/docs">/)
})

test('頁面不是即時互動的 API 呼叫，只呈現靜態文件內容', () => {
  assert.doesNotMatch(page, /useEffect|useState/)
})

test('清楚區分 private-development 與 subscription-commercial 兩種存取層級', () => {
  assert.match(page, /private-development/)
  assert.match(page, /subscription-commercial/)
  assert.match(page, /svd1\./)
  assert.match(page, /svv1\./)
  assert.match(page, /互不相通/)
})

test('Request／Response 契約與後端固定的 HTTP 合約一致', () => {
  assert.match(page, /POST \/api\/external\/v1\/speech/)
  assert.match(page, /Idempotency-Key/)
  assert.match(page, /16-64 letters, digits, underscores, or hyphens/)
  assert.match(page, /"voice": "<authorized-voice-alias>"/)
  assert.match(page, /audio\/wav/)
  assert.match(page, /invalid_request/)
  assert.match(page, /invalid_api_key/)
  assert.match(page, /voice_not_available/)
  assert.match(page, /idempotency_conflict/)
  assert.match(page, /rate_limited/)
  assert.match(page, /synthesis_unavailable/)
  assert.match(page, /協定允許的文字硬上限/)
  assert.match(page, /External speech POST 會先通過來源／全域防濫用額度/)
  assert.match(page, /Playground 與 external API 會共同消耗同一 consumer 的每分鐘額度/)
})

test('所有範例都標示伺服器端執行，並警告不要把 token 放進瀏覽器', () => {
  assert.match(page, /伺服器端執行/)
  assert.match(page, /絕對不要放進瀏覽器程式碼、URL 或前端 log/)
  assert.match(page, /STORYVOICE_VOICE_TOKEN/)
})

test('說明專案仍由團隊核發，但受管金鑰已有自助生命週期', () => {
  assert.match(page, /專案與 consumer entitlement 仍由 StoryVoice 團隊核發/)
  assert.match(page, /自行建立、輪替或撤銷受管金鑰/)
  assert.match(page, /Playground/)
  assert.match(page, /usage/)
})

test('不洩漏內部路徑、SHA、reference、transcript 或 owner GUID', () => {
  assert.doesNotMatch(page, /Sha256|ReferenceAudio|transcript|OwnerId|AssetRootPath/i)
})
