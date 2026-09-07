import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Outlet, Route, Routes, useNavigate } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'

import { LocaleProvider } from '../../src/i18n'
import { DeveloperPlaygroundPage } from '../../src/pages/DeveloperPlaygroundPage'

const overview = {
  serviceEnabled: true, requestsPerMinute: 3, maximumTextCharacters: 200, maximumTextUtf8Bytes: 2048,
  projects: ['one', 'two'].map(projectId => ({
    projectId, keyId: `key-${projectId}`, displayName: `Project ${projectId}`, status: 'active',
    voices: [{ voiceAlias: `voice-${projectId}`, status: 'active' }],
  })),
}
let requests: { resolve: (response: Response) => void; signal: AbortSignal }[]
const audioResponse = () => new Response('synthetic-audio', {
  headers: { 'Content-Type': 'audio/wav', 'X-Request-Id': 'synthetic-request', 'X-StoryVoice-Audio-Duration-Ms': '1000' },
})

beforeEach(() => {
  requests = []
  vi.stubGlobal('fetch', vi.fn((url: string, options?: RequestInit) => {
    if (url.endsWith('/overview')) return Promise.resolve(Response.json(overview))
    if (url.endsWith('/playground')) return new Promise<Response>(resolve => {
      requests.push({ resolve, signal: options!.signal! })
    })
    throw new Error(`Unexpected test request: ${url}`)
  }))
  let nextUrl = 0
  vi.stubGlobal('URL', class extends URL {
    static createObjectURL = vi.fn(() => `blob:synthetic-${++nextUrl}`)
    static revokeObjectURL = vi.fn()
  })
})

function Shell() {
  const navigate = useNavigate()
  return <>
    <button onClick={() => navigate('/?project=two')}>切換路由</button>
    <Outlet context={{ email: 'qa@example.invalid', csrfToken: 'test-csrf' }} />
  </>
}

async function openPlayground() {
  const view = render(<LocaleProvider><MemoryRouter initialEntries={['/?project=one']}><Routes>
    <Route element={<Shell />}><Route index element={<DeveloperPlaygroundPage />} /></Route>
  </Routes></MemoryRouter></LocaleProvider>)
  await screen.findByRole('button', { name: '產生語音' })
  return view
}

it('取消後立刻重試，舊的成功回應不能取代新音訊或解除新要求的等待狀態', async () => {
  const user = userEvent.setup()
  const { container } = await openPlayground()
  await user.click(screen.getByRole('button', { name: '產生語音' }))
  await user.click(screen.getByRole('button', { name: '取消', exact: true }))
  expect(requests[0].signal.aborted).toBe(true)
  await user.click(screen.getByRole('button', { name: '產生語音' }))
  await act(async () => requests[0].resolve(audioResponse()))
  expect(URL.createObjectURL).not.toHaveBeenCalled()
  expect(screen.getByRole('button', { name: '正在產生…' }).hasAttribute('disabled')).toBe(true)
  await act(async () => requests[1].resolve(audioResponse()))
  expect(container.querySelector('audio')?.getAttribute('src')).toBe('blob:synthetic-1')
})

it('音訊在編輯文字、重送與離開頁面時會回收 Blob URL', async () => {
  const user = userEvent.setup()
  const { unmount } = await openPlayground()
  await user.click(screen.getByRole('button', { name: '產生語音' }))
  await act(async () => requests[0].resolve(audioResponse()))
  await user.click(screen.getByRole('button', { name: '用相同冪等鍵重送' }))
  expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:synthetic-1')
  await act(async () => requests[1].resolve(audioResponse()))
  await user.type(screen.getByRole('textbox', { name: '試聽文字' }), '新的內容')
  expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:synthetic-2')
  await user.click(screen.getByRole('button', { name: '產生語音' }))
  await act(async () => requests[2].resolve(audioResponse()))
  unmount()
  expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:synthetic-3')
})

it('切換專案路由時中止合成，晚到的音訊不會顯示在另一個專案', async () => {
  const user = userEvent.setup()
  const { container } = await openPlayground()
  await user.click(screen.getByRole('button', { name: '產生語音' }))
  await user.click(screen.getByRole('button', { name: '切換路由' }))
  await screen.findByDisplayValue('Project two')
  expect(requests[0].signal.aborted).toBe(true)
  await act(async () => requests[0].resolve(audioResponse()))
  expect(container.querySelector('audio')).toBeNull()
  expect(URL.createObjectURL).not.toHaveBeenCalled()
})
