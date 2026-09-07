import { act, render, screen } from '@testing-library/react'
import { MemoryRouter, Outlet, Route, Routes, useNavigate } from 'react-router-dom'
import userEvent from '@testing-library/user-event'
import { beforeEach, expect, it, vi } from 'vitest'

import { LibraryPage } from '../../src/pages/LibraryPage'

vi.mock('../../src/BookInsightsPanel', () => ({ BookInsightsPanel: () => <p>閱讀筆記面板</p> }))
vi.mock('../../src/NarrationPanel', () => ({ NarrationPanel: () => <p>朗讀面板</p> }))
vi.mock('../../src/LibraryStatusMatrix', () => ({ default: () => <p>書庫狀態</p> }))

const books = ['one', 'two'].map(id => ({
  id, title: `故事 ${id}`, author: '測試作者', language: 'zh-TW', fileType: 'txt', status: 'Ready',
  chapterCount: 1, createdAt: '2026-01-01T00:00:00Z', authorizedTextAvailable: true,
}))
const details = (index: number) => ({ ...books[index], originalFileName: 'story.txt', chapters: [] })
let completeFirst: ((response: Response) => void) | undefined
let delayFirst = false

beforeEach(() => {
  delayFirst = false
  completeFirst = undefined
  vi.stubGlobal('fetch', vi.fn((url: string) => {
    if (url.endsWith('/books')) return Promise.resolve(Response.json(books))
    if (url.endsWith('/books/one') && delayFirst) return new Promise<Response>(resolve => { completeFirst = resolve })
    return Promise.resolve(Response.json(details(url.endsWith('/two') ? 1 : 0)))
  }))
})

function Shell() {
  const navigate = useNavigate()
  return <><button onClick={() => navigate('/library/two')}>換第二本</button><Outlet context={{ csrfToken: 'synthetic-csrf' }} /></>
}

function openLibrary() {
  return render(<MemoryRouter initialEntries={['/library/one']}><Routes>
    <Route element={<Shell />}><Route path="library/:bookId" element={<LibraryPage />} /></Route>
  </Routes></MemoryRouter>)
}

it('書籍詳情同時顯示筆記與朗讀面板，不產生重複 React key', async () => {
  const errors = vi.spyOn(console, 'error').mockImplementation(() => {})
  openLibrary()
  await screen.findByText('閱讀筆記面板')
  expect(screen.getByText('朗讀面板')).toBeTruthy()
  expect(errors.mock.calls.filter(call => String(call[0]).includes('same key'))).toHaveLength(0)
})

it('快速換書後，第一本晚到的詳情不能取代目前選書', async () => {
  delayFirst = true
  const user = userEvent.setup()
  openLibrary()
  await screen.findByRole('button', { name: /故事 one/ })
  await user.click(screen.getByRole('button', { name: '換第二本' }))
  await screen.findByRole('heading', { name: '故事 two' })
  await act(async () => completeFirst!(Response.json(details(0))))
  expect(screen.getByRole('heading', { name: '故事 two' })).toBeTruthy()
  expect(screen.queryByRole('heading', { name: '故事 one' })).toBeNull()
})
