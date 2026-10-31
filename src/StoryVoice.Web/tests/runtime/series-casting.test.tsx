import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Outlet, Route, Routes } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'
import { SeriesCastPanel } from '../../src/SeriesCastPanel'

const characters = ['小雨', '小晴'].map((canonicalName, index) => ({
  id: `character-${index}`, canonicalName, characterProfileId: `profile-${index}`, role: 'Main',
  voiceProvider: 'edge', voice: 'male', rate: '+0%', pitch: '+0Hz', volume: '+0%', aliases: [], notes: null,
}))
const base = { narratorProvider: 'edge', narratorVoice: 'male', narratorRate: '+0%', narratorPitch: '+0Hz', narratorVolume: '+0%',
  defaultSpeakerPauseMs: 180, activeCastRevisionId: null, pointOfViewCharacterId: null, narrativeVoiceMode: 'IndependentNarrator', books: [] }
const summaries = [{ id: 'series-0', name: '第一個測試系列', bookCount: 0, characterCount: 2 }, { id: 'series-1', name: '第二個測試系列', bookCount: 0, characterCount: 0 }]
let saved = { ...base, ...summaries[0], characters }
let delaySave = false
let completeSave: (response: Response) => void
let writes: Array<{ url: string; options: RequestInit }> = []

beforeEach(() => {
  saved = { ...base, ...summaries[0], characters }
  delaySave = false
  writes = []
  vi.stubGlobal('fetch', vi.fn((url: string, options?: RequestInit) => {
    if (options?.method === 'PUT') {
      writes.push({ url, options })
      const body = JSON.parse(options.body as string)
      saved = { ...saved, narratorProvider: body.narratorProvider, narratorVoice: body.narratorVoice,
        characters: saved.characters.map(character => ({ ...character, ...body.characters.find((item: { characterId: string }) => item.characterId === character.id) })) }
      return delaySave ? new Promise<Response>(resolve => { completeSave = resolve }) : Promise.resolve(Response.json(saved))
    }
    if (url.endsWith('/voice-options')) return Promise.resolve(Response.json(['male', 'female-a', 'female-b'].map(voice => ({
      provider: 'edge', voice, displayName: voice, locale: 'zh-TW', formalNarrationAvailable: true, usageScope: 'standard',
      gender: voice === 'male' ? 'male' : 'female', minimumAge: null, maximumAge: null, characterTags: [],
    }))))
    if (url.endsWith('/character-profiles')) return Promise.resolve(Response.json(characters.map(character => ({
      id: character.characterProfileId, canonicalName: character.canonicalName, gender: '女性', age: '20', isActive: true, hasAvatar: false,
    }))))
    if (url.endsWith('/local-clone-preview')) return Promise.resolve(Response.json({ available: false, label: null }))
    if (url.endsWith('/books') || url.endsWith('/narration-rebuilds')) return Promise.resolve(Response.json([]))
    if (url.endsWith('/series/')) return Promise.resolve(Response.json(summaries))
    if (url.endsWith('/series/series-0')) return Promise.resolve(Response.json(saved))
    if (url.endsWith('/series/series-1')) return Promise.resolve(Response.json({ ...base, ...summaries[1], characters: [] }))
    throw new Error(`Unexpected test request: ${url}`)
  }))
})

async function openSeries() {
  render(<MemoryRouter><Routes><Route element={<Outlet context={{ email: 'qa@example.invalid', csrfToken: 'test-csrf' }} />}>
    <Route index element={<SeriesCastPanel />} />
  </Route></Routes></MemoryRouter>)
  await screen.findByRole('button', { name: '依角色資料建議聲線' })
}
const form = () => within(screen.getByRole('form', { name: '整系列聲線設定' }))
const selected = (name: string) => (form().getByRole('combobox', { name }) as HTMLSelectElement).value

it('建議先填表並說明理由，按儲存才寫入，重新開啟仍保留已選的聲線', async () => {
  const user = userEvent.setup()
  await openSeries()
  await user.click(form().getByRole('button', { name: '依角色資料建議聲線' }))
  const choices = [selected('小雨'), selected('小晴')]
  expect(choices.every(choice => choice.startsWith('edge\nfemale-'))).toBe(true)
  expect(writes).toHaveLength(0)
  expect(form().getAllByText('符合角色庫性別設定。')).toHaveLength(2)
  await user.click(form().getByRole('button', { name: '儲存整系列聲線' }))
  expect(writes).toHaveLength(1)
  expect(writes[0].options.headers).toEqual(expect.objectContaining({ 'X-CSRF-TOKEN': 'test-csrf' }))
  await user.click(screen.getByRole('button', { name: /第二個測試系列/ }))
  await user.click(screen.getByRole('button', { name: /第一個測試系列/ }))
  await screen.findByRole('button', { name: '依角色資料建議聲線' })
  expect([selected('小雨'), selected('小晴')]).toEqual(choices)
})

it('建議保留手動選擇，也能還原成原本儲存的整組聲線', async () => {
  const user = userEvent.setup()
  await openSeries()
  await user.selectOptions(form().getByRole('combobox', { name: '小雨' }), 'edge\nfemale-b')
  await user.click(form().getByRole('button', { name: '依角色資料建議聲線' }))
  expect(selected('小雨')).toBe('edge\nfemale-b')
  expect(form().getByText('保留你手動選擇的聲線。')).toBeTruthy()
  await user.click(form().getByRole('button', { name: '還原已儲存聲線' }))
  expect([selected('小雨'), selected('小晴')]).toEqual(['edge\nmale', 'edge\nmale'])
  expect(writes).toHaveLength(0)
})

it('儲存途中切換系列，晚到的回應不能把新系列換回原系列', async () => {
  const user = userEvent.setup()
  delaySave = true
  await openSeries()
  await user.click(form().getByRole('button', { name: '依角色資料建議聲線' }))
  await user.click(form().getByRole('button', { name: '儲存整系列聲線' }))
  await user.click(screen.getByRole('button', { name: /第二個測試系列/ }))
  await screen.findByRole('heading', { name: '第二個測試系列' })
  await act(async () => completeSave(Response.json(saved)))
  expect(screen.getByRole('heading', { name: '第二個測試系列' })).toBeTruthy()
  expect(form().queryByRole('combobox', { name: '小雨' })).toBeNull()
  expect(form().getByRole('button', { name: '儲存整系列聲線' }).hasAttribute('disabled')).toBe(false)
})
