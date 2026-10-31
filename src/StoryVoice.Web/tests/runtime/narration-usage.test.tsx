import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { expect, it, vi } from 'vitest'
import { NarrationUsagePanel } from '../../src/components/NarrationUsagePanel'

const attempt = {
  id: 'attempt-1', startedAt: '2026-09-08T02:00:00Z', finishedAt: null, outcome: 'Unknown', provider: 'bluemagpie',
  inputCharacters: 1234, completedChunks: 2, totalChunks: 5, elapsedMs: null, synthesisElapsedMs: null, audioBytes: null,
}

it('loads on demand and keeps missing usage distinct from zero or a price', async () => {
  const fetch = vi.fn().mockResolvedValue(Response.json({ jobId: 'job-1', totalAttempts: 1, attempts: [attempt] }))
  vi.stubGlobal('fetch', fetch)
  render(<NarrationUsagePanel jobId="job-1" />)
  expect(fetch).not.toHaveBeenCalled()
  await userEvent.click(screen.getByRole('button', { name: '查看配音用量' }))
  expect(await screen.findByText('結果未確認')).toBeTruthy()
  expect(screen.getByText('1,234')).toBeTruthy()
  expect(screen.getByText('2 / 5')).toBeTruthy()
  expect(screen.getAllByText('未記錄')).toHaveLength(3)
  expect(screen.getByText(/尚未換算金額/)).toBeTruthy()
  expect(fetch).toHaveBeenCalledWith(expect.stringContaining('/api/narrations/job-1/usage'), expect.objectContaining({ method: 'GET', credentials: 'same-origin' }))
})

it('ignores late responses after switching jobs and supports retry after a read failure', async () => {
  let complete: (response: Response) => void = () => {}
  const fetch = vi.fn().mockImplementationOnce(() => new Promise<Response>(resolve => { complete = resolve }))
    .mockRejectedValueOnce(new Error('synthetic-private-diagnostic'))
    .mockResolvedValueOnce(Response.json({ jobId: 'job-2', totalAttempts: 0, attempts: [] }))
  vi.stubGlobal('fetch', fetch)
  const { rerender } = render(<NarrationUsagePanel jobId="job-1" />)
  await userEvent.click(screen.getByRole('button', { name: '查看配音用量' }))
  const signal = fetch.mock.calls[0][1].signal as AbortSignal
  rerender(<NarrationUsagePanel jobId="job-2" />)
  expect(signal.aborted).toBe(true)
  await act(async () => complete(Response.json({ jobId: 'job-1', totalAttempts: 1, attempts: [attempt] })))
  expect(screen.queryByText('1,234')).toBeNull()
  await userEvent.click(screen.getByRole('button', { name: '查看配音用量' }))
  expect((await screen.findByRole('alert')).textContent).toContain('用量暫時無法讀取')
  expect(screen.queryByText('synthetic-private-diagnostic')).toBeNull()
  await userEvent.click(screen.getByRole('button', { name: '更新用量' }))
  expect(await screen.findByText(/目前沒有紀錄/)).toBeTruthy()
})
