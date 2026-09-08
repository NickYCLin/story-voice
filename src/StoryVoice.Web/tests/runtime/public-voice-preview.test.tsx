import { act, fireEvent, render, screen } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import { VoicePreviewButton } from '../../src/components/PublicVoicePreview'
import type { PublicVoiceCard } from '../../src/publicVoiceCatalog'

const voice = (alias = 'synthetic-voice'): PublicVoiceCard => ({
  alias, displayName: '測試聲線', subtitle: '合成測試資料', disclosure: 'AI 合成語音',
  styles: [], useCases: [], sampleUrl: `/api/public/v1/voices/${alias}/demo`,
  canPreview: true, ctaKind: 'contact', subscriptionAvailable: false, status: 'available',
})

beforeEach(() => {
  vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {})
  vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => {})
})

function deferred() {
  let reject!: (error: Error) => void
  let resolve!: () => void
  const promise = new Promise<void>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

function media(container: HTMLElement) {
  const audio = container.querySelector('audio')!
  let paused = true
  Object.defineProperty(audio, 'paused', { configurable: true, get: () => paused })
  vi.spyOn(audio, 'pause').mockImplementation(() => { paused = true; fireEvent.pause(audio) })
  return { audio, play: () => { paused = false; fireEvent.play(audio) } }
}

it('等待試聽時可暫停，取消中的播放要求不會被誤報為失敗', async () => {
  const pending = deferred()
  const view = render(<VoicePreviewButton voice={voice()} />)
  const { audio, play } = media(view.container)
  vi.spyOn(audio, 'play').mockImplementation(() => { play(); return pending.promise })
  fireEvent.click(screen.getByRole('button', { name: '播放固定示範' }))
  fireEvent.waiting(audio)
  expect(screen.getByRole('status').textContent).toBe('示範音檔載入中…')
  expect(screen.getByRole('button', { name: '暫停固定示範' })).toBeTruthy()
  fireEvent.click(screen.getByRole('button', { name: '暫停固定示範' }))
  await act(async () => pending.reject(new DOMException('The play request was interrupted.', 'AbortError')))
  expect(screen.queryByRole('alert')).toBeNull()
  expect(screen.queryByRole('status')).toBeNull()
  expect(screen.getByRole('button', { name: '播放固定示範' })).toBeTruthy()
})

it('真正的音檔錯誤可重新載入重試，恢復播放後清除等待提示', async () => {
  const pending = deferred()
  const view = render(<VoicePreviewButton voice={voice()} />)
  const { audio, play } = media(view.container)
  fireEvent.error(audio)
  expect(screen.getByRole('alert')).toBeTruthy()
  Object.defineProperty(audio, 'error', { configurable: true, value: { code: 3 } })
  vi.spyOn(audio, 'play').mockImplementation(() => { play(); return pending.promise })
  fireEvent.click(screen.getByRole('button', { name: '播放固定示範' }))
  expect(audio.load).toHaveBeenCalledTimes(1)
  fireEvent.waiting(audio)
  expect(screen.getByRole('status')).toBeTruthy()
  fireEvent.playing(audio)
  await act(async () => pending.resolve())
  expect(screen.queryByRole('status')).toBeNull()
  expect(screen.queryByRole('alert')).toBeNull()
  fireEvent.stalled(audio)
  expect(screen.queryByRole('status')).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: '暫停固定示範' }))
  fireEvent.playing(audio)
  expect(screen.getByRole('button', { name: '播放固定示範' })).toBeTruthy()
})

it('播放尚未開始時合併重複要求，離開後忽略回應', async () => {
  const pending = deferred()
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockReturnValue(pending.promise)
  const view = render(<VoicePreviewButton voice={voice()} />)
  const audio = view.container.querySelector('audio')!
  fireEvent.click(screen.getByRole('button', { name: '播放固定示範' }))
  fireEvent.click(screen.getByRole('button', { name: '播放固定示範' }))
  expect(audio.play).toHaveBeenCalledTimes(1)
  view.unmount()
  expect(audio.pause).toHaveBeenCalled()
  await act(async () => pending.reject(new Error('late failure')))
})

it('前一次取消的晚到錯誤不能覆蓋新一次播放', async () => {
  const first = deferred()
  const second = deferred()
  const view = render(<VoicePreviewButton voice={voice()} />)
  const { audio, play } = media(view.container)
  vi.spyOn(audio, 'play').mockImplementationOnce(() => { play(); return first.promise })
    .mockImplementationOnce(() => { play(); return second.promise })
  fireEvent.click(screen.getByRole('button', { name: '播放固定示範' }))
  fireEvent.click(screen.getByRole('button', { name: '暫停固定示範' }))
  fireEvent.click(screen.getByRole('button', { name: '播放固定示範' }))
  await act(async () => first.reject(new Error('old request failed')))
  expect(screen.queryByRole('alert')).toBeNull()
  expect(screen.getByRole('button', { name: '暫停固定示範' })).toBeTruthy()
  await act(async () => second.resolve())
})

it('更換聲線會停止舊音訊，晚到的播放錯誤不會顯示在新聲線', async () => {
  const pending = deferred()
  const view = render(<VoicePreviewButton voice={voice()} />)
  const { audio, play } = media(view.container)
  vi.spyOn(audio, 'play').mockImplementation(() => { play(); return pending.promise })
  fireEvent.click(screen.getByRole('button', { name: '播放固定示範' }))
  view.rerender(<VoicePreviewButton voice={voice('second-voice')} />)
  expect(audio.pause).toHaveBeenCalled()
  await act(async () => pending.reject(new Error('old source failed')))
  expect(screen.queryByRole('alert')).toBeNull()
  expect(screen.getByRole('button', { name: '播放固定示範' })).toBeTruthy()
})
