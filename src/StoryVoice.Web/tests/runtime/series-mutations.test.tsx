import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Outlet, Route, Routes } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'
import { SeriesCastPanel } from '../../src/SeriesCastPanel'

const voice = 'localclone:11111111111111111111111111111111'
const profile = { id: 'profile-1', canonicalName: '角色庫甲', isActive: true, hasAvatar: false }
const summaries = ['甲', '乙'].map((name, index) => ({ id: `series-${index}`, name: `${name}系列`, bookCount: 0, characterCount: 1 }))
const details = summaries.map((summary, index) => ({
  ...summary, narratorProvider: 'local-clone', narratorVoice: voice, narratorRate: '+0%',
  narratorPitch: '+0Hz', narratorVolume: '+0%', defaultSpeakerPauseMs: 180,
  activeCastRevisionId: null, pointOfViewCharacterId: null, narrativeVoiceMode: 'IndependentNarrator', books: [],
  characters: [{ id: `character-${index}`, canonicalName: `${index ? '乙' : '甲'}角色`, role: 'Main',
    characterProfileId: null, voiceProvider: 'local-clone', voice, rate: '+0%', pitch: '+0Hz', volume: '+0%', notes: null, aliases: [] }],
}))
let pending: Array<{ url: string; body: unknown; resolve: (response: Response) => void }>

beforeEach(() => {
  pending = []
  vi.stubGlobal('fetch', vi.fn((url: string, options?: RequestInit) => {
    if (options?.method === 'PUT' || options?.method === 'POST') return new Promise<Response>(resolve => pending.push({ url, body: JSON.parse(options.body as string), resolve }))
    if (url.endsWith('/voice-options')) return Promise.resolve(Response.json([{
      provider: 'local-clone', voice, displayName: '授權角色參考', characterProfileId: profile.id,
      locale: 'zh-TW', formalNarrationAvailable: true, usageScope: 'private',
    }]))
    if (url.endsWith('/character-profiles')) return Promise.resolve(Response.json([profile]))
    if (url.endsWith('/reference-audio')) return Promise.resolve(new Response('fixture-audio', { headers: { 'Content-Type': 'audio/wav' } }))
    if (url.endsWith('/local-clone-preview')) return Promise.resolve(Response.json({ available: false, label: null }))
    if (url.endsWith('/books') || url.endsWith('/narration-rebuilds')) return Promise.resolve(Response.json([]))
    if (url.endsWith('/series/')) return Promise.resolve(Response.json(summaries))
    const detail = details.find(item => url.endsWith(`/series/${item.id}`))
    if (detail) return Promise.resolve(Response.json(detail))
    throw new Error(`Unexpected request: ${url}`)
  }))
  vi.stubGlobal('URL', class extends URL {
    static createObjectURL = vi.fn(() => 'blob:private-reference')
    static revokeObjectURL = vi.fn()
  })
})

async function openSeries() {
  const view = render(<MemoryRouter><Routes><Route element={<Outlet context={{ csrfToken: 'fixture' }} />}>
    <Route index element={<SeriesCastPanel />} />
  </Route></Routes></MemoryRouter>)
  await screen.findByRole('heading', { name: '甲系列' })
  return view
}
async function link(name: string) {
  const select = screen.getByRole('combobox', { name: `${name}角色 的角色庫連結` })
  await userEvent.selectOptions(select, profile.id)
  await userEvent.click(within(select.closest('form')!).getByRole('button', { name: '儲存單角連結' }))
}
async function switchToSecond() {
  await userEvent.click(screen.getByRole('button', { name: /乙系列/ }))
  await screen.findByRole('heading', { name: '乙系列' })
}

it('更換角色庫連結後切換系列，舊回應不覆蓋新系列或解除新請求的忙碌狀態', async () => {
  await openSeries()
  await link('甲')
  expect(pending[0].url).toContain('/series-0/characters/character-0/character-profile')
  await switchToSecond()
  await link('乙')
  await act(async () => pending[0].resolve(Response.json({ ...details[0], characters: [{ ...details[0].characters[0], characterProfileId: profile.id }] })))
  expect(screen.getByRole('heading', { name: '乙系列' })).toBeTruthy()
  expect(screen.getByRole('button', { name: '儲存中…' }).hasAttribute('disabled')).toBe(true)
  await act(async () => pending[1].resolve(Response.json({ ...details[1], characters: [{ ...details[1].characters[0], characterProfileId: profile.id }] })))
  expect(await screen.findByText(/已將 乙角色 連結到角色庫/)).toBeTruthy()
})

it('舊系列的連結錯誤不顯示在新系列，切回原系列才重新讀取狀態', async () => {
  await openSeries()
  await link('甲')
  await switchToSecond()
  await act(async () => pending[0].resolve(Response.json({ detail: '舊系列錯誤' }, { status: 400 })))
  expect(screen.queryByText('舊系列錯誤')).toBeNull()
  expect(screen.getByRole('heading', { name: '乙系列' })).toBeTruthy()
  await userEvent.click(screen.getByRole('button', { name: /甲系列/ }))
  await screen.findByRole('heading', { name: '甲系列' })
  expect((screen.getByRole('combobox', { name: '甲角色 的角色庫連結' }) as HTMLSelectElement).value).toBe('')
})

it('切換系列移除並回收前一系列的私人參考錄音，不觸發合成', async () => {
  const { container } = await openSeries()
  await userEvent.click(screen.getAllByRole('button', { name: '播放角色參考錄音' })[0])
  await screen.findByText('授權角色參考 · 角色管理參考錄音')
  expect(container.querySelector('audio')?.getAttribute('src')).toBe('blob:private-reference')
  await switchToSecond()
  expect(container.querySelector('audio')).toBeNull()
  expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:private-reference')
  expect(pending).toHaveLength(0)
})

it('切換系列後新增別名使用新系列的角色 ID', async () => {
  await openSeries()
  await switchToSecond()
  const button = screen.getByRole('button', { name: '加入別名' })
  const form = within(button.closest('form')!)
  await userEvent.type(form.getByRole('textbox'), '新的別名')
  await userEvent.click(button)
  expect(pending).toHaveLength(1)
  expect(pending[0].url).toContain('/series-1/characters/character-1/aliases')
})

it('建立系列途中切換到既有系列，完成後只新增清單項目而不強制切頁', async () => {
  await openSeries()
  await userEvent.type(screen.getByRole('textbox', { name: '新系列名稱' }), '丙系列')
  await userEvent.click(screen.getByRole('button', { name: '建立系列' }))
  expect(pending).toHaveLength(1)
  await switchToSecond()
  await act(async () => pending[0].resolve(Response.json({ ...details[0], id: 'series-new', name: '丙系列', characters: [] })))
  expect(screen.getByRole('heading', { name: '乙系列' })).toBeTruthy()
  expect(screen.getByRole('button', { name: /丙系列/ })).toBeTruthy()
})
