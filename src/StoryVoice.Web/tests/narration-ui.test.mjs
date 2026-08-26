import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const panel = readFileSync(new URL('../src/NarrationPanel.tsx', import.meta.url), 'utf8')
const libraryPage = readFileSync(new URL('../src/pages/LibraryPage.tsx', import.meta.url), 'utf8')
const bookInsightsPanel = readFileSync(new URL('../src/BookInsightsPanel.tsx', import.meta.url), 'utf8')

test('new narration routes eligible authorized text through the multi-character series workflow', () => {
  assert.ok(panel.includes('book.authorizedTextAvailable || book.contentBookId !== null'))
  assert.ok(panel.includes('多角色系列配音'))
  assert.ok(panel.includes('固定旁白與角色聲線、逐章審核、全系列 staged rebuild'))
  assert.ok(panel.includes('to="/series"'))
  assert.ok(!panel.includes('function createNarration'))
})

test('narration discloses the configured local or external provider for imported files', () => {
  assert.ok(panel.includes('該系列目前設定的語音服務'))
  assert.ok(panel.includes('私人本機自架或外部供應商'))
  assert.ok(!panel.includes('文字會送往 Microsoft Edge'))
  assert.ok(panel.includes('主動匯入、且有權使用的無 DRM EPUB／TXT 正文'))
  assert.ok(!panel.includes('博客來'))
  assert.ok(panel.includes('音訊完成後保存於你的私人 StoryVoice 帳號'))
})

test('narration renders durable accessible progress instead of a static status badge', () => {
  assert.ok(panel.includes('role="progressbar"'))
  assert.ok(panel.includes('aria-valuenow={job.progressPercent}'))
  assert.ok(panel.includes('style={{ width: `${job.progressPercent}%` }}'))
  assert.ok(panel.includes("job.status === 'Queued' ? '等待執行' : '分塊語音合成中'"))
})

test('narration polling is serialized and stale responses cannot regress durable state', () => {
  assert.ok(panel.includes('window.setTimeout'))
  assert.ok(!panel.includes('window.setInterval'))
  assert.ok(panel.includes('mergeFreshJobs'))
  assert.ok(panel.includes('Date.parse(incoming.updatedAt) >= Date.parse(existing.updatedAt)'))
  assert.ok(panel.includes('job.bookId === bookId'))
  assert.ok(libraryPage.includes('<NarrationPanel key={selectedBook.id} book={selectedBook} csrfToken={csrfToken} />'))
})

test('narration mutations use CSRF, poll durable jobs, support cancel and private audio playback', () => {
  assert.ok(panel.includes("'X-CSRF-TOKEN': csrfToken"))
  assert.ok(panel.includes('window.setTimeout'))
  assert.ok(panel.includes('/cancel`'))
  assert.ok(panel.includes('/audio`)}'))
  assert.ok(panel.includes('<audio'))
  assert.ok(libraryPage.includes('<NarrationPanel key={selectedBook.id} book={selectedBook} csrfToken={csrfToken} />'))
  assert.ok(bookInsightsPanel.includes('const canAnalyzeText = book.authorizedTextAvailable'))
})
