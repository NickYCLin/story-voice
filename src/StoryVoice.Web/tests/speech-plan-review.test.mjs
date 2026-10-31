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

test('單句試聽走伺服器端 segment 試聽端點，不再呼叫不存在的 playground 路由', () => {
  assert.match(review, /previewSegmentAudio/)
  assert.match(review, /試聽此句/)
  assert.match(review, /segments\/\$\{segment\.id\}\/preview/)
  assert.doesNotMatch(review, /\/api\/developer\/playground\/synthesize/)
  assert.doesNotMatch(review, /zh-TW-HsiaoChenNeural/)
})

test('不支援單句試聽的供應商會停用按鈕並說明原因，而不是丟出 404', () => {
  assert.match(review, /previewDisabledReason/)
  assert.match(review, /previewDisabled !== null/)
  assert.match(review, /整批合成工作中執行/)
})

test('已自動確認的判定可以修改：任何正文片段都能重新指派朗讀方式與說話者', () => {
  assert.match(review, /修改指派/)
  assert.match(review, /segments\/\$\{segment\.id\}\/reassign/)
  assert.match(review, /segment\.sourceKind === 'Body'/)
  assert.match(review, /內心／默讀片段必須指定角色/)
})

test('支援批次操作：接受全部建議、批次同角色確認、為缺草稿章節產生草稿', () => {
  assert.match(review, /confirm-suggested/)
  assert.match(review, /接受全部建議/)
  assert.match(review, /全部確認為/)
  assert.match(review, /buildMissingDrafts/)
  assert.match(review, /為缺草稿的/)
})

test('審核中可直接補建角色，聲線建立後固定', () => {
  assert.match(review, /onAddCharacter/)
  assert.match(review, /新角色名稱/)
  assert.match(review, /聲線建立後即固定/)
})

test('顯示統計與決策來源，狀態以中文呈現並解釋 Stale', () => {
  assert.match(review, /DRAFT_STATUS_LABELS/)
  assert.match(review, /DECISION_SOURCE_LABELS/)
  assert.match(review, /旁白 fallback/)
  assert.match(review, /已過期；請重新產生草稿後再審核/)
})

test('逐章摺疊：分段只在展開時渲染，指派下拉為受控元件', () => {
  assert.match(review, /expandedChapterId/)
  assert.match(review, /draft && isExpanded &&/)
  assert.match(review, /assignSelections/)
  assert.doesNotMatch(review, /document\.getElementById/)
  assert.doesNotMatch(review, /defaultValue/)
})
