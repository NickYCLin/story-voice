import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const page = readFileSync(new URL('../src/pages/DeveloperCredentialsPage.tsx', import.meta.url), 'utf8')
const shared = readFileSync(new URL('../src/developerVoiceConsole.ts', import.meta.url), 'utf8')
const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8')
const project = readFileSync(new URL('../src/pages/DeveloperProjectPage.tsx', import.meta.url), 'utf8')

test('API 金鑰頁位於登入殼層，專案詳情可帶入 owner-scoped project', () => {
  const route = app.indexOf('<Route element={<DeveloperCredentialsPage />} path="developer/credentials" />')
  const privateLayout = app.indexOf('<Route element={<AppLayout />} path="/">')
  assert.notEqual(route, -1)
  assert.ok(route > privateLayout)
  assert.match(project, /\/developer\/credentials\?project=\$\{encodeURIComponent\(project\.projectId \|\| project\.keyId\)\}/)
})

test('所有 mutation 都走 same-origin JSON helper 與 CSRF，不把 external bearer 放進請求', () => {
  assert.match(shared, /\/api\/developer\/external-voice\/credentials/)
  assert.match(shared, /csrfToken/)
  assert.match(shared, /method: 'POST'/)
  assert.doesNotMatch(shared, /Authorization|Bearer|localStorage|sessionStorage/)
  assert.doesNotMatch(page, /Authorization|Bearer|localStorage|sessionStorage/)
})

test('完整金鑰只存在一次性畫面，可複製或下載且會釋放 object URL', () => {
  assert.match(page, /只顯示這一次/)
  assert.match(page, /navigator\.clipboard\.writeText\(issued\.accessToken\)/)
  assert.match(page, /STORYVOICE_VOICE_TOKEN=\$\{issued\.accessToken\}/)
  assert.match(page, /URL\.revokeObjectURL\(objectUrl\)/)
  assert.match(page, /setIssued\(null\)/)
})

test('頁面涵蓋建立、換發重疊時間、撤銷、last-used 與 durable audit', () => {
  assert.match(page, /建立金鑰/)
  assert.match(page, /保留 1 小時/)
  assert.match(page, /保留 24 小時/)
  assert.match(page, /立即撤銷/)
  assert.match(page, /最近使用/)
  assert.match(page, /Durable audit/)
  assert.match(page, /金鑰異動紀錄/)
})
