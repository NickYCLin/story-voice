import { Link, NavLink } from 'react-router-dom'
import { localize, useLocale } from '../i18n'
import { LanguageSwitcher } from './LanguageSwitcher'
import { StoryVoiceBrand } from './StoryVoiceBrand'

// Public pages never depend on a session request. The workspace handles sign-in when needed.
export function PublicSiteHeader() {
  const { locale } = useLocale()
  const t = (zh: string, en: string) => localize(locale, zh, en)
  return <header className="public-site-header">
    <a className="public-skip-link" href="#main-content">{t('跳到主要內容', 'Skip to content')}</a>
    <div className="public-site-header-inner">
      <StoryVoiceBrand />
      <nav aria-label={t('公開頁面導覽', 'Public navigation')}>
        <NavLink end to="/">{t('首頁', 'Home')}</NavLink>
        <NavLink to="/voices">{t('聲線館', 'Voices')}</NavLink>
        <NavLink to="/developers/docs">{t('API 文件', 'API docs')}</NavLink>
      </nav>
      <div className="public-site-actions">
        <LanguageSwitcher />
        <Link className="secondary-button public-focus" to="/library">{t('進入工作區', 'Open workspace')} <span aria-hidden="true">↗</span></Link>
      </div>
    </div>
  </header>
}
