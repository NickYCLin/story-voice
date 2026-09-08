import { act, fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import { useListeningProgress, type ListeningProgress } from '../../src/useListeningProgress'
import { AudioPlayer } from '../../src/components/AudioPlayer'
import { LocaleProvider } from '../../src/i18n'

const progress = (positionMs = 42000, version: string | null = 'v1'): ListeningProgress => ({
  jobId: 'job', positionMs, durationMs: 120000, version, updatedAt: '2026-09-08T00:00:00Z',
})
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((done) => { resolve = done })
  return { promise, resolve }
}
beforeEach(() => {
  vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {})
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue()
})

it('跨裝置讀取的進度可續播，單純開啟播放器不會回寫', async () => {
  const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(Response.json(progress()))
  function Player() {
    const saved = useListeningProgress('job', 'csrf')
    return <LocaleProvider><AudioPlayer src="/test.mp3" storageKey="job" serverProgress={saved.progress} onSaveProgress={saved.save} /></LocaleProvider>
  }
  const view = render(<Player />)
  const audio = view.container.querySelector('audio')!
  Object.defineProperty(audio, 'duration', { configurable: true, value: 120 })
  fireEvent.loadedMetadata(audio)
  expect(await screen.findByRole('button', { name: /00:42.*續播/ })).toBeTruthy()
  view.unmount()
  expect(fetchMock).toHaveBeenCalledTimes(1)
})

it('同一播放器依序保存，等待時只送出最後位置，包含導覽離開後的保存', async () => {
  const firstWrite = deferred<Response>()
  const fetchMock = vi.spyOn(globalThis, 'fetch')
    .mockResolvedValueOnce(Response.json(progress(0, null)))
    .mockReturnValueOnce(firstWrite.promise)
    .mockResolvedValueOnce(Response.json(progress(30000, 'v2')))
  const { result, unmount } = renderHook(() => useListeningProgress('job', 'csrf'))
  await waitFor(() => expect(result.current.status).toBe('ready'))
  act(() => { result.current.save(10000, 120000); result.current.save(20000, 120000); result.current.save(30000, 120000) })
  expect(fetchMock).toHaveBeenCalledTimes(2)
  unmount()
  await act(async () => firstWrite.resolve(Response.json(progress(10000, 'v1'))))
  expect(fetchMock).toHaveBeenCalledTimes(3)
  const options = fetchMock.mock.calls[2][1]!
  expect(JSON.parse(options.body as string)).toEqual({ positionMs: 30000, durationMs: 120000, expectedVersion: 'v1' })
  expect(options.keepalive).toBe(true)
  expect(options.headers).toMatchObject({ 'X-CSRF-TOKEN': 'csrf' })
})

it('舊分頁遇到版本衝突後停止覆寫其他裝置', async () => {
  const fetchMock = vi.spyOn(globalThis, 'fetch')
    .mockResolvedValueOnce(Response.json(progress()))
    .mockResolvedValueOnce(Response.json(progress(80000, 'v2'), { status: 409 }))
  const { result } = renderHook(() => useListeningProgress('job', 'csrf'))
  await waitFor(() => expect(result.current.status).toBe('ready'))
  act(() => result.current.save(50000, 120000))
  await waitFor(() => expect(result.current.status).toBe('conflict'))
  act(() => result.current.save(60000, 120000))
  expect(fetchMock).toHaveBeenCalledTimes(2)
})

it('讀取失敗可繼續使用本機進度，後續播放會重新讀取版本再儲存', async () => {
  const fetchMock = vi.spyOn(globalThis, 'fetch')
    .mockRejectedValueOnce(new TypeError('offline'))
    .mockResolvedValueOnce(Response.json(progress()))
    .mockResolvedValueOnce(Response.json(progress(60000, 'v2')))
  const { result } = renderHook(() => useListeningProgress('job', 'csrf'))
  await waitFor(() => expect(result.current.status).toBe('error'))
  act(() => result.current.save(60000, 120000))
  await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
  expect(JSON.parse(fetchMock.mock.calls[2][1]!.body as string).expectedVersion).toBe('v1')
  await waitFor(() => expect(result.current.status).toBe('ready'))
})

it('晚到的遠端進度不干擾已跳轉的播放器，播完也會保存結束位置', () => {
  const save = vi.fn()
  const player = (saved?: ListeningProgress) => <LocaleProvider><AudioPlayer src="/test.mp3" storageKey="job" serverProgress={saved} onSaveProgress={save} /></LocaleProvider>
  const { container, rerender } = render(player())
  const audio = container.querySelector('audio')!
  Object.defineProperty(audio, 'duration', { configurable: true, value: 120 })
  fireEvent.loadedMetadata(audio)
  fireEvent.change(screen.getByRole('slider', { name: '播放進度' }), { target: { value: '70' } })
  rerender(player(progress()))
  expect(audio.currentTime).toBe(70)
  expect(screen.queryByRole('button', { name: /續播/ })).toBeNull()
  fireEvent.ended(audio)
  expect(save).toHaveBeenLastCalledWith(120000, 120000)
})

it('遠端已播完會清除舊本機續播提示', () => {
  localStorage.setItem('storyvoice.progress.job', JSON.stringify({ time: 42, duration: 120 }))
  render(<LocaleProvider><AudioPlayer src="/test.mp3" storageKey="job" serverProgress={progress(120000)} /></LocaleProvider>)
  expect(screen.queryByRole('button', { name: /續播/ })).toBeNull()
})
