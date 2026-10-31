import { Outlet } from 'react-router-dom'
import { AuthScreen } from './AuthScreen'
import { useAuthSession } from './auth'
import type { AuthedOutletContext } from './authOutletContext'
import { PublicSiteHeader } from './components/PublicSiteHeader'
import { RouteContent } from './components/RouteContent'
import { localize, useLocale } from './i18n'

export function AppLayout() {
  const { authState, loadAuthSession, logout } = useAuthSession()
  const { locale, isEnglish } = useLocale()
  const t = (zh: string, en: string) => localize(locale, zh, en)

  if (authState.status === 'loading') {
    return <><PublicSiteHeader /><main id="main-content" tabIndex={-1} className="site-page grid min-h-[60svh] place-items-center text-stone-500"><span role="status">{t('正在確認 StoryVoice 登入狀態…', 'Checking your StoryVoice session…')}</span></main></>
  }

  if (authState.status === 'error') {
    return <><PublicSiteHeader /><main id="main-content" tabIndex={-1} className="site-page grid min-h-[60svh] place-items-center text-center text-rose-700"><span role="alert">{t('無法連接登入服務，請重新整理頁面。', 'StoryVoice could not reach the sign-in service. Refresh the page to try again.')}</span></main></>
  }

  if (authState.status === 'anonymous') {
    return <AuthScreen csrfToken={authState.csrfToken} onAuthenticated={loadAuthSession} />
  }

  return (
    <div className="workspace-shell">
      <PublicSiteHeader session={{ authState, loadAuthSession, logout }} contentId="workspace-content" />
      <div className="workspace-body" id="workspace-content" tabIndex={-1}>
        {isEnglish && <aside className="site-language-note" role="note">
          <p>The library, character, and narration production workspace is currently Taiwan-Mandarin-first. English navigation is available while some production labels and voice options remain in Traditional Chinese.</p>
        </aside>}
        <RouteContent>
          <Outlet context={{ email: authState.email, csrfToken: authState.csrfToken } satisfies AuthedOutletContext} />
        </RouteContent>
        <footer className="site-footer">
          {t('StoryVoice 是開源軟體，不提供 DRM 規避功能。僅處理你擁有或獲得授權使用的內容。', 'StoryVoice is open source. No DRM circumvention. Process only content you have the right to use.')}
        </footer>
      </div>
    </div>
  )
}
