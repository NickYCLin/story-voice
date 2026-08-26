import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const app = await readFile(new URL('../src/pages/LibraryPage.tsx', import.meta.url), 'utf8')

test('library organizer exposes accessible search and sort controls', () => {
  for (const marker of [
    'aria-label="書庫整理工具"',
    '搜尋書名或作者',
    '最近加入',
    '符合 {visibleBooks.length}／全部 {books.length} 本',
    '沒有符合條件的書',
  ]) assert.ok(app.includes(marker), `missing ${marker}`)
  assert.ok(!app.includes('博客來'))
  assert.ok(!app.includes('Companion'))
})

test('library renders filtered books and only redirects when the book no longer exists', () => {
  assert.match(app, /visibleBooks\.map\(\(book\)/)
  // 重導只在這本書「真的不存在」時發生；被篩選條件濾出側欄仍保留使用者的選書。
  assert.match(app, /!books\.some\(\(book\) => book\.id === routeBookId\)/)
  assert.doesNotMatch(app, /!visibleBooks\.some\(\(book\) => book\.id === routeBookId\)/)
  assert.match(app, /navigate\(`\/library\/\$\{visibleBooks\[0\]\.id\}`, \{ replace: true \}\)/)
})

test('retired device tags no longer appear or touch local storage', () => {
  assert.ok(!app.includes('此裝置標籤'))
  assert.ok(!app.includes('storyvoice:device-book-tags:v1'))
  assert.ok(!app.includes('localStorage'))
})

test('library state is local to the page, not centrally reset by a logout handler', () => {
  // 登出集中在 AppLayout／auth.ts：整個私人殼層在 anonymous 狀態下會被 <AuthScreen>
  // 取代並卸載，LibraryPage 不需要（也沒有）自己的 handleLogout 手動清空狀態。
  assert.doesNotMatch(app, /handleLogout/)
  assert.match(app, /useState<LibraryCatalogFilters>\(defaultCatalogFilters\)/)
})
