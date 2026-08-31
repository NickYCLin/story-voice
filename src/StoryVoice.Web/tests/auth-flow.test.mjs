import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const authSource = readFileSync(new URL('../src/auth.ts', import.meta.url), 'utf8')
const authScreenSource = readFileSync(new URL('../src/AuthScreen.tsx', import.meta.url), 'utf8')
const appLayoutSource = readFileSync(new URL('../src/AppLayout.tsx', import.meta.url), 'utf8')
const appSource = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8')
const apiSource = readFileSync(new URL('../src/api.ts', import.meta.url), 'utf8')
const libraryPageSource = readFileSync(new URL('../src/pages/LibraryPage.tsx', import.meta.url), 'utf8')

test('StoryVoice 先建立自己的帳號工作階段，再顯示個人書庫', () => {
  assert.match(authSource, /\/api\/auth\/session/)
  assert.match(authScreenSource, /\/api\/auth\/register/)
  assert.match(authScreenSource, /\/api\/auth\/login/)
  assert.match(authScreenSource, /登入 StoryVoice/)
  assert.match(authScreenSource, /建立 StoryVoice 帳號/)
  assert.match(authSource, /authenticated/)
})

test('登入狀態載入與錯誤可由輔助科技辨識', () => {
  assert.match(appLayoutSource, /authState\.status === 'loading'[\s\S]*role="status"/)
  assert.match(appLayoutSource, /authState\.status === 'error'[\s\S]*role="alert"/)
})

test('所有 Cookie 寫入都帶 CSRF，登出會整個卸載私人書庫殼層', () => {
  assert.match(authScreenSource, /X-CSRF-TOKEN/)
  assert.match(authSource, /\/api\/auth\/logout/)
  assert.match(apiSource, /credentials:\s*'same-origin'/)
  // AppLayout 在 anonymous 狀態下用提早 return 整段換成 AuthScreen，而不是逐一清空狀態，
  // 讓登出後不會有任何一頁殘留前一個帳號的私人資料：<Outlet> 一定在這個提早 return 之後才會出現。
  const anonymousGuard = appLayoutSource.indexOf("authState.status === 'anonymous'")
  const outletRender = appLayoutSource.indexOf('<Outlet')
  assert.notEqual(anonymousGuard, -1)
  assert.notEqual(outletRender, -1)
  assert.ok(anonymousGuard < outletRender, 'Outlet 必須在 anonymous 提早 return 之後才會渲染')
  assert.match(appLayoutSource, /<AuthScreen /)
})

test('登入後引導使用者自行匯入有權處理的檔案', () => {
  assert.match(authScreenSource, /準備無 DRM 的 EPUB 或 UTF-8 TXT/)
  assert.match(authScreenSource, /匯入檔案並檢查解析後的章節/)
  assert.match(libraryPageSource, /書籍來自哪個平台都沒關係/)
  assert.doesNotMatch(libraryPageSource, /companion-token|Companion|博客來/)
})

test('匿名登入畫面可切換語言，並導向公開雙語介紹', () => {
  assert.match(authScreenSource, /<LanguageSwitcher/)
  assert.match(authScreenSource, /to="\/about"/)
  assert.match(authScreenSource, /Learn about StoryVoice/)
  assert.match(appSource, /<LandingPage publicMode \/>.*path="\/about"/)
})
