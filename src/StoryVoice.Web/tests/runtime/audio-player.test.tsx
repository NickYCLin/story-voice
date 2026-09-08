import { act, fireEvent, render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { AudioPlayer, type NarrationTimeline } from '../../src/components/AudioPlayer'
import { LocaleProvider } from '../../src/i18n'

const timeline: NarrationTimeline = {
  jobId: 'synthetic-job', textAvailable: true,
  chapters: [
    { chapterId: 'one', sortOrder: 0, title: '雨夜', startMs: 0 },
    { chapterId: 'two', sortOrder: 1, title: '天亮', startMs: 60000 },
  ],
  turns: [
    { index: 0, startMs: 0, durationMs: 30000, chapterSortOrder: 0, kind: 'narrator', characterId: null, characterName: null, text: '雨落在窗邊。' },
    { index: 1, startMs: 60000, durationMs: 60000, chapterSortOrder: 1, kind: 'dialogue', characterId: 'speaker', characterName: '小雨', text: '早安。' },
  ],
}

const player = (src = '/first.mp3', storageKey = 'first') => (
  <LocaleProvider><AudioPlayer src={src} storageKey={storageKey} timeline={timeline} title="測試故事" /></LocaleProvider>
)

function loadAudio(container: HTMLElement, duration = 120) {
  const audio = container.querySelector('audio')!
  Object.defineProperty(audio, 'duration', { configurable: true, value: duration })
  fireEvent.loadedMetadata(audio)
  return audio
}

beforeEach(() => {
  vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {})
  vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => {})
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue()
})

