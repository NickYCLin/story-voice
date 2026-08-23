import { NavLink, Outlet } from 'react-router-dom'

import { AuthScreen } from './AuthScreen'
import { useAuthSession } from './auth'
import type { AuthedOutletContext } from './authOutletContext'

const navLinkClassName = ({ isActive }: { isActive: boolean }) =>
  `rounded-full px-4 py-2 text-sm transition ${isActive
    ? 'border border-amber-300 bg-amber-50 text-amber-800'
    : 'border border-transparent text-stone-500 hover:border-stone-200 hover:text-stone-800'}`

export function AppLayout() {
  const { authState, loadAuthSession, logout } = useAuthSession()

  if (authState.status === 'loading') {
    return <main className="grid min-h-screen place-items-center bg-[#faf6ee] text-stone-500">正在確認 StoryVoice 登入狀態…</main>
  }

  if (authState.status === 'error') {
    return <main className="grid min-h-screen place-items-center bg-[#faf6ee] px-6 text-center text-rose-700">無法連接登入服務，請重新整理頁面。</main>
  }

  if (authState.status === 'anonymous') {
    return <AuthScreen csrfToken={authState.csrfToken} onAuthenticated={loadAuthSession} />
  }

  return (
    <div className="relative min-h-screen overflow-hidden bg-[#faf6ee] text-[#332a1f]">
      <div className="ambient ambient-one" aria-hidden="true" />
      <div className="ambient ambient-two" aria-hidden="true" />

      <header className="relative z-10 mx-auto flex max-w-7xl flex-wrap items-center justify-between gap-4 px-6 py-6 lg:px-10">
        <NavLink className="group flex items-center gap-3" to="/" aria-label="StoryVoice 首頁">
          <span className="grid h-11 w-11 place-items-center rounded-2xl border border-amber-300 bg-amber-50 font-serif text-lg text-amber-800 shadow-[0_4px_18px_rgba(180,101,15,.14)] transition group-hover:border-amber-400">
            SV
          </span>
          <span>
            <strong className="block font-serif text-lg tracking-wide text-stone-900">StoryVoice</strong>
            <span className="block text-[10px] uppercase tracking-[.26em] text-stone-500">AI Story Director</span>
          </span>
        </NavLink>

        <nav aria-label="主要導覽" className="flex flex-wrap items-center gap-1">
          <NavLink className={navLinkClassName} end to="/">首頁</NavLink>
          <NavLink className={navLinkClassName} to="/library">書庫</NavLink>
          <NavLink className={navLinkClassName} to="/collections">書冊</NavLink>
          <NavLink className={navLinkClassName} to="/characters">角色管理</NavLink>
          <NavLink className={navLinkClassName} to="/voices">公開聲線</NavLink>
          <NavLink className={navLinkClassName} to="/developers/docs">API 文件</NavLink>
          <NavLink className={navLinkClassName} to="/developer">開發者</NavLink>
          <NavLink className={navLinkClassName} to="/shared">分享給我的</NavLink>
        </nav>

        <div className="flex items-center gap-4">
          <span className="hidden max-w-52 truncate text-xs text-stone-500 md:inline">{authState.email}</span>
          <button className="rounded-full border border-stone-200 px-4 py-2 text-sm text-stone-700 transition hover:border-rose-300 hover:text-rose-700" onClick={() => void logout()} type="button">登出</button>
          <a
            className="rounded-full border border-stone-200 bg-white px-4 py-2 text-sm text-stone-700 transition hover:border-amber-300 hover:bg-amber-50"
            href="https://github.com/NickYCLin/story-voice"
            rel="noreferrer"
            target="_blank"
          >
            GitHub ↗
          </a>
        </div>
      </header>

      <Outlet context={{ email: authState.email, csrfToken: authState.csrfToken } satisfies AuthedOutletContext} />

      <footer className="relative z-10 border-t border-stone-200 px-6 py-8 text-center text-xs leading-6 text-stone-400">
        StoryVoice is open source. No DRM circumvention. Process only content you have the right to use.
      </footer>
    </div>
  )
}
