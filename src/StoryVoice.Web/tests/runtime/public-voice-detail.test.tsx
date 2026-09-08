import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { PublicVoiceDetailPage } from '../../src/pages/PublicVoiceDetailPage'

const detail = (alias = 'synthetic-voice') => ({
  voice: {
    alias, displayName: `測試聲線 ${alias}`, subtitle: '合成的測試資料', disclosure: 'AI 合成語音',
    styles: ['溫暖'], useCases: ['故事朗讀'], sampleUrl: `/api/public/v1/voices/${alias}/demo`,
    canPreview: true, ctaKind: 'view-plans', subscriptionAvailable: true, status: 'available',
  },
  license: {
    commercialUseAllowed: true, publicDistributionAllowed: true, crossProjectApiAllowed: true,
    effectiveAtUtc: new Date(Date.now() - 3600_000).toISOString(), expiresAtUtc: new Date(Date.now() + 3600_000).toISOString(),
    territoryMode: 'country-list', territoryCountryCodes: ['TW'],
  },
})

beforeEach(() => {
  vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {})
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue()
})
afterEach(() => vi.useRealTimers())

function Navigation() {
  const navigate = useNavigate()
  return <button onClick={() => navigate('/voices/second-voice')} type="button">切換聲線</button>
}
function show(alias = 'synthetic-voice') {
  return render(<MemoryRouter initialEntries={[`/voices/${alias}`]}>
    <Navigation />
    <Routes><Route path="/voices/:alias" element={<PublicVoiceDetailPage />} /></Routes>
  </MemoryRouter>)
}

it('reads anonymously and shows the validated scope without granting API access', async () => {
  const fetch = vi.fn().mockResolvedValue(Response.json(detail()))
  vi.stubGlobal('fetch', fetch)
  const view = show()
  expect(await screen.findByRole('heading', { name: '測試聲線 synthetic-voice' })).toBeTruthy()
  expect(fetch).toHaveBeenCalledWith(expect.stringContaining('/api/public/v1/voices/synthetic-voice'),
    expect.objectContaining({ credentials: 'omit', cache: 'no-store' }))
  expect(screen.getByText('台灣 (TW)')).toBeTruthy()
  expect(screen.getByText('須另外取得專案授權')).toBeTruthy()
  expect(view.container.querySelectorAll('time')).toHaveLength(2)
  const audio = view.container.querySelector('audio')!
  expect(audio.preload).toBe('none')
  expect(HTMLMediaElement.prototype.play).not.toHaveBeenCalled()
  await userEvent.click(screen.getByRole('button', { name: '播放固定示範' }))
  expect(HTMLMediaElement.prototype.play).toHaveBeenCalledTimes(1)
  view.unmount()
  expect(HTMLMediaElement.prototype.pause).toHaveBeenCalled()
})

it('hides withdrawn voices and retries a transient read error without showing private diagnostics', async () => {
  const fetch = vi.fn().mockRejectedValueOnce(new Error('synthetic-private-host'))
    .mockResolvedValueOnce(new Response(null, { status: 404 }))
  vi.stubGlobal('fetch', fetch)
  const view = show()
  expect(await screen.findByRole('alert')).toBeTruthy()
  expect(view.container.textContent).not.toContain('synthetic-private-host')
  await userEvent.click(screen.getByRole('button', { name: '重新載入' }))
  expect(await screen.findByText('目前無法公開查看這個聲線')).toBeTruthy()
  expect(view.container.querySelector('audio')).toBeNull()
})

it('rejects mismatched aliases and unsafe demo URLs', async () => {
  const unsafe = detail()
  unsafe.voice.sampleUrl = 'https://untrusted.invalid/audio.wav'
  const fetch = vi.fn().mockResolvedValueOnce(Response.json(detail('different-voice')))
    .mockResolvedValueOnce(Response.json(unsafe))
  vi.stubGlobal('fetch', fetch)
  const view = show()
  expect(await screen.findByRole('alert')).toBeTruthy()
  expect(view.container.querySelector('audio')).toBeNull()
  await userEvent.click(screen.getByRole('button', { name: '重新載入' }))
  expect(await screen.findByRole('button', { name: '固定示範尚未開放' })).toBeTruthy()
  expect(view.container.querySelector('audio')).toBeNull()
})

it('aborts the old request and ignores late JSON after changing the route', async () => {
  let complete: (value: unknown) => void = () => {}
  const fetch = vi.fn().mockResolvedValueOnce({ ok: true, status: 200, json: () => new Promise(resolve => { complete = resolve }) })
    .mockResolvedValueOnce(Response.json(detail('second-voice')))
  vi.stubGlobal('fetch', fetch)
  show()
  await waitFor(() => expect(fetch).toHaveBeenCalledTimes(1))
  const signal = fetch.mock.calls[0][1].signal as AbortSignal
  await userEvent.click(screen.getByRole('button', { name: '切換聲線' }))
  expect(await screen.findByRole('heading', { name: '測試聲線 second-voice' })).toBeTruthy()
  expect(signal.aborted).toBe(true)
  await act(async () => complete(detail()))
  expect(screen.queryByRole('heading', { name: '測試聲線 synthetic-voice' })).toBeNull()
})

it('revalidates on focus and stops the demo when the voice is no longer public', async () => {
  const fetch = vi.fn().mockResolvedValueOnce(Response.json(detail())).mockResolvedValueOnce(new Response(null, { status: 404 }))
  vi.stubGlobal('fetch', fetch)
  const view = show()
  expect(await screen.findByRole('button', { name: '播放固定示範' })).toBeTruthy()
  act(() => window.dispatchEvent(new Event('focus')))
  expect(await screen.findByText('目前無法公開查看這個聲線')).toBeTruthy()
  expect(view.container.querySelector('audio')).toBeNull()
  expect(HTMLMediaElement.prototype.pause).toHaveBeenCalled()
})

it('bounds reads and removes a voice at the advertised expiry', async () => {
  vi.useFakeTimers()
  const expiring = detail()
  expiring.license.expiresAtUtc = new Date(Date.now() + 1_000).toISOString()
  const fetch = vi.fn().mockImplementationOnce(() => new Promise(() => {}))
    .mockResolvedValueOnce(Response.json(expiring)).mockResolvedValueOnce(new Response(null, { status: 404 }))
  vi.stubGlobal('fetch', fetch)
  const view = show()
  const signal = fetch.mock.calls[0][1].signal as AbortSignal
  await act(async () => vi.advanceTimersByTimeAsync(10_000))
  expect(signal.aborted).toBe(true)
  expect(screen.getByRole('alert')).toBeTruthy()
  // The first response expired while the request was stalled; it must stay hidden.
  await act(async () => screen.getByRole('button', { name: '重新載入' }).click())
  expect(screen.getByText('目前無法公開查看這個聲線')).toBeTruthy()
  view.unmount()

  const fresh = detail()
  fresh.license.expiresAtUtc = new Date(Date.now() + 1_000).toISOString()
  fetch.mockReset().mockResolvedValueOnce(Response.json(fresh)).mockResolvedValueOnce(new Response(null, { status: 404 }))
  const secondView = show()
  await act(async () => {})
  expect(screen.getByRole('button', { name: '播放固定示範' })).toBeTruthy()
  await act(async () => vi.advanceTimersByTimeAsync(1_001))
  expect(screen.getByText('目前無法公開查看這個聲線')).toBeTruthy()
  expect(secondView.container.querySelector('audio')).toBeNull()
})
