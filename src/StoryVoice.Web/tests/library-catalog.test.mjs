import assert from 'node:assert/strict'
import test from 'node:test'

import { filterAndSortBooks } from '../src/libraryCatalog.ts'

const baseBooks = [
  {
    id: 'b-2', title: '月夜故事', author: '作者乙', createdAt: '2026-08-02T00:00:00Z',
  },
  {
    id: 'b-1', title: 'Alpha Tale', author: 'Alice', createdAt: '2026-08-03T00:00:00Z',
  },
  {
    id: 'b-3', title: '彼岸之書', author: '作者甲', createdAt: '2026-08-01T00:00:00Z',
  },
]

const defaults = { query: '', sort: 'created-desc' }

test('searches title and author without mutating input', () => {
  const original = structuredClone(baseBooks)
  assert.deepEqual(filterAndSortBooks(baseBooks, { ...defaults, query: 'alpha' }).map(book => book.id), ['b-1'])
  assert.deepEqual(filterAndSortBooks(baseBooks, { ...defaults, query: '作者甲' }).map(book => book.id), ['b-3'])
  assert.deepEqual(baseBooks, original)
})

test('sorts deterministically by title, author, and creation time', () => {
  assert.deepEqual(filterAndSortBooks(baseBooks, { ...defaults, sort: 'title' }).map(book => book.id), ['b-2', 'b-3', 'b-1'])
  assert.deepEqual(filterAndSortBooks(baseBooks, { ...defaults, sort: 'author' }).map(book => book.id), ['b-2', 'b-3', 'b-1'])
  assert.deepEqual(filterAndSortBooks(baseBooks, defaults).map(book => book.id), ['b-1', 'b-2', 'b-3'])
})

test('title sort understands Chinese-numeral volume markers, not just Arabic digits', () => {
  const volumes = [
    { id: 'v11', title: '特殊傳說第十一部：黑館的秘密', author: '護玄', createdAt: '2026-08-10T00:00:00Z' },
    { id: 'v2', title: '特殊傳說第二部：生存遊戲開始', author: '護玄', createdAt: '2026-08-10T00:00:00Z' },
    { id: 'v1', title: '特殊傳說第一部：不存在的學園', author: '護玄', createdAt: '2026-08-10T00:00:00Z' },
    { id: 'v10', title: '特殊傳說第十部：水之妖精族', author: '護玄', createdAt: '2026-08-10T00:00:00Z' },
  ]
  assert.deepEqual(
    filterAndSortBooks(volumes, { ...defaults, sort: 'title' }).map(book => book.id),
    ['v1', 'v2', 'v10', 'v11'],
  )
})
