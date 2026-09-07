import { act, fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Outlet, Route, Routes } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'

import { CharacterLibraryPage } from '../../src/pages/CharacterLibraryPage'

const characters = ['小雨', '小晴'].map((canonicalName, index) => ({
  id: `synthetic-${index}`, canonicalName, personality: `${canonicalName}原本的個性`,
  hasAvatar: false, age: null, gender: null, birthday: null, catchphrase: null,
  background: '原本背景', speakingStyle: null, isActive: true,
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
}))

let completeAi: (response: Response) => void
let aiSignal: AbortSignal | undefined
let completeOldProfiles: ((response: Response) => void) | undefined
let completePreview: ((response: Response) => void) | undefined
let delayProfiles = false
let readyProfile = false

beforeEach(() => {
  delayProfiles = false
  readyProfile = false
  completeOldProfiles = undefined
  completePreview = undefined
  vi.stubGlobal('fetch', vi.fn((url: string, options?: RequestInit) => {
    if (url.endsWith('/ai-assist')) {
      aiSignal = options?.signal ?? undefined
      // Deliberately ignore cancellation to model a response already being decoded.
      return new Promise<Response>(resolve => { completeAi = resolve })
    }
    if (url.endsWith('/voice-profiles')) {
      if (delayProfiles && url.includes('synthetic-0')) return new Promise<Response>(resolve => { completeOldProfiles = resolve })
      return Promise.resolve(Response.json(readyProfile ? [{ id: 'voice', kind: 'Base', mode: 'Clone', status: 'Ready', referenceAudioDurationSeconds: 15 }] : []))
    }
    if (url.endsWith('/preview')) return new Promise<Response>(resolve => { completePreview = resolve })
    if (url.endsWith('/local-clone-preview')) return Promise.resolve(Response.json({ available: false, label: null }))
    if (url.endsWith('/character-profiles')) return Promise.resolve(Response.json(characters))
    throw new Error(`Unexpected test request: ${url}`)
  }))
  vi.stubGlobal('URL', class extends URL {
    static createObjectURL = vi.fn(() => 'blob:synthetic-preview')
    static revokeObjectURL = vi.fn()
  })
})

async function openLibrary() {
  const view = render(<MemoryRouter><Routes>
    <Route element={<Outlet context={{ email: 'qa@example.invalid', csrfToken: 'test-csrf' }} />}>
      <Route index element={<CharacterLibraryPage />} />
    </Route>
  </Routes></MemoryRouter>)
  await screen.findByDisplayValue('小雨')
  return view
}

const personality = () => screen.getByRole('textbox', { name: /^個性/ }) as HTMLTextAreaElement
const resolveAi = () => act(async () => completeAi(Response.json({ personality: 'AI 回傳的個性', background: '不該改寫的背景' })))

it('AI 生成途中切換角色，舊回應不會填進新角色或重新顯示成功訊息', async () => {
  const user = userEvent.setup()
  await openLibrary()
  await user.click(screen.getByRole('button', { name: /AI 補完個性/ }))
  await user.click(screen.getByRole('button', { name: /小晴/ }))
  await resolveAi()
  expect(personality().value).toBe('小晴原本的個性')
  expect(aiSignal?.aborted).toBe(true)
  expect(screen.queryByText(/AI 角色設定已生成/)).toBeNull()
  expect(screen.getByRole('button', { name: /AI 補完個性/ }).hasAttribute('disabled')).toBe(false)
})

it('AI 回應只補指定欄位，使用者在等待期間輸入的內容也會保留', async () => {
  const user = userEvent.setup()
  await openLibrary()
  await user.click(screen.getByRole('button', { name: /AI 補完個性/ }))
  fireEvent.change(personality(), { target: { value: '我剛修改的個性' } })
  await resolveAi()
  expect(personality().value).toBe('我剛修改的個性')
  expect((screen.getByRole('textbox', { name: /^人物背景/ }) as HTMLTextAreaElement).value).toBe('原本背景')
})

it('重設會取消未完成的 AI 填表，晚到回應不能蓋掉重設結果', async () => {
  const user = userEvent.setup()
  await openLibrary()
  await user.click(screen.getByRole('button', { name: /AI 補完個性/ }))
  await user.click(screen.getByRole('button', { name: '重設' }))
  await resolveAi()
  expect(personality().value).toBe('小雨原本的個性')
  expect(aiSignal?.aborted).toBe(true)
})

it('前一個角色晚到的聲線摘要不能套到新角色', async () => {
  const user = userEvent.setup()
  delayProfiles = true
  await openLibrary()
  await user.click(screen.getByRole('button', { name: /小晴/ }))
  await act(async () => completeOldProfiles!(Response.json([{ id: 'old-voice', kind: 'Base', mode: 'Clone', status: 'Ready', referenceAudioDurationSeconds: 15 }])))
  await user.click(screen.getByRole('button', { name: '試講', exact: true }))
  expect(screen.queryByRole('combobox', { name: '選擇聲線' })).toBeNull()
})

it('離開角色後完成的試講不會播放，也不會建立無法回收的 Blob URL', async () => {
  const user = userEvent.setup()
  readyProfile = true
  await openLibrary()
  await user.click(screen.getByRole('button', { name: '試講', exact: true }))
  await user.click(screen.getByRole('button', { name: /播放試講/ }))
  await user.click(screen.getByRole('button', { name: /小晴/ }))
  await act(async () => completePreview!(new Response('synthetic-audio', { headers: { 'Content-Type': 'audio/wav' } })))
  expect(URL.createObjectURL).not.toHaveBeenCalled()
})
