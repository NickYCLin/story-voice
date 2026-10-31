import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { expect, it, vi } from 'vitest'
import { PrivateNarrationTracks } from '../../src/components/PrivateNarrationTracks'

vi.mock('../../src/components/AudioPlayer', () => ({ AudioPlayer: ({ src }: { src: string }) => <audio aria-label="fixture audio" src={src} /> }))
const track = { jobId: 'job-1', mode: 'MultiCharacter', visibility: 'Staged', status: 'Completed', castRevisionId: 'cast-1', sourceHash: 'fixture-source', artifactVersion: 'artifact-1', audioBytes: 100, createdAt: '2026-10-13T00:00:00Z', available: true, blockedReason: null }
const state = { bookId: 'book-1', revision: 0, selectedJobId: null, tracks: [track] }

it('requires an explicit choice and sends the reviewed revision without publishing a cast', async () => {
  const fetch = vi.fn().mockResolvedValueOnce(Response.json(state)).mockResolvedValueOnce(Response.json({ ...state, revision: 1, selectedJobId: track.jobId }))
  vi.stubGlobal('fetch', fetch)
  render(<PrivateNarrationTracks bookId="book-1" csrfToken="fixture" />)
  await userEvent.selectOptions(await screen.findByRole('combobox'), 'job-1')
  expect(fetch).toHaveBeenCalledTimes(1)
  await userEvent.click(screen.getByRole('button', { name: '設為這本書的私人播放版本' }))
  expect(await screen.findByText('已儲存私人播放版本，其他音軌仍保留。')).toBeTruthy()
  expect(fetch).toHaveBeenLastCalledWith(expect.stringContaining('/tracks/selection'), expect.objectContaining({ method: 'PUT', body: JSON.stringify({ jobId: 'job-1', expectedRevision: 0, expectedArtifactVersion: 'artifact-1' }) }))
})

it('shows a conflict without reporting a successful switch', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(Response.json(state)).mockResolvedValueOnce(Response.json({ ...state, revision: 2 }, { status: 409 })))
  render(<PrivateNarrationTracks bookId="book-1" csrfToken="fixture" />)
  await userEvent.selectOptions(await screen.findByRole('combobox'), 'job-1')
  await userEvent.click(screen.getByRole('button', { name: '設為這本書的私人播放版本' }))
  expect(await screen.findByText(/音軌或選擇已變更/)).toBeTruthy()
  expect(screen.queryByText('已儲存私人播放版本，其他音軌仍保留。')).toBeNull()
})
