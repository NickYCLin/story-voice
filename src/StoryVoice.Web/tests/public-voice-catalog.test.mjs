import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const page = readFileSync(new URL('../src/pages/PublicVoicesPage.tsx', import.meta.url), 'utf8')
const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8')
const layout = readFileSync(new URL('../src/AppLayout.tsx', import.meta.url), 'utf8')
const authScreen = readFileSync(new URL('../src/AuthScreen.tsx', import.meta.url), 'utf8')
const css = readFileSync(new URL('../src/index.css', import.meta.url), 'utf8')

test('公開聲線館位於登入殼層外，並可從私人導覽開啟', () => {
  const publicRoute = app.indexOf('<Route element={<PublicVoicesPage />} path="/voices" />')
  const privateLayout = app.indexOf('<Route element={<AppLayout />} path="/">')
  assert.notEqual(publicRoute, -1)
  assert.notEqual(privateLayout, -1)
  assert.ok(publicRoute < privateLayout, '公開路由必須獨立於需要登入的 AppLayout')
  assert.match(layout, /to="\/voices">\{t\('公開聲線', 'Voices'\)\}/)
  assert.match(authScreen, /to="\/voices">\{t\('瀏覽公開聲線館', 'Browse public voices'\)\}/)
})

test('目錄匿名讀取公開 API，並將 404 與空目錄安全呈現', () => {
  assert.match(page, /PUBLIC_VOICE_ENDPOINT = '\/api\/public\/v1\/voices'/)
  assert.match(page, /fetch\(apiUrl\(PUBLIC_VOICE_ENDPOINT\), \{\s*cache: 'no-store'/)
  assert.match(page, /credentials: 'omit'/)
  assert.match(page, /response\.status === 404/)
  assert.match(page, /公開聲線館目前尚未啟用/)
  assert.match(page, /目前沒有可公開展示的聲線/)
})

test('固定示範只接受卡片 alias 對應的公開 demo URL', () => {
  assert.match(page, /voice\.canPreview \|\| !voice\.sampleUrl/)
  assert.match(page, /PUBLIC_DEMO_PREFIX.*encodeURIComponent\(voice\.alias\).*\/demo/s)
  assert.match(page, /voice\.sampleUrl === expectedPath/)
  assert.match(page, /preload="none"/)
  assert.match(page, /固定公開示範，不會送出或合成你輸入的文字/)
})

test('公開 DTO 的文字、alias 與標籤界限和後端契約一致', () => {
  assert.ok(page.includes('/^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$/.test(card.alias)'))
  assert.doesNotMatch(page, /\[a-z0-9_-\]/)
  assert.ok(page.includes('isShortText(card.displayName, 120)'))
  assert.ok(page.includes('isOptionalShortText(card.subtitle, 500)'))
  assert.match(page, /value\.length >= 1/)
  assert.match(page, /value\.length <= 8/)
  assert.match(page, /isShortText\(item, 40\)/)
})

test('訂閱 CTA 必須同時通過 view-plans 與 subscriptionAvailable', () => {
  assert.match(page, /voice\.ctaKind === 'view-plans' && voice\.subscriptionAvailable/)
  assert.match(page, /可公開試聽／可申請訂閱/)
  assert.match(page, /查看訂閱與申請說明/)
  assert.match(page, /不會立即結帳或顯示價格/)
  assert.match(page, /目前未連接即時結帳，也未提供方案價格/)
  assert.doesNotMatch(page, /查看訂閱方式|可公開試聽與訂閱/)
  assert.match(page, /尚未開放訂閱/)
  assert.match(page, /完成商用與跨專案授權後才會開放/)
})

test('卡片資料完全來自公開 DTO，不硬編角色姓名或推測屬性', () => {
  assert.match(page, /voice\.displayName/)
  assert.match(page, /voice\.subtitle/)
  assert.match(page, /voice\.disclosure/)
  assert.match(page, /voice\.styles/)
  assert.match(page, /voice\.useCases/)
  assert.doesNotMatch(page, /周子謙|林若晴|褚冥漾/)
})

test('搜尋與條件篩選具可見標籤，播放狀態與錯誤可被輔助科技讀取', () => {
  assert.match(page, /搜尋公開聲線/)
  assert.match(page, /聲線風格/)
  assert.match(page, /核准用途/)
  assert.match(page, /開放狀態/)
  assert.match(page, /aria-pressed=/)
  assert.match(page, /aria-live="polite"/)
  assert.match(page, /role="alert"/)
  assert.match(css, /\.public-focus:focus-visible/)
  assert.match(css, /@media \(prefers-reduced-motion: reduce\)/)
})
