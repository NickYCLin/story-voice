import { useContext, useEffect, useId, useRef, useState } from 'react'
import { Link, NavLink, useLocation } from 'react-router-dom'
import { AuthSessionContext, type useAuthSession } from '../auth'
import { localize, useLocale } from '../i18n'
import { LanguageSwitcher } from './LanguageSwitcher'
import { StoryVoiceBrand } from './StoryVoiceBrand'

type Props = { session?: ReturnType<typeof useAuthSession>; contentId?: string }

// Same geometry and navigation on public and private pages. A public page never
// waits for session resolution; only the account action changes after it arrives.
export function PublicSiteHeader({ session, contentId = 'main-content' }: Props) {
  const sharedSession = useContext(AuthSessionContext)
  const currentSession = session ?? sharedSession
  const authenticated = currentSession?.authState.status === 'authenticated'
  const { locale } = useLocale()
  const t = (zh: string, en: string) => localize(locale, zh, en)
  const { pathname } = useLocation()
  const [openPath, setOpenPath] = useState<string | null>(null)
  const open = openPath === pathname
  const menuId = useId()
  const menuButton = useRef<HTMLButtonElement>(null)
  const menu = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!open) return
    const closeOutside = (event: PointerEvent) => {
      if (event.target instanceof Node && !menu.current?.contains(event.target) && !menuButton.current?.contains(event.target)) setOpenPath(null)
    }
    document.addEventListener('pointerdown', closeOutside)
    return () => document.removeEventListener('pointerdown', closeOutside)
  }, [open])

  return <header className="site-header public-site-header" onKeyDown={event => {
    if (event.key === 'Escape' && open) { setOpenPath(null); menuButton.current?.focus({ preventScroll: true }) }
  }}>
    <a className="public-skip-link" href={`#${contentId}`}>{t('跳到主要內容', 'Skip to content')}</a>
    <div className="site-header-inner">
      <StoryVoiceBrand />
      <nav className="site-primary-nav" aria-label={t('主要導覽', 'Primary navigation')}>
        <NavLink end to="/">{t('首頁', 'Home')}</NavLink>
        <NavLink to="/voices">{t('公開聲線', 'Voices')}</NavLink>
        <NavLink to="/developers/docs">{t('API 文件', 'API docs')}</NavLink>
      </nav>
      <div className="site-account-actions">
        <LanguageSwitcher />
        <div className="site-session-action">
          {authenticated
            ? <button className="secondary-button" onClick={() => void currentSession?.logout()} type="button">{t('登出', 'Sign out')}</button>
            : <Link className="secondary-button" to="/library">{t('進入工作區', 'Open workspace')}</Link>}
        </div>
      </div>
      <div className="site-workspace-menu" ref={menu}>
        <button aria-controls={menuId} aria-expanded={open} className="site-menu-button" ref={menuButton} onClick={() => setOpenPath(open ? null : pathname)} type="button">
          {t('工作區', 'Workspace')} <span aria-hidden="true">{open ? '−' : '＋'}</span>
        </button>
        <div className="site-menu-panel" hidden={!open} id={menuId} onClick={event => {
          if ((event.target as HTMLElement).closest('a')) setOpenPath(null)
        }}>
          <p>{t('故事工作區', 'Story workspace')}</p>
          <nav aria-label={t('工作區導覽', 'Workspace navigation')}>
            <NavLink to="/library">{t('書庫', 'Library')}</NavLink>
            <NavLink to="/collections">{t('書冊', 'Collections')}</NavLink>
            <NavLink to="/characters">{t('角色管理', 'Characters')}</NavLink>
            <NavLink to="/series">{t('系列配音', 'Series')}</NavLink>
            <NavLink to="/developer">{t('開發者', 'Developer')}</NavLink>
            <NavLink to="/shared">{t('分享給我的', 'Shared with me')}</NavLink>
          </nav>
          <Link to="/about">{t('關於 StoryVoice', 'About StoryVoice')}</Link>
          <a href="https://github.com/NickYCLin/story-voice" rel="noreferrer" target="_blank">GitHub ↗</a>
        </div>
      </div>
    </div>
  </header>
}
