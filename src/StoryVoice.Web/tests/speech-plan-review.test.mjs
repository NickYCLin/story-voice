import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const review = readFileSync(new URL('../src/SpeechPlanReview.tsx', import.meta.url), 'utf8')

test('劇本審核只以 owner 取得的章節正文切片呈現，並標示低信心／待審核分段', () => {
  assert.match(review, /NeedsReview/)
  assert.match(review, /chapter\.originalText\.slice/)
  assert.match(review, /segment\.confidence/)
  assert.match(review, /確認這段角色/)
})

test('已確認的對白段也會顯示判定到的角色名字，不只是待審核段落', () => {
  assert.match(review, /segment\.characterName/)
  assert.match(review, /無法判定/)
})

test('內心／默讀片段顯示視角角色，且不列入對白待審核或角色指派', () => {
  assert.match(review, /kind: 'Narrator' \| 'Dialogue' \| 'InnerMonologue'/)
  assert.match(review, /segment\.kind === 'InnerMonologue' && `內心／默讀：\$\{segment\.characterName \?\? '無法判定'\}/)
  assert.match(review, /filter\(\(segment\) => segment\.kind === 'Dialogue' && segment\.reviewStatus !== 'Confirmed'\)/)
  assert.match(review, /segment\.kind === 'Dialogue' && segment\.reviewStatus !== 'Confirmed' && \(/)
})

test('計畫未確認時禁止建立 staged 多角色工作，並顯示缺口數', () => {
  assert.match(review, /confirmedGapCount/)
  assert.match(review, /disabled=\{confirmedGapCount > 0/)
  assert.match(review, /\/narration-rebuilds/)
  assert.match(review, /rightsAttested/)
  assert.match(review, /系列目前設定的語音服務/)
  assert.match(review, /私人本機自架或外部供應商/)
  assert.doesNotMatch(review, /交給 Edge 神經語音服務/)
})

test('劇本審核支援單句語音試聽與合成效果預覽', () => {
  assert.match(review, /previewSegmentAudio/)
  assert.match(review, /試聽此句/)
  assert.match(review, /\/api\/developer\/playground\/synthesize/)
})