describe('播放器實際互動', () => {
  it('解碼失敗後重試會恢復選定位置，不因重新載入音檔回到開頭', async () => {
    const { container } = render(player())
    const audio = loadAudio(container)
    let error: { code: number } | null = { code: 3 }
    let readyState = 0
    let seeking = false
    let time = 119
    Object.defineProperties(audio, {
      error: { configurable: true, get: () => error },
      readyState: { configurable: true, get: () => readyState },
      seeking: { configurable: true, get: () => seeking },
      currentTime: { configurable: true, get: () => time, set: (value: number) => { time = value; seeking = true } },
    })
    vi.mocked(audio.load).mockImplementation(() => { error = null; time = 0 })
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '播放', exact: true }))
      fireEvent.click(screen.getByRole('button', { name: '播放', exact: true }))
    })
    expect(audio.load).toHaveBeenCalledTimes(1)
    expect(audio.play).not.toHaveBeenCalled()
    readyState = 1
    await act(async () => fireEvent.loadedMetadata(audio))
    expect(audio.currentTime).toBe(119)
    expect(audio.play).not.toHaveBeenCalled()
    seeking = false
    await act(async () => fireEvent.seeked(audio))
    expect(audio.play).toHaveBeenCalledTimes(1)
  })

  it('跳轉尚未完成時先等 seeked，再從指定位置播放', async () => {
    const { container } = render(player())
    const audio = loadAudio(container)
    let seeking = true
    Object.defineProperty(audio, 'seeking', { configurable: true, get: () => seeking })
    audio.currentTime = 119
    await act(async () => fireEvent.click(screen.getByRole('button', { name: '播放', exact: true })))
    expect(audio.play).not.toHaveBeenCalled()
    // An older seek event must not resume while the latest seek is still pending.
    await act(async () => fireEvent.seeked(audio))
    expect(audio.play).not.toHaveBeenCalled()
    seeking = false
    await act(async () => fireEvent.seeked(audio))
    expect(audio.play).toHaveBeenCalledTimes(1)
    expect(audio.currentTime).toBe(119)
  })

  it('離開頁面會取消等待中的跳轉，不讓舊音檔稍後自行播放', async () => {
    const { container, unmount } = render(player())
    const audio = loadAudio(container)
    Object.defineProperty(audio, 'seeking', { configurable: true, value: true })
    await act(async () => fireEvent.click(screen.getByRole('button', { name: '播放', exact: true })))
    unmount()
    await act(async () => fireEvent.seeked(audio))
    expect(audio.play).not.toHaveBeenCalled()
  })

  it('連按播放不會重複等待，跳轉逾時後可再試', async () => {
    vi.useFakeTimers()
    try {
      const { container } = render(player())
      const audio = loadAudio(container)
      let seeking = true
      Object.defineProperty(audio, 'seeking', { configurable: true, get: () => seeking })
      const play = screen.getByRole('button', { name: '播放', exact: true })
      await act(async () => { fireEvent.click(play); fireEvent.click(play) })
      await act(async () => vi.advanceTimersByTimeAsync(10_000))
      expect(audio.play).not.toHaveBeenCalled()
      expect(screen.getByRole('alert').textContent).toContain('無法播放')
      await act(async () => { fireEvent.click(play); fireEvent.click(play) })
      seeking = false
      await act(async () => fireEvent.seeked(audio))
      expect(audio.play).toHaveBeenCalledTimes(1)
      expect(screen.queryByRole('alert')).toBeNull()
    } finally {
      vi.useRealTimers()
    }
  })

  it('進度變更立即跳轉，鍵盤操作不依賴滑鼠或觸控放開事件', () => {
    const { container } = render(player())
    const audio = loadAudio(container)
    const slider = screen.getByRole('slider', { name: '播放進度' })
    fireEvent.change(slider, { target: { value: '70' } })
    expect(audio.currentTime).toBe(70)
    expect(slider.getAttribute('aria-valuenow')).toBe('70')
    expect(screen.getByText('小雨')).toBeTruthy()
    // A click/release without any change must not jump back to an old seek value.
    fireEvent.mouseUp(slider)
    expect(audio.currentTime).toBe(70)
  })

  it('切換音檔與儲存鍵會清空舊狀態，並保存前一個音檔的位置', () => {
    localStorage.setItem('storyvoice.progress.first', JSON.stringify({ time: 42, duration: 120 }))
    const { container, rerender } = render(player())
    const audio = loadAudio(container)
    expect(screen.getByRole('button', { name: /00:42.*續播/ })).toBeTruthy()
    audio.currentTime = 30
    fireEvent.timeUpdate(audio)
    fireEvent.play(audio)
    rerender(player('/second.mp3', 'second'))
    expect(screen.queryByRole('button', { name: /續播/ })).toBeNull()
    expect(screen.getByRole('button', { name: '播放', exact: true })).toBeTruthy()
    expect(screen.getByRole('slider', { name: '播放進度' }).getAttribute('aria-valuenow')).toBe('0')
    expect(localStorage.getItem('storyvoice.progress.second')).toBeNull()
    expect(JSON.parse(localStorage.getItem('storyvoice.progress.first')!).time).toBe(30)
  })

  it('播放被拒絕時顯示可理解的錯誤，重試成功後會清除', async () => {
    vi.mocked(HTMLMediaElement.prototype.play).mockRejectedValueOnce(new DOMException('Blocked', 'NotAllowedError'))
    const { container } = render(player())
    loadAudio(container)
    await act(async () => fireEvent.click(screen.getByRole('button', { name: '播放', exact: true })))
    expect(screen.getByRole('alert').textContent).toContain('無法播放')
    await act(async () => fireEvent.click(screen.getByRole('button', { name: '播放', exact: true })))
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('音檔載入失敗也會提示錯誤，不留下看似仍在播放的按鈕', () => {
    const { container } = render(player())
    const audio = loadAudio(container)
    fireEvent.play(audio)
    fireEvent.error(audio)
    expect(screen.getByRole('alert').textContent).toContain('無法播放')
    expect(screen.getByRole('button', { name: '播放', exact: true })).toBeTruthy()
  })

  it('切換章節、回到章首與播放完成會更新同步文字及續播位置', () => {
    const { container } = render(player())
    const audio = loadAudio(container)
    fireEvent.click(screen.getByRole('button', { name: '下一章' }))
    expect(audio.currentTime).toBe(60)
    expect(screen.getByText('早安。')).toBeTruthy()
    expect(screen.getByRole('button', { name: '下一章' }).hasAttribute('disabled')).toBe(true)
    audio.currentTime = 80
    fireEvent.timeUpdate(audio)
    fireEvent.click(screen.getByRole('button', { name: '上一章' }))
    expect(audio.currentTime).toBe(60)
    fireEvent.click(screen.getByRole('button', { name: '上一章' }))
    expect(audio.currentTime).toBe(0)
    expect(localStorage.getItem('storyvoice.progress.first')).toBeNull()
    audio.currentTime = 120
    fireEvent.ended(audio)
    expect(localStorage.getItem('storyvoice.progress.first')).toBeNull()
  })

  it('音檔尚無有效長度時停用進度，不讓 NaN 或 Infinity 進入控制項', () => {
    const { container } = render(player())
    loadAudio(container, Infinity)
    const slider = screen.getByRole('slider', { name: '播放進度' })
    expect(slider.hasAttribute('disabled')).toBe(true)
    expect(slider.getAttribute('aria-valuemax')).toBe('0')
  })

  it('只查看播放器但沒有播放或跳轉，離開時仍保留原本的續播位置', () => {
    const saved = JSON.stringify({ time: 42, duration: 120 })
    localStorage.setItem('storyvoice.progress.first', saved)
    const { container, unmount } = render(player())
    const audio = loadAudio(container)
    fireEvent.timeUpdate(audio)
    fireEvent(window, new Event('pagehide'))
    unmount()
    expect(localStorage.getItem('storyvoice.progress.first')).toBe(saved)
  })

  it('音量拉到零後取消靜音會恢復可聽見的音量', () => {
    const { container } = render(player())
    const audio = loadAudio(container)
    fireEvent.change(screen.getByRole('slider', { name: '音量' }), { target: { value: '0' } })
    fireEvent.click(screen.getByRole('button', { name: '取消靜音' }))
    expect(audio.volume).toBe(1)
    expect(audio.muted).toBe(false)
  })
})
