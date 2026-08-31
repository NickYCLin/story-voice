import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const page = readFileSync(new URL('../src/pages/DeveloperCredentialsPage.tsx', import.meta.url), 'utf8')
const shared = readFileSync(new URL('../src/developerVoiceConsole.ts', import.meta.url), 'utf8')
const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8')
const project = readFileSync(new URL('../src/pages/DeveloperProjectPage.tsx', import.meta.url), 'utf8')
const projectReference = readFileSync(new URL('../src/developerProjectReference.ts', import.meta.url), 'utf8')

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

test('keyId query 會先解析成 select 與 API 共用的 canonical projectId', () => {
  assert.match(page, /findDeveloperProjectByReference\(nextOverview\.projects, requestedProject\)/)
  assert.match(page, /requested && requested\.status !== 'expired' \? requested : firstAvailable/)
  assert.match(page, /canonicalDeveloperProjectReference\(selected\)/)
  assert.match(projectReference, /project\.projectId === reference \|\| project\.keyId === reference/)
  assert.match(projectReference, /project\.projectId \|\| project\.keyId/)
})

test('一次性 secret 未關閉前會阻擋另一個建立或換發操作', () => {
  assert.match(page, /if \(issued\) \{[\s\S]*請先保存並關閉目前的一次性金鑰/)
  assert.match(page, /disabled=\{routeTransitioning \|\| busy \|\| Boolean\(issued\) \|\| Boolean\(pendingAction\) \|\| !overview\.serviceEnabled \|\| !selectedProject \|\| selectedProject\.status === 'expired'\}/)
  assert.match(page, /disabled=\{routeTransitioning \|\| busy \|\| Boolean\(issued\) \|\| Boolean\(pendingAction\) \|\| !overview\.serviceEnabled\}[\s\S]*t\('換發', 'Rotate'\)/)
  assert.match(page, /建立與換發功能會暫停/)
})

test('mutation 成功後 refresh 失敗會保留成功語意並提示畫面可能過期', () => {
  assert.match(page, /async function refreshAfterMutation\(successMessage: string\)/)
  assert.match(page, /await refresh\(\)[\s\S]*catch \{[\s\S]*\$\{successMessage\}[\s\S]*但金鑰清單與異動紀錄重新整理失敗/)
  assert.match(page, /await refreshAfterMutation\(t\('金鑰已撤銷。', 'Key revoked.'\)\)/)
})

test('金鑰頁停用服務時阻擋建立與換發但保留撤銷，並顯示預定撤銷時間', () => {
  assert.equal(page.match(/if \(!overview\?\.serviceEnabled\)/g)?.length, 2)
  assert.match(page, /語音 API 目前未啟用，暫時無法建立或換發金鑰；現有受管金鑰仍可撤銷/)
  assert.match(page, /disabled=\{routeTransitioning \|\| busy \|\| Boolean\(issued\) \|\| Boolean\(pendingAction\) \|\| !overview\.serviceEnabled\}[\s\S]*t\('換發', 'Rotate'\)/)
  assert.match(page, /disabled=\{routeTransitioning \|\| busy \|\| Boolean\(pendingAction\)\}[\s\S]*t\('立即撤銷', 'Revoke now'\)/)
  assert.match(page, /credential\.status === 'revocation-scheduled'[\s\S]*t\('預定撤銷', 'Scheduled revocation'\)[\s\S]*t\('撤銷時間', 'Revoked at'\)/)
  assert.match(page, /formatUtc\(credential\.revokedAtUtc, locale\)/)
  assert.match(page, /<ConfirmDialog/)
  assert.doesNotMatch(page, /window\.confirm/)
})

test('到期專案 query 不會被選來建立金鑰，送出前也會再次驗證專案狀態', () => {
  assert.match(page, /requested && requested\.status !== 'expired' \? requested : firstAvailable/)
  assert.match(page, /issuableProjects = overview\?\.projects\.filter\(\(project\) => project\.status !== 'expired'\)/)
  assert.match(page, /issuableProjects\.length === 0/)
  assert.match(page, /issuableProjects\.map\(\(project\)/)
  assert.match(page, /if \(!selectedProject \|\| selectedProject\.status === 'expired'\) \{[\s\S]*請選擇尚未到期的 API 專案/)
  assert.match(page, /!selectedProject \|\| selectedProject\.status === 'expired'/)
})

test('複製權限失敗時提供手動保存與下載退路', () => {
  assert.match(page, /navigator\.clipboard\.writeText\(issued\.accessToken\)/)
  assert.match(page, /catch \{[\s\S]*請手動選取上方完整金鑰，或下載 \.env/)
})

test('project query 切換會立即進入 loading 並阻擋舊專案 mutation', () => {
  assert.match(page, /const routeTransitioning = loadedRequestedProject !== requestedProject/)
  assert.match(page, /useEffect\(\(\) => \{[\s\S]*setState\('loading'\)[\s\S]*setPendingAction\(null\)/)
  assert.match(page, /if \(routeTransitioning \|\| state !== 'ready'\) \{[\s\S]*正在切換 API 專案，請等資料更新完成後再操作/)
  assert.equal(page.match(/if \(routeTransitioning \|\| state !== 'ready'\)/g)?.length, 3)
  assert.match(page, /if \(routeTransitioning \|\| state === 'loading'\)/)
  assert.match(page, /disabled=\{routeTransitioning \|\| busy/)
})

test('project query 切換或載入失敗時仍保留正在顯示的一次性 secret', () => {
  const transitionStart = page.indexOf("if (routeTransitioning || state === 'loading')")
  const errorStart = page.indexOf("if (state === 'error' || !overview)")
  const readyStart = page.indexOf('\n  return (', errorStart + 1)
  const routeEffectEnd = page.indexOf('async function refresh()', page.indexOf('useEffect(() =>'))

  assert.notEqual(transitionStart, -1)
  assert.ok(errorStart > transitionStart)
  assert.ok(readyStart > errorStart)
  assert.match(page.slice(transitionStart, errorStart), /\{issuedCredentialPanel\}/)
  assert.match(page.slice(errorStart, readyStart), /\{issuedCredentialPanel\}/)
  assert.match(page, /const issuedCredentialPanel = issued &&/)
  assert.doesNotMatch(page.slice(0, routeEffectEnd), /setIssued\(null\)/)
})

test('金鑰生命週期與一次性 secret 警告提供完整英文介面', () => {
  assert.match(page, /const \{ locale \} = useLocale\(\)/)
  assert.match(shared, /CREDENTIAL_STATUS_LABEL_EN/)
  assert.match(page, /credentialStatusLabel/)
  assert.match(page, /Each complete key is shown only once/)
  assert.match(page, /immediately\? This action cannot be undone/)
  assert.match(page, /Key activity log/)
  assert.match(page, /Production backend/)
})
