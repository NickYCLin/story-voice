import { NavLink, Outlet } from 'react-router-dom'

import { AuthScreen } from './AuthScreen'
import { useAuthSession } from './auth'
import type { AuthedOutletContext } from './authOutletContext'
import { LanguageSwitcher } from './components/LanguageSwitcher'
import { localize, useLocale } from './i18n'

const navLinkClassName = ({ isActive }: { isActive: boolean }) =>
  `rounded-full px-4 py-2 text-sm transition ${isActive
    ? 'border border-amber-300 bg-amber-50 text-amber-800'
    : 'border border-transparent text-stone-500 hover:border-stone-200 hover:text-stone-800'}`

export function AppLayout() {
  const { authState, loadAuthSession, logout } = useAuthSession()
  const { locale, isEnglish } = useLocale()
  const t = (zh: string, en: string) => localize(locale, zh, en)

  if (authState.status === 'loading') {
    return <main className="grid min-h-screen place-items-center bg-[#faf6ee] text-stone-500"><span role="status">{t('正在確認 StoryVoice 登入狀態…', 'Checking your StoryVoice session…')}</span></main>
  }

  if (authState.status === 'error') {
    return <main className="grid min-h-screen place-items-center bg-[#faf6ee] px-6 text-center text-rose-700"><span role="alert">{t('無法連接登入服務，請重新整理頁面。', 'StoryVoice could not reach the sign-in service. Refresh the page to try again.')}</span></main>
  }

  if (authState.status === 'anonymous') {
    return <AuthScreen csrfToken={authState.csrfToken} onAuthenticated={loadAuthSession} />
  }

  return (
    <div className="relative min-h-screen overflow-hidden bg-[#faf6ee] text-[#332a1f]">
      <div className="ambient ambient-one" aria-hidden="true" />
      <div className="ambient ambient-two" aria-hidden="true" />

      <header className="relative z-10 mx-auto flex max-w-7xl flex-wrap items-center justify-between gap-4 px-6 py-6 lg:px-10">
        <NavLink className="group flex items-center gap-3" to="/" aria-label={t('StoryVoice 首頁', 'StoryVoice home')}>
          <span className="grid h-11 w-11 place-items-center rounded-2xl border border-amber-300 bg-amber-50 font-serif text-lg text-amber-800 shadow-[0_4px_18px_rgba(180,101,15,.14)] transition group-hover:border-amber-400">
            SV
          </span>
          <span>
            <strong className="block font-serif text-lg tracking-wide text-stone-900">StoryVoice</strong>
            <span className="block text-[10px] uppercase tracking-[.26em] text-stone-500">{t('AI 故事導演', 'AI Story Director')}</span>
          </span>
        </NavLink>

        <nav aria-label={t('主要導覽', 'Primary navigation')} className="flex flex-wrap items-center gap-1">
          <NavLink className={navLinkClassName} end to="/">{t('首頁', 'Home')}</NavLink>
          <NavLink className={navLinkClassName} to="/library">{t('書庫', 'Library')}</NavLink>
          <NavLink className={navLinkClassName} to="/collections">{t('書冊', 'Collections')}</NavLink>
          <NavLink className={navLinkClassName} to="/characters">{t('角色管理', 'Characters')}</NavLink>
          <NavLink className={navLinkClassName} to="/series">{t('系列配音', 'Series')}</NavLink>
          <NavLink className={navLinkClassName} to="/voices">{t('公開聲線', 'Voices')}</NavLink>
          <NavLink className={navLinkClassName} to="/developers/docs">{t('API 文件', 'API docs')}</NavLink>
          <NavLink className={navLinkClassName} to="/developer">{t('開發者', 'Developer')}</NavLink>
          <NavLink className={navLinkClassName} to="/shared">{t('分享給我的', 'Shared with me')}</NavLink>
        </nav>

        <div className="flex flex-wrap items-center justify-end gap-3">
          <LanguageSwitcher />
          <span className="hidden max-w-52 truncate text-xs text-stone-500 md:inline">{authState.email}</span>
          <button className="rounded-full border border-stone-200 px-4 py-2 text-sm text-stone-700 transition hover:border-rose-300 hover:text-rose-700" onClick={() => void logout()} type="button">{t('登出', 'Sign out')}</button>
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

      {isEnglish && (
        <aside className="relative z-10 mx-auto mb-2 max-w-7xl px-6 lg:px-10" role="note">
          <p className="rounded-2xl border border-amber-200 bg-amber-50/85 px-4 py-3 text-xs leading-5 text-amber-950">
            The library, character, and narration production workspace is currently Taiwan-Mandarin-first. English navigation is available while some production labels and voice options remain in Traditional Chinese.
          </p>
        </aside>
      )}

      <Outlet context={{ email: authState.email, csrfToken: authState.csrfToken } satisfies AuthedOutletContext} />

      <footer className="relative z-10 border-t border-stone-200 px-6 py-8 text-center text-xs leading-6 text-stone-400">
        {t(
          'StoryVoice 是開源軟體，不提供 DRM 規避功能。僅處理你擁有或獲得授權使用的內容。',
          'StoryVoice is open source. No DRM circumvention. Process only content you have the right to use.',
        )}
      </footer>
    </div>
  )
}
