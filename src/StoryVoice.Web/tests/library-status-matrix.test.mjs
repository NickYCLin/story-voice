import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const matrix = await readFile(new URL('../src/LibraryStatusMatrix.tsx', import.meta.url), 'utf8')
const app = await readFile(new URL('../src/pages/LibraryPage.tsx', import.meta.url), 'utf8')

test('library status matrix separates source capabilities from StoryVoice processing', () => {
  assert.match(matrix, /\/api\/library\/status-matrix\//)
  assert.match(matrix, /官方 TTS.*只代表來源閱讀器宣告的能力/)
  assert.match(matrix, /StoryVoice 音訊.*合法 EPUB／TXT/)
  assert.match(matrix, /storyVoiceNarrationMatchesAuthorizedText/)
  assert.match(matrix, /既有 StoryVoice 音訊（非目前合法正文）/)
  assert.doesNotMatch(matrix, /if \(!status\.authorizedTextAvailable\) return/)
  assert.match(matrix, /可處理正文：已就緒/)
  assert.match(matrix, /可處理正文：未提供/)
  assert.doesNotMatch(matrix, /擷取式摘要/)
  assert.match(matrix, /你的筆記/)
  assert.match(matrix, /authorized_text_required/)
  assert.match(matrix, /等待正文：這筆書籍資料沒有可處理內容/)
  assert.doesNotMatch(matrix, /上傳並連結/)
  assert.match(matrix, /正在讀取全部書籍的處理狀態/)
  assert.match(matrix, /role="status"/)
})

test('authenticated library renders the matrix and refreshes when imported-book identity changes', () => {
  assert.match(app, /<LibraryStatusMatrix/)
  assert.match(app, /book\.contentBookId/)
})
