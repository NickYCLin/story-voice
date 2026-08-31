import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const i18n = readFileSync(new URL('../src/i18n.tsx', import.meta.url), 'utf8')
const switcher = readFileSync(new URL('../src/components/LanguageSwitcher.tsx', import.meta.url), 'utf8')
const main = readFileSync(new URL('../src/main.tsx', import.meta.url), 'utf8')
const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8')
const layout = readFileSync(new URL('../src/AppLayout.tsx', import.meta.url), 'utf8')
const auth = readFileSync(new URL('../src/AuthScreen.tsx', import.meta.url), 'utf8')
const landing = readFileSync(new URL('../src/pages/LandingPage.tsx', import.meta.url), 'utf8')

test('語言選擇依 URL、已儲存偏好、瀏覽器語言的順序決定', () => {
  assert.match(i18n, /SupportedLocale = 'zh-TW' \| 'en'/)
  const urlChoice = i18n.indexOf("new URLSearchParams(window.location.search).get('lang')")
  const storedChoice = i18n.indexOf("localStorage.getItem(LOCALE_STORAGE_KEY)")
  const browserChoice = i18n.indexOf('navigator.language')
  assert.ok(urlChoice >= 0 && urlChoice < storedChoice)
  assert.ok(storedChoice < browserChoice)
  assert.match(i18n, /browserLocale\.toLowerCase\(\)\.startsWith\('zh'\) \? 'zh-TW' : 'en'/)
})

test('語言偏好會穩定保存，並同步網頁 lang 屬性', () => {
  assert.match(i18n, /LOCALE_STORAGE_KEY = 'storyvoice\.locale'/)
  assert.match(i18n, /document\.documentElement\.lang = locale/)
  assert.match(i18n, /localStorage\.setItem\(LOCALE_STORAGE_KEY, locale\)/)
  assert.match(i18n, /url\.searchParams\.set\('lang', nextLocale\)/)
  assert.match(i18n, /window\.history\.replaceState/)
  assert.match(i18n, /numberLocale: locale === 'en' \? 'en-US' : 'zh-TW'/)
  assert.match(i18n, /dateLocale: locale === 'en' \? 'en-US' : 'zh-TW'/)
  assert.match(main, /<LocaleProvider>[\s\S]*<BrowserRouter/)
})

test('繁中與 EN 切換是可讀取的 segmented control', () => {
  assert.match(switcher, /role="group"/)
  assert.match(switcher, /aria-pressed=\{locale === option\.locale\}/)
  assert.match(switcher, /locale: 'zh-TW', label: '繁中'/)
  assert.match(switcher, /locale: 'en', label: 'EN'/)
  assert.match(switcher, /Interface language/)
})

test('公開 about 頁不依賴登入殼層，並提供公開導覽與雙語產品定位', () => {
  const aboutRoute = app.indexOf('<Route element={<LandingPage publicMode />} path="/about" />')
  const privateLayout = app.indexOf('<Route element={<AppLayout />} path="/">')
  assert.ok(aboutRoute >= 0 && aboutRoute < privateLayout)
  assert.match(landing, /publicMode\?: boolean/)
  assert.match(landing, /to="\/voices"/)
  assert.match(landing, /to="\/developers\/docs"/)
  assert.match(landing, /self-hosted AI audiobook studio/i)
  assert.match(landing, /Taiwan-Mandarin-first/)
  assert.match(landing, /No DRM circumvention/)
})

test('登入與私人工作區 chrome 有雙語文案與誠實能力提示', () => {
  assert.match(auth, /Sign in to StoryVoice/)
  assert.match(auth, /Create a StoryVoice account/)
  assert.match(auth, /<LanguageSwitcher/)
  assert.match(layout, /<LanguageSwitcher/)
  assert.match(layout, /Primary navigation/)
  assert.match(layout, /production workspace is currently Taiwan-Mandarin-first/)
})
