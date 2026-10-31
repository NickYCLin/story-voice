import { act, fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, expect, it, vi } from 'vitest'

import { SpeechPlanReview, type SpeechPlanReviewEntry } from '../../src/SpeechPlanReview'

const chapter = { id: 'chapter', title: '雨夜', chapterNumber: 1, sortOrder: 0, originalText: '雨落下。天亮了。' }
const entries = [{
  book: { id: 'book', title: '測試故事' }, chapter,
  draft: {
    id: 'draft', seriesId: 'series', bookId: 'book', chapterId: chapter.id,
    planVersion: 1, status: 'ReadyToConfirm', confirmedRevisionId: null,
    segments: [0, 1].map(index => ({
      id: `segment-${index}`, sortOrder: index, sourceKind: 'Body', kind: 'Narrator',
      startOffset: index * 4, length: 4, characterId: null, characterName: null,
      confidence: 100, decisionSource: 'Rule', reviewStatus: 'Confirmed',
    })),
  },
}] as SpeechPlanReviewEntry[]

let requests: { resolve: (response: Response) => void; signal: AbortSignal }[]
beforeEach(() => {
  requests = []
  vi.stubGlobal('fetch', vi.fn((_url: string, options: RequestInit) => new Promise<Response>(resolve => {
    requests.push({ resolve, signal: options.signal! })
  })))
  let nextUrl = 0
  vi.stubGlobal('URL', class extends URL {
    static createObjectURL = vi.fn(() => `blob:sentence-${++nextUrl}`)
    static revokeObjectURL = vi.fn()
  })
})

const review = (seriesId = 'series') => <SpeechPlanReview seriesId={seriesId} entries={entries} characters={[]} narratorProvider="bluemagpie" characterVoiceOptions={[]} csrfToken="synthetic-csrf" onDraftUpdated={() => {}} onAddCharacter={async () => null} onRebuildCreated={() => {}} />
const complete = (index: number) => act(async () => requests[index].resolve(new Response('synthetic-audio')))

it('改聽另一句會取消前一個要求，晚到回應不會造成兩段重疊播放', async () => {
  const user = userEvent.setup()
  const { container, unmount } = render(review())
  await user.click(screen.getByRole('button', { name: /展開分段/ }))
  await user.click(screen.getAllByRole('button', { name: /試聽此句/ })[0])
  await user.click(screen.getAllByRole('button', { name: /試聽此句/ })[1])
  expect(requests[0].signal.aborted).toBe(true)
  await complete(1)
  const audio = container.querySelector('audio')!
  expect(audio.getAttribute('src')).toBe('blob:sentence-1')
  await complete(0)
  expect(URL.createObjectURL).toHaveBeenCalledTimes(1)
  fireEvent.play(audio)
  expect(screen.getByRole('button', { name: /播放中/ })).toBeTruthy()
  fireEvent.pause(audio)
  expect(screen.queryByRole('button', { name: /播放中/ })).toBeNull()
  unmount()
  expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:sentence-1')
})

it('切換系列與音訊錯誤都會清除播放器並回收試聽資源', async () => {
  const user = userEvent.setup()
  const { container, rerender } = render(review())
  await user.click(screen.getByRole('button', { name: /展開分段/ }))
  await user.click(screen.getAllByRole('button', { name: /試聽此句/ })[0])
  await complete(0)
  fireEvent.error(container.querySelector('audio')!)
  expect(container.querySelector('audio')).toBeNull()
  expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:sentence-1')
  await user.click(screen.getAllByRole('button', { name: /試聽此句/ })[0])
  rerender(review('another-series'))
  expect(requests[1].signal.aborted).toBe(true)
  await complete(1)
  expect(container.querySelector('audio')).toBeNull()
  expect(URL.createObjectURL).toHaveBeenCalledTimes(1)
})
