import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const app = readFileSync(new URL('../src/BookInsightsPanel.tsx', import.meta.url), 'utf8')

test('角色分析只接受使用者自行匯入的可處理正文', () => {
  assert.ok(app.includes('const canAnalyzeText = book.authorizedTextAvailable'))
  assert.ok(app.includes('請先匯入你有權處理的 EPUB 或 UTF-8 TXT'))
  assert.ok(!app.includes('/content-link`'))
})

test('retired extractive summary has no remaining UI or API request', () => {
  assert.ok(!app.includes('擷取式摘要'))
  assert.ok(!app.includes('/summary`'))
})

test('manual notes work independently from provider text and mutations carry CSRF', () => {
  assert.ok(app.includes('我的閱讀筆記'))
  assert.ok(app.includes('這裡只保存你親自輸入的帳號筆記'))
  assert.ok(app.includes("body: JSON.stringify({ body, chapterId: null })"))
  assert.ok(app.includes("method: 'DELETE'"))
  assert.ok(app.includes("headers: { 'X-CSRF-TOKEN': csrfToken }"))
})

test('local LLM candidates can be checked, merged by canonical name and applied to a series cast', () => {
  assert.ok(app.includes('本機 LLM 角色與 alias 分析'))
  assert.ok(app.includes('Canonical 名稱'))
  assert.ok(app.includes('Aliases（以、分隔）'))
  assert.ok(app.includes('建立／合併系列角色表'))
  assert.ok(app.includes("/character-analysis`"))
  assert.ok(app.includes('/analyzed-characters`'))
  assert.ok(app.includes('handleGenerateCharacterAnalysis'))
  assert.ok(app.includes("method: 'PUT'"))
  assert.ok(app.includes('本機 LLM 正在逐章讀取完整正文'))
  assert.ok(app.includes('完成後立即卸載'))
  assert.ok(app.includes('candidateDrafts[candidate.name]?.selected'))
  assert.ok(app.includes("to={selectedSeriesId ? `/series?seriesId=${selectedSeriesId}` : '/series'}"))
  assert.ok(!app.includes('/character-candidates`'))
  assert.ok(!app.includes('偵測到的說話角色'))
  assert.ok(!app.includes('FirstPersonNarrator'))
})

test('角色候選聲線以 provider 與 voice 複合 identity 選取，API payload 保留兩個欄位', () => {
  assert.ok(app.includes('const voiceKey = (voice: SeriesVoiceOption) => `${voice.provider}\\n${voice.voice}`'))
  assert.ok(app.includes('voiceKey(option) === draft.voiceKey'))
  assert.ok(app.includes('voiceProvider: voice.provider'))
  assert.ok(app.includes('voice: voice.voice'))
})

test('角色候選聲線跟隨目標系列 provider，不會由全域 catalog 建立混用 cast', () => {
  assert.ok(app.includes('narratorProvider: string'))
  assert.ok(app.includes("voice.provider === selectedSeries.narratorProvider"))
  assert.ok(app.includes("selectedSeries.narratorProvider === '3wa-voxcpm2' && voice.provider === 'edge'"))
  assert.ok(app.includes(".filter((voice) => voice.provider !== '3wa-voxcpm2')"))
  assert.ok(app.includes('applicableVoiceOptions.find((option) => voiceKey(option) === draft.voiceKey)'))
  assert.ok(app.includes('applicableVoiceOptions.map((voice) => <option'))
  assert.ok(!app.includes('voiceOptions.find((option) => voiceKey(option) === draft.voiceKey)'))
})

test('書籍分析介面不再提供外部書櫃同步與正文連結', () => {
  assert.ok(!app.includes('博客來'))
  assert.ok(!app.includes('來源 metadata'))
  assert.ok(!app.includes('/metadata-corrections`'))
})
