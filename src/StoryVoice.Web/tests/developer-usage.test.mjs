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
  assert.match(page, /尚未到期的專案/)
})

test('活動表只呈現安全 metadata，並明確說明不保存敏感內容', () => {
  assert.match(page, /Request ID/)
  assert.match(page, /external API 與可歸屬到目前 owner／project 的 Playground 合成要求/)
  assert.match(page, /不保存輸入文字、完整金鑰、冪等鍵、參考音檔或逐字稿/)
  assert.match(page, /寫入採 best-effort，不能作為計費或 hard quota 的唯一依據/)
  assert.doesNotMatch(page, /activity\.text\b|accessToken|tokenSha256|transcriptText|referenceAudio/)
})

test('用量查詢在送 API 前把 keyId query 正規化成 canonical projectId', () => {
  const normalization = page.indexOf('normalizeDeveloperProjectReference(')
  const usageRequest = page.indexOf('fetchDeveloperVoiceUsage({')

  assert.notEqual(normalization, -1)
  assert.notEqual(usageRequest, -1)
  assert.ok(normalization < usageRequest)
  assert.match(page, /projectId: normalizedProjectId \|\| undefined/)
})

test('未知 project reference 會先顯示 not found，絕不退化成全 owner 用量', () => {
  const unknownGuard = page.indexOf('if (projectReference && !matchingProject)')
  const usageRequest = page.indexOf('fetchDeveloperVoiceUsage({')

  assert.notEqual(unknownGuard, -1)
  assert.notEqual(usageRequest, -1)
  assert.ok(unknownGuard < usageRequest)
  assert.match(page, /if \(projectReference && !matchingProject\) \{[\s\S]*setState\('not-found'\)[\s\S]*return/)
  assert.match(page, /未執行全部專案的用量查詢/)
})

test('project query 切換會立即隱藏舊用量並拒絕 stale response', () => {
  const transitionGuard = page.indexOf('if (routeTransitioning)')
  const readyReport = page.indexOf("{state === 'ready' && overview && report && (")

  assert.match(page, /const routeTransitioning = loadedRequestedProject !== requestedProject/)
  assert.notEqual(transitionGuard, -1)
  assert.ok(transitionGuard < readyReport)
  assert.match(page, /const projectReference = loadedRequestedProjectRef\.current !== requestedProject[\s\S]*\? requestedProject[\s\S]*: projectId/)
  assert.match(page, /controller\.signal\.aborted \|\| requestSequenceRef\.current !== requestSequence/)
  assert.match(page, /setLoadedRequestedProject\(requestedProject\)/)
  assert.match(page, /正在切換 API 專案/)
})

test('到期摘要排除已過期專案，單選過期專案時改用已到期語意', () => {
  assert.match(page, /filter\(\(project\) => project\.status !== 'expired'\)/)
  assert.match(page, /selectedProject\?\.status === 'expired'/)
  assert.match(page, /已於 \$\{formatUtc\(selectedProject\.expiresAtUtc\)\} 到期/)
  assert.match(page, /目前沒有尚未到期的專案/)
})

test('用量載入與錯誤狀態可由輔助科技辨識', () => {
  assert.match(page, /state === 'loading'[\s\S]*role="status"/)
  assert.match(page, /state === 'error'[\s\S]*role="alert"/)
})
