import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const page = readFileSync(new URL('../src/pages/DeveloperUsagePage.tsx', import.meta.url), 'utf8')
const shared = readFileSync(new URL('../src/developerVoiceConsole.ts', import.meta.url), 'utf8')
const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8')
const consolePage = readFileSync(new URL('../src/pages/DeveloperConsolePage.tsx', import.meta.url), 'utf8')
const projectPage = readFileSync(new URL('../src/pages/DeveloperProjectPage.tsx', import.meta.url), 'utf8')

test('用量頁位於登入殼層，總覽與專案詳情都有入口', () => {
  const route = app.indexOf('<Route element={<DeveloperUsagePage />} path="developer/usage" />')
  const privateLayout = app.indexOf('<Route element={<AppLayout />} path="/">')
  assert.notEqual(route, -1)
  assert.ok(route > privateLayout)
  assert.match(consolePage, /to="\/developer\/usage">用量與活動/)
  assert.match(projectPage, /\/developer\/usage\?project=/)
})

test('查詢只使用 owner session，不在瀏覽器保存或傳送 external bearer', () => {
  assert.match(shared, /\/api\/developer\/external-voice\/usage/)
  assert.match(shared, /URLSearchParams/)
  assert.doesNotMatch(page, /Authorization|Bearer|localStorage|sessionStorage|Idempotency-Key/)
  assert.doesNotMatch(shared, /Authorization|Bearer|localStorage|sessionStorage/)
})

test('用量摘要涵蓋成功率、429、latency、產出時間與大小', () => {
  assert.match(page, /成功率/)
  assert.match(page, /429 次數/)
  assert.match(page, /平均耗時/)
  assert.match(page, /產出長度/)
  assert.match(page, /產出大小/)
  assert.match(page, /每分鐘上限/)
  assert.match(page, /最近到期時間/)
})

test('活動表只呈現安全 metadata，並明確說明不保存敏感內容', () => {
  assert.match(page, /Request ID/)
  assert.match(page, /不保存輸入文字、完整金鑰、冪等鍵、參考音檔或逐字稿/)
  assert.doesNotMatch(page, /activity\.text\b|accessToken|tokenSha256|transcriptText|referenceAudio/)
})
