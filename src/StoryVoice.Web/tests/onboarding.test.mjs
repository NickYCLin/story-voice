import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const libraryPage = readFileSync(new URL('../src/pages/LibraryPage.tsx', import.meta.url), 'utf8')

function positionOf(source, marker) {
  const position = source.indexOf(marker)
  assert.notEqual(position, -1, `找不到必要的新手介面標記：${marker}`)
  return position
}

test('直接上傳是唯一且清楚的書籍匯入主流程', () => {
  positionOf(libraryPage, 'id="book-file"')
  assert.match(libraryPage, /推薦方式/)
  assert.match(libraryPage, /書籍來自哪個平台都沒關係/)
  assert.doesNotMatch(libraryPage, /同步博客來|Companion/)
})

test('空書庫畫面直接教使用者 3 步驟開始，不依賴公開介紹頁', () => {
  const emptyState = positionOf(libraryPage, "books.length === 0")
  const stepOne = positionOf(libraryPage, '準備一本你有權處理、無 DRM 的 EPUB 或 UTF-8 TXT')
  const stepTwo = positionOf(libraryPage, '選擇檔案並按「匯入並解析」')
  const stepThree = positionOf(libraryPage, '匯入後選書、展開章節檢查解析內容')

  assert.ok(emptyState < stepOne, '3 步驟教學應該在空書庫狀態內')
  assert.ok(stepOne < stepTwo && stepTwo < stepThree, '3 步驟教學必須依序出現')
})
