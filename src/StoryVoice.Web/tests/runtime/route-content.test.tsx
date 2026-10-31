import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { lazy, useState, type ReactNode } from 'react'
import { Link, MemoryRouter, Route, Routes } from 'react-router-dom'
import { expect, it, vi } from 'vitest'
import { AppLayout } from '../../src/AppLayout'
import { RouteContent } from '../../src/components/RouteContent'
import { LocaleProvider } from '../../src/i18n'

function Shell({ children }: { children: ReactNode }) {
  return <LocaleProvider><MemoryRouter initialEntries={['/slow']}>
    <nav><Link to="/other">其他頁面</Link></nav>
    <RouteContent>{children}</RouteContent>
  </MemoryRouter></LocaleProvider>
}

it('keeps navigation visible while the requested page module is loading', async () => {
  function Ready() { return <p>頁面已就緒</p> }
  let complete: (module: { default: typeof Ready }) => void = () => {}
  const Slow = lazy(() => new Promise<{ default: typeof Ready }>(resolve => { complete = resolve }))
  render(<Shell><Slow /></Shell>)
  expect(screen.getByRole('status').textContent).toBe('頁面載入中…')
  expect(screen.getByRole('link', { name: '其他頁面' })).toBeTruthy()
  await act(async () => complete({ default: Ready }))
  expect(await screen.findByText('頁面已就緒')).toBeTruthy()
  expect(screen.queryByRole('status')).toBeNull()
})

it('shows a safe retry message when an import fails and can navigate to another page', async () => {
  vi.spyOn(console, 'error').mockImplementation(() => {})
  const Broken = lazy(() => Promise.reject(new Error('synthetic-module-diagnostic')))
  render(<Shell><Routes>
    <Route path="/slow" element={<Broken />} />
    <Route path="/other" element={<p>另一頁已就緒</p>} />
  </Routes></Shell>)
  expect((await screen.findByRole('alert')).textContent).toContain('頁面暫時無法載入')
  expect(screen.getByRole('button', { name: '重新整理頁面' })).toBeTruthy()
  expect(screen.queryByText('synthetic-module-diagnostic')).toBeNull()
  await userEvent.click(screen.getByRole('link', { name: '其他頁面' }))
  expect(await screen.findByText('另一頁已就緒')).toBeTruthy()
  expect(screen.queryByRole('alert')).toBeNull()
})

it('does not remount healthy page content just because the route changes', async () => {
  function StatefulPage() {
    const [count, setCount] = useState(0)
    return <button type="button" onClick={() => setCount(count + 1)}>保留狀態 {count}</button>
  }
  render(<Shell><StatefulPage /></Shell>)
  await userEvent.click(screen.getByRole('button', { name: '保留狀態 0' }))
  await userEvent.click(screen.getByRole('link', { name: '其他頁面' }))
  expect(screen.getByRole('button', { name: '保留狀態 1' })).toBeTruthy()
})

it('does not import a private page before the account is authenticated', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json({ authenticated: false, email: null, csrfToken: 'synthetic-csrf' })))
  const load = vi.fn().mockResolvedValue({ default: () => <p>私人頁面</p> })
  const PrivatePage = lazy(load)
  render(<LocaleProvider><MemoryRouter initialEntries={['/private']}><Routes>
    <Route element={<AppLayout />}><Route path="/private" element={<PrivatePage />} /></Route>
  </Routes></MemoryRouter></LocaleProvider>)
  await screen.findByRole('button', { name: '登入', exact: true })
  expect(load).not.toHaveBeenCalled()
  expect(screen.queryByText('私人頁面')).toBeNull()
})
