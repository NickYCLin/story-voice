import { LocaleProvider } from '../../src/i18n'
import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { PublicVoicesPage } from '../../src/pages/PublicVoicesPage'

const card = (alias: string, name: string, style: string) => ({
  alias, displayName: name, subtitle: '僅限測試資料', disclosure: 'AI 合成聲音',
  styles: [style], useCases: ['故事朗讀'], sampleUrl: `/api/public/v1/voices/${alias}/demo`,
  canPreview: true, ctaKind: 'contact', subscriptionAvailable: false, status: 'available',
})
const show = () => render(<LocaleProvider><MemoryRouter><PublicVoicesPage /></MemoryRouter></LocaleProvider>)
beforeEach(() => vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {}))
afterEach(() => vi.useRealTimers())

it('未啟用時在首個內容區說明狀態，不顯示假卡片或試聽入口', async () => {
  const fetch = vi.fn().mockResolvedValue(new Response(null, { status: 404 }))
  vi.stubGlobal('fetch', fetch)
  const view = show()
  expect(screen.getByRole('status').textContent).toContain('正在載入')
  expect(await screen.findByRole('heading', { name: '公開聲線館目前尚未啟用' })).toBeTruthy()
  expect(screen.queryByRole('searchbox')).toBeNull()
  expect(view.container.querySelector('audio')).toBeNull()
  expect(screen.getByRole('link', { name: /前往角色管理/ }).getAttribute('href')).toBe('/characters')
  expect(fetch).toHaveBeenCalledTimes(1)
  expect(fetch).toHaveBeenCalledWith(expect.stringContaining('/api/public/v1/voices'), expect.objectContaining({ credentials: 'omit' }))
})

it('空目錄與錯誤不同，失敗可以重試且不洩漏伺服器訊息', async () => {
  const fetch = vi.fn().mockRejectedValueOnce(new Error('private-host-secret'))
    .mockResolvedValueOnce(Response.json([]))
  vi.stubGlobal('fetch', fetch)
  const view = show()
  expect(await screen.findByRole('alert')).toBeTruthy()
  expect(view.container.textContent).not.toContain('private-host-secret')
  await userEvent.click(screen.getByRole('button', { name: '重新載入' }))
  expect(await screen.findByText('目前沒有可公開展示的聲線')).toBeTruthy()
  expect(screen.queryByRole('alert')).toBeNull()
})

it('搜尋及風格篩選能清除，選擇聲線只前往詳情而不授權或合成', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json([
    card('voice-a', '測試甲', '溫暖'), card('voice-b', '測試乙', '明亮'),
  ])))
  show()
  await screen.findByRole('searchbox')
  await userEvent.selectOptions(screen.getByLabelText('聲線風格'), '溫暖')
  expect(screen.getByRole('link', { name: '測試甲' })).toBeTruthy()
  expect(screen.queryByRole('link', { name: '測試乙' })).toBeNull()
  await userEvent.type(screen.getByRole('searchbox'), '找不到')
  expect(screen.getByText('找不到符合條件的聲線')).toBeTruthy()
  await userEvent.click(screen.getAllByRole('button', { name: '清除篩選' })[0])
  expect(screen.getByRole('link', { name: '測試乙' })).toBeTruthy()
  expect(screen.getAllByRole('link', { name: /查看聲線與使用範圍/ })[0].getAttribute('href')).toBe('/voices/voice-a')
  expect(screen.queryByRole('button', { name: /訂閱|合成/ })).toBeNull()
})

it('格式錯誤不會被當成空目錄或展示部分未驗證資料', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json([{ alias: 'invalid' }])))
  show()
  expect(await screen.findByRole('alert')).toBeTruthy()
  expect(screen.queryByText('目前沒有可公開展示的聲線')).toBeNull()
})

it('載入逾時會提供重試，取消後的晚到回應不會覆蓋狀態', async () => {
  vi.useFakeTimers()
  let resolve!: (response: Response) => void
  vi.stubGlobal('fetch', vi.fn().mockReturnValue(new Promise<Response>(yes => { resolve = yes })))
  show()
  await act(async () => { vi.advanceTimersByTime(10_000) })
  expect(screen.getByRole('alert')).toBeTruthy()
  await act(async () => resolve(Response.json([])))
  expect(screen.getByRole('alert')).toBeTruthy()
  expect(screen.queryByText('目前沒有可公開展示的聲線')).toBeNull()
})
