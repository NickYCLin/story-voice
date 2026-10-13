import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const collectionsPage = readFileSync(new URL('../src/pages/CollectionsPage.tsx', import.meta.url), 'utf8')
const collectionDetailPage = readFileSync(new URL('../src/pages/CollectionDetailPage.tsx', import.meta.url), 'utf8')
const appLayout = (readFileSync(new URL('../src/AppLayout.tsx', import.meta.url), 'utf8') + readFileSync(new URL('../src/components/PublicSiteHeader.tsx', import.meta.url), 'utf8'))

test('書冊列表可以建立新書冊，且與角色配音系列(series) API 無關', () => {
  assert.match(collectionsPage, /\/api\/collections/)
  assert.match(collectionsPage, /建立新書冊/)
  assert.doesNotMatch(collectionsPage, /\/api\/series/)
  assert.doesNotMatch(collectionsPage, /narratorVoice/)
})

test('書冊詳情頁可以調整成員書籍排序，且只能加入含正文的書籍', () => {
  assert.match(collectionDetailPage, /\/api\/collections\/\$\{collectionId\}\/books/)
  assert.match(collectionDetailPage, /book\.status !== 'Linked'/)
  assert.match(collectionDetailPage, /移出書冊/)
  assert.match(collectionDetailPage, /sortOrder/)
})

test('書冊詳情頁的分享表單以 email 分享唯讀存取，並可撤銷', () => {
  assert.match(collectionDetailPage, /分享給其他使用者（唯讀）/)
  assert.match(collectionDetailPage, /\/api\/collections\/\$\{collectionId\}\/shares/)
  assert.match(collectionDetailPage, /type="email"/)
  assert.match(collectionDetailPage, /撤銷分享/)
  assert.match(collectionDetailPage, /看不到你的閱讀筆記、摘要或朗讀音訊/)
})

test('刪除書冊與移出書籍前都會先跳出確認對話框，不是瀏覽器原生 confirm', () => {
  assert.match(collectionsPage, /<ConfirmDialog/)
  assert.match(collectionDetailPage, /<ConfirmDialog/)
  assert.doesNotMatch(collectionsPage, /window\.confirm/)
  assert.doesNotMatch(collectionDetailPage, /window\.confirm/)
})

test('主導覽包含書冊與分享給我的入口', () => {
  assert.match(appLayout, /to="\/collections"/)
  assert.match(appLayout, /to="\/shared"/)
})
