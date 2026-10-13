import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { expect, it, vi } from 'vitest'
import App from '../../src/App'
import { LocaleProvider } from '../../src/i18n'

function open(path:string) { return render(<LocaleProvider><MemoryRouter initialEntries={[path]}><App /></MemoryRouter></LocaleProvider>) }

it('public content renders while session is pending, and never requests private book data', async () => {
  let resolveSession: (value:Response)=>void = ()=>{}
  const request = vi.fn((url:string)=>url.endsWith('/auth/session') ? new Promise<Response>(r=>{resolveSession=r}) : Promise.resolve(Response.json([])))
  vi.stubGlobal('fetch',request)
  open('/voices')
  await screen.findByText('目前沒有可公開展示的聲線')
  expect(screen.getByRole('link',{name:'進入工作區',exact:true})).toBeTruthy()
  await act(async()=>resolveSession(Response.json({authenticated:true,email:'synthetic@example.test',csrfToken:'synthetic'})))
  expect(await screen.findByRole('button',{name:'登出',exact:true})).toBeTruthy()
  expect(screen.queryByText('synthetic@example.test')).toBeNull()
  expect(request.mock.calls.map(([url])=>url).sort()).toEqual(['/api/auth/session','/api/public/v1/voices'].sort())
  await userEvent.click(screen.getByRole('link',{name:'首頁',exact:true}))
  await screen.findByRole('heading',{level:1})
  expect(screen.getByRole('navigation',{name:'主要導覽'})).toBeTruthy()
  expect(screen.getByRole('button',{name:'工作區',exact:true})).toBeTruthy()
  expect(request.mock.calls.filter(([url])=>url.endsWith('/auth/session'))).toHaveLength(1)
})

it('anonymous navigation keeps the common header and guards the private workspace', async () => {
  const request = vi.fn((url:string)=>Promise.resolve(Response.json(url.endsWith('/auth/session')?{authenticated:false,email:null,csrfToken:'synthetic'}:[])))
  vi.stubGlobal('fetch',request)
  open('/')
  await screen.findByRole('heading',{level:1})
  await userEvent.click(screen.getByRole('button',{name:'工作區',exact:true}))
  await userEvent.click(screen.getByRole('link',{name:'角色管理',exact:true}))
  expect(await screen.findByRole('button',{name:'登入',exact:true})).toBeTruthy()
  expect(screen.getByRole('navigation',{name:'主要導覽'})).toBeTruthy()
  expect(request.mock.calls.some(([url])=>url.includes('character-profiles')||url.includes('/books'))).toBe(false)
})

it('sign-out clears private content across the shared session', async () => {
  let authenticated=true
  vi.stubGlobal('fetch',vi.fn((url:string)=>{
    if(url.endsWith('/auth/logout')) {authenticated=false;return Promise.resolve(Response.json({}))}
    return Promise.resolve(Response.json(url.endsWith('/auth/session')?{authenticated,email:authenticated?'synthetic@example.test':null,csrfToken:'synthetic'}:[]))
  }))
  open('/library')
  await screen.findByRole('heading',{name:'書庫',exact:true})
  await userEvent.click(screen.getByRole('button',{name:'登出',exact:true}))
  await waitFor(()=>expect(screen.queryByRole('heading',{name:'書庫',exact:true})).toBeNull())
  expect(await screen.findByRole('button',{name:'登入',exact:true})).toBeTruthy()
})

it('the same navigation continues through API docs, missing pages, and menu keyboard dismissal', async () => {
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(Response.json({ authenticated: false, email: null, csrfToken: 'synthetic' }))))
  open('/not-a-page')
  expect(screen.getByRole('heading', { name: '找不到這個頁面。' })).toBeTruthy()
  const workspace = screen.getByRole('button', { name: '工作區', exact: true })
  await userEvent.click(workspace)
  expect(workspace.getAttribute('aria-expanded')).toBe('true')
  await userEvent.keyboard('{Escape}')
  expect(workspace.getAttribute('aria-expanded')).toBe('false')
  expect(document.activeElement).toBe(workspace)
  await userEvent.click(screen.getByRole('link', { name: 'API 文件', exact: true }))
  expect(await screen.findByRole('heading', { name: '跨專案語音合成 API' })).toBeTruthy()
  expect(screen.getAllByRole('banner')).toHaveLength(1)
  expect(screen.getByRole('navigation', { name: '主要導覽' })).toBeTruthy()
  expect(screen.getByRole('button', { name: '工作區', exact: true }).getAttribute('aria-expanded')).toBe('false')
})
