import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Outlet, Route, Routes } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'

import { SeriesCastPanel } from '../../src/SeriesCastPanel'

const series = ['第一個測試系列', '第二個測試系列'].map((name, index) => ({
  id: `series-${index}`, name, bookCount: 0, characterCount: 0, activeCastRevisionId: null,
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  narratorProvider: 'bluemagpie', narratorVoice: 'female_voice', narratorRate: '+0%',
  narratorPitch: '+0Hz', narratorVolume: '+0%', defaultSpeakerPauseMs: 180,
  pointOfViewCharacterId: null, narrativeVoiceMode: 'IndependentNarrator', books: [], characters: [],
}))
const failed = { id: 'failed-batch', status: 'Failed', members: [] }
let completeRetry: (response: Response) => void
let completeOldList: ((response: Response) => void) | undefined
let delayList = false

beforeEach(() => {
  delayList = false
  completeOldList = undefined
  vi.stubGlobal('fetch', vi.fn((url: string) => {
    if (url.endsWith('/retry')) return new Promise<Response>(resolve => { completeRetry = resolve })
    if (url.endsWith('/narration-rebuilds')) {
      if (url.includes('series-1')) return Promise.resolve(Response.json([]))
      if (delayList) return new Promise<Response>(resolve => { completeOldList = resolve })
      return Promise.resolve(Response.json([failed]))
    }
    if (url.endsWith('/voice-options')) return Promise.resolve(Response.json([{
      provider: 'bluemagpie', voice: 'female_voice', displayName: '測試女聲', locale: 'zh-TW',
      formalNarrationAvailable: true, usageScope: 'private-self-hosted',
    }]))
    if (url.endsWith('/books') || url.endsWith('/character-profiles')) return Promise.resolve(Response.json([]))
    if (url.endsWith('/series/')) return Promise.resolve(Response.json(series))
    const details = series.find(item => url.endsWith(`/series/${item.id}`))
    if (details) return Promise.resolve(Response.json(details))
    throw new Error(`Unexpected test request: ${url}`)
  }))
})

async function openSeries() {
  render(<MemoryRouter><Routes>
    <Route element={<Outlet context={{ email: 'qa@example.invalid', csrfToken: 'test-csrf' }} />}>
      <Route index element={<SeriesCastPanel />} />
    </Route>
  </Routes></MemoryRouter>)
  await screen.findByRole('button', { name: /第二個測試系列/ })
}

it('重新開啟後載入失敗批次，確認權利才能接續，重試保留原批次', async () => {
  const user = userEvent.setup()
  await openSeries()
  const retry = await screen.findByRole('button', { name: '接續未完成配音' })
  expect(retry.hasAttribute('disabled')).toBe(true)
  await user.click(screen.getByRole('checkbox', { name: /我確認仍有權處理/ }))
  await user.click(retry)
  expect(screen.getByRole('button', { name: '正在恢復…' }).hasAttribute('disabled')).toBe(true)
  expect(fetch).toHaveBeenCalledWith('/api/series/series-0/narration-rebuilds/failed-batch/retry', expect.objectContaining({
    method: 'POST', body: JSON.stringify({ rightsAttested: true }),
    headers: expect.objectContaining({ 'X-CSRF-TOKEN': 'test-csrf' }),
  }))
  await act(async () => completeRetry(Response.json({ ...failed, status: 'Building' })))
  expect(screen.queryByRole('button', { name: '接續未完成配音' })).toBeNull()
  expect(screen.getByRole('heading', { name: '配音中' })).toBeTruthy()
  expect(screen.getByText(/已接續原配音工作/)).toBeTruthy()
})

it('重試途中切換系列，晚到的結果不能套用到新系列', async () => {
  const user = userEvent.setup()
  await openSeries()
  await screen.findByRole('button', { name: '接續未完成配音' })
  await user.click(screen.getByRole('checkbox', { name: /我確認仍有權處理/ }))
  await user.click(screen.getByRole('button', { name: '接續未完成配音' }))
  await user.click(screen.getByRole('button', { name: /第二個測試系列/ }))
  await act(async () => completeRetry(Response.json({ ...failed, status: 'Building' })))
  expect(screen.queryByRole('heading', { name: '配音中' })).toBeNull()
  expect(screen.queryByText(/已接續原配音工作/)).toBeNull()
})

it('前一個系列的批次清單晚到，不會在新系列顯示恢復入口', async () => {
  const user = userEvent.setup()
  delayList = true
  await openSeries()
  await vi.waitFor(() => expect(completeOldList).toBeDefined())
  await user.click(screen.getByRole('button', { name: /第二個測試系列/ }))
  await act(async () => completeOldList!(Response.json([failed])))
  expect(screen.queryByRole('region', { name: '恢復配音' })).toBeNull()
})

it('恢復被拒絕時保留失敗批次並顯示原因', async () => {
  const user = userEvent.setup()
  await openSeries()
  await screen.findByRole('button', { name: '接續未完成配音' })
  await user.click(screen.getByRole('checkbox', { name: /我確認仍有權處理/ }))
  await user.click(screen.getByRole('button', { name: '接續未完成配音' }))
  await act(async () => completeRetry(Response.json({ detail: '已確認的劇本已變更，請建立新批次。' }, { status: 400 })))
  expect(screen.getByText('已確認的劇本已變更，請建立新批次。')).toBeTruthy()
  expect(screen.getByRole('heading', { name: '配音失敗' })).toBeTruthy()
  expect(screen.getByRole('button', { name: '接續未完成配音' }).hasAttribute('disabled')).toBe(false)
})
