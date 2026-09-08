import { Component, Suspense, type ReactNode } from 'react'
import { useLocation } from 'react-router-dom'
import { localize, useLocale } from '../i18n'

type BoundaryProps = { children: ReactNode; fallback: ReactNode; resetKey: string }
type BoundaryState = { failed: boolean; resetKey: string }

class RouteErrorBoundary extends Component<BoundaryProps, BoundaryState> {
  state: BoundaryState = { failed: false, resetKey: this.props.resetKey }

  static getDerivedStateFromError() {
    return { failed: true }
  }

  static getDerivedStateFromProps(props: BoundaryProps, state: BoundaryState) {
    return props.resetKey === state.resetKey ? null : { failed: false, resetKey: props.resetKey }
  }

  render() {
    return this.state.failed ? this.props.fallback : this.props.children
  }
}

export function RouteContent({ children }: { children: ReactNode }) {
  const { pathname, search } = useLocation()
  const { locale } = useLocale()
  const t = (zh: string, en: string) => localize(locale, zh, en)
  const frame = 'relative z-10 mx-auto w-full max-w-7xl px-6 py-12 lg:px-10'
  return (
    <RouteErrorBoundary resetKey={`${pathname}${search}`} fallback={
      <div className={frame}>
        <div className="rounded-2xl border border-stone-200 bg-white p-6">
          <p className="text-sm leading-6 text-stone-700" role="alert">{t('頁面暫時無法載入，請重新整理後再試一次。', 'This page could not load. Refresh the page to try again.')}</p>
          <button className="secondary-button mt-4" onClick={() => window.location.reload()} type="button">{t('重新整理頁面', 'Refresh page')}</button>
        </div>
      </div>
    }>
      <Suspense fallback={<div className={frame}><p className="text-sm text-stone-600" role="status">{t('頁面載入中…', 'Loading page…')}</p></div>}>
        {children}
      </Suspense>
    </RouteErrorBoundary>
  )
}
