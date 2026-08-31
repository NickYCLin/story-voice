import { useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'

import {
  fetchDeveloperVoiceOverview,
  formatUtc,
  PROJECT_STATUS_CLASS,
  projectStatusLabel,
  tierLabel,
} from '../developerVoiceConsole'
import { localize, useLocale, type SupportedLocale } from '../i18n'
import type {
  DeveloperVoiceConsoleOverview,
  DeveloperVoiceProjectSummary,
} from '../developerVoiceConsole'

type PageState =
  | { status: 'loading' }
  | { status: 'error' }
  | { status: 'not-found' }
  | {
    status: 'ready'
    overview: DeveloperVoiceConsoleOverview
    project: DeveloperVoiceProjectSummary
  }

const remainingLabel = (expiresAtUtc: string, locale: SupportedLocale) => {
  const expiresAt = new Date(expiresAtUtc).getTime()
  if (Number.isNaN(expiresAt)) return localize(locale, '無法計算', 'Unavailable')

  const remainingDays = Math.ceil((expiresAt - Date.now()) / 86_400_000)
  if (remainingDays <= 0) return localize(locale, '已到期', 'Expired')
  if (remainingDays === 1) return localize(locale, '剩餘 1 天', '1 day remaining')
  return localize(locale, `剩餘 ${remainingDays} 天`, `${remainingDays} days remaining`)
}

export function DeveloperProjectPage() {
  const { locale, numberLocale } = useLocale()
  const t = (zh: string, en: string) => localize(locale, zh, en)
  const { projectId = '' } = useParams()
  const [state, setState] = useState<PageState>({ status: 'loading' })
  const [loadedProjectId, setLoadedProjectId] = useState<string | null>(null)
  const requestSequenceRef = useRef(0)
  const routeTransitioning = loadedProjectId !== projectId

  useEffect(() => {
    const requestSequence = requestSequenceRef.current + 1
    requestSequenceRef.current = requestSequence
    const controller = new AbortController()
    setState({ status: 'loading' })
    fetchDeveloperVoiceOverview(controller.signal)
      .then((overview) => {
        if (controller.signal.aborted || requestSequenceRef.current !== requestSequence) return
        const project = overview.projects.find((candidate) =>
          candidate.projectId === projectId || candidate.keyId === projectId)
        setState(project
          ? { status: 'ready', overview, project }
          : { status: 'not-found' })
        setLoadedProjectId(projectId)
      })
      .catch((error: unknown) => {
        if (error instanceof DOMException && error.name === 'AbortError') return
        if (controller.signal.aborted || requestSequenceRef.current !== requestSequence) return
        setState({ status: 'error' })
        setLoadedProjectId(projectId)
      })
    return () => {
      controller.abort()
      if (requestSequenceRef.current === requestSequence) {
        requestSequenceRef.current += 1
      }
    }
  }, [projectId])

  if (routeTransitioning || state.status === 'loading') {
    return (
      <main className="library-state mx-auto my-12 max-w-7xl">
        <span role="status">{loadedProjectId === null
          ? t('正在讀取專案詳情…', 'Loading project details…')
          : t('正在切換 API 專案…', 'Switching API projects…')}</span>
      </main>
    )
  }

  if (state.status === 'error') {
    return (
      <main className="mx-auto max-w-7xl px-6 py-12 lg:px-10">
        <div className="library-state border-rose-300 text-rose-700" role="alert">{t('專案詳情讀取失敗，請重新整理頁面。', 'We could not load this project. Refresh the page to try again.')}</div>
      </main>
    )
  }

  if (state.status === 'not-found') {
    return (
      <main className="mx-auto max-w-7xl px-6 py-12 lg:px-10">
        <div className="library-state min-h-52">
          <div>
            <h1 className="font-serif text-2xl text-stone-800">{t('找不到這個 API 專案。', 'API project not found.')}</h1>
            <p className="mt-3 text-sm text-stone-500">{t('專案不存在，或目前登入的帳號沒有檢視權限。', 'The project does not exist, or your account does not have access to it.')}</p>
            <Link className="mt-5 inline-flex font-semibold text-amber-800 underline" to="/developer">{t('返回開發者總覽', 'Back to developer overview')}</Link>
          </div>
        </div>
      </main>
    )
  }

  const { overview, project } = state
  const credentialLabel = `${project.tokenPrefix}${project.keyId}.…`

  return (
    <main className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
      <Link className="text-sm font-semibold text-amber-800 underline" to="/developer">← {t('返回開發者總覽', 'Back to developer overview')}</Link>

      <header className="mt-7 flex flex-wrap items-start justify-between gap-5">
        <div>
          <p className="eyebrow">Developer project</p>
          <h1 className="mt-3 font-serif text-4xl tracking-tight sm:text-5xl">
            {project.displayName || project.projectId || project.keyId}
          </h1>
          <p className="mt-3 text-sm text-stone-500">{t('專案 ID：', 'Project ID: ')}<code>{project.projectId || '—'}</code></p>
        </div>
        <span className={`rounded-full border px-4 py-2 text-sm ${PROJECT_STATUS_CLASS[project.status]}`}>
          {projectStatusLabel(project.status, locale)}
        </span>
      </header>

      {!overview.serviceEnabled && (
        <div className="mt-8 rounded-2xl border border-amber-300 bg-amber-50 px-5 py-4 text-sm leading-6 text-amber-900">
          {t('合成聲線 API 服務目前未啟用；這份頁面只代表已登錄的專案與授權資料，實際呼叫會得到 404。', 'The synthetic voice API is currently disabled. This page only reflects registered project and grant data; API calls will return 404.')}
        </div>
      )}

      <section className="mt-8 grid grid-cols-1 gap-4 lg:grid-cols-3" aria-label={t('專案摘要', 'Project summary')}>
        <article className="rounded-2xl border border-stone-200 bg-white/80 p-6">
          <h2 className="text-xs uppercase tracking-[.2em] text-stone-400">{t('存取層級', 'Access tier')}</h2>
          <p className="mt-3 text-sm font-semibold text-stone-800">{tierLabel(project.accessTier, locale)}</p>
          <p className="mt-2 text-xs leading-5 text-stone-500">Consumer: <code>{project.consumerFamilyId || t('未提供', 'Not provided')}</code></p>
          {project.territoryCountryCode && <p className="mt-1 text-xs text-stone-500">{t('授權地區：', 'Authorized territory: ')}{project.territoryCountryCode}</p>}
        </article>

        <article className="rounded-2xl border border-stone-200 bg-white/80 p-6">
          <h2 className="text-xs uppercase tracking-[.2em] text-stone-400">{t('有效期間', 'Access window')}</h2>
          <dl className="mt-3 space-y-2 text-sm text-stone-700">
            <div><dt className="inline text-stone-400">{t('開始：', 'Starts: ')}</dt><dd className="inline">{formatUtc(project.effectiveAtUtc, locale)}</dd></div>
            <div><dt className="inline text-stone-400">{t('到期：', 'Expires: ')}</dt><dd className="inline">{formatUtc(project.expiresAtUtc, locale)}</dd></div>
          </dl>
          <p className="mt-3 text-xs font-semibold text-amber-800">{remainingLabel(project.expiresAtUtc, locale)}</p>
        </article>

        <article className="rounded-2xl border border-stone-200 bg-white/80 p-6">
          <h2 className="text-xs uppercase tracking-[.2em] text-stone-400">{t('共用限制', 'Shared limits')}</h2>
          <dl className="mt-3 space-y-2 text-sm text-stone-700">
            <div><dt className="inline text-stone-400">{t('速率：', 'Rate: ')}</dt><dd className="inline">{localize(locale, `每分鐘 ${overview.requestsPerMinute.toLocaleString(numberLocale)} 次`, `${overview.requestsPerMinute.toLocaleString(numberLocale)} requests/minute`)}</dd></div>
            <div><dt className="inline text-stone-400">{t('文字：', 'Text: ')}</dt><dd className="inline">{localize(locale, `${overview.maximumTextCharacters.toLocaleString(numberLocale)} 字`, `${overview.maximumTextCharacters.toLocaleString(numberLocale)} characters`)}</dd></div>
            <div><dt className="inline text-stone-400">{t('UTF-8：', 'UTF-8: ')}</dt><dd className="inline">{overview.maximumTextUtf8Bytes.toLocaleString(numberLocale)} bytes</dd></div>
          </dl>
        </article>
      </section>

      <div className="mt-6 grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1.4fr)_minmax(19rem,.6fr)]">
        <section className="rounded-2xl border border-stone-200 bg-white/80 p-6" aria-labelledby="project-voices-heading">
          <h2 className="font-serif text-2xl text-stone-900" id="project-voices-heading">{t('已授權聲線', 'Authorized voices')}</h2>
          <p className="mt-2 text-sm leading-6 text-stone-500">{t('撤銷狀態會在每次正式 API 呼叫前重新驗證。', 'Revocation status is checked again before every API synthesis request.')}</p>

          {project.voices.length === 0 && <div className="library-state mt-5 min-h-36">{t('這個專案還沒有授權任何聲線。', 'No voices are authorized for this project yet.')}</div>}
          {project.voices.length > 0 && (
            <ul className="mt-5 divide-y divide-stone-100">
              {project.voices.map((voice) => (
                <li className="flex flex-wrap items-center justify-between gap-3 py-4" key={voice.voiceAlias}>
                  <div>
                    <code className="text-sm text-stone-800">{voice.voiceAlias}</code>
                    {voice.revokedAtUtc && <p className="mt-1 text-xs text-stone-400">{t('撤銷時間：', 'Revoked at: ')}{formatUtc(voice.revokedAtUtc, locale)}</p>}
                  </div>
                  {voice.status === 'revoked'
                    ? <span className="rounded-full border border-rose-300 bg-rose-50 px-3 py-1 text-xs text-rose-700">{t('已撤銷', 'Revoked')}</span>
                    : <span className="rounded-full border border-emerald-300 bg-emerald-50 px-3 py-1 text-xs text-emerald-700">{t('可使用', 'Available')}</span>}
                </li>
              ))}
            </ul>
          )}
        </section>

        <aside className="space-y-6">
          <section className="rounded-2xl border border-stone-200 bg-white/80 p-6" aria-labelledby="credential-heading">
            <h2 className="font-serif text-2xl text-stone-900" id="credential-heading">{t('Credential 摘要', 'Credential summary')}</h2>
            <p className="mt-4 text-xs text-stone-400">{t('金鑰識別', 'Key identifier')}</p>
            <code className="mt-1 block break-all text-sm text-stone-800">{credentialLabel}</code>
            <p className="mt-4 text-xs text-stone-400">{t('用量與活動', 'Usage and activity')}</p>
            <Link
              className="mt-1 inline-flex text-sm font-semibold text-amber-800 underline"
              to={`/developer/usage?project=${encodeURIComponent(project.projectId || project.keyId)}`}
            >
              {t('查看這個專案的用量與活動 →', 'View this project’s usage and activity →')}
            </Link>
            <p className="mt-4 text-xs leading-5 text-stone-500">
              {t('完整 secret 不會在頁面重新顯示。受管金鑰可自行建立、換發與撤銷；既有設定檔金鑰仍由維運流程管理。', 'Complete secrets are never shown again. You can create, rotate, and revoke managed keys; deployment-configured keys remain under the operations workflow.')}
            </p>
            <Link
              className="mt-4 inline-flex font-semibold text-amber-800 underline"
              to={`/developer/credentials?project=${encodeURIComponent(project.projectId || project.keyId)}`}
            >
              {t('管理 API 金鑰 →', 'Manage API keys →')}
            </Link>
          </section>

          <section className="rounded-2xl border border-stone-200 bg-stone-900 p-6 text-stone-100">
            <h2 className="font-serif text-2xl">{t('快速開始', 'Quick start')}</h2>
            <ol className="mt-4 list-decimal space-y-3 pl-5 text-sm leading-6 text-stone-300">
              <li>{t('閱讀 request、header 與錯誤碼契約。', 'Review the request, header, and error-code contract.')}</li>
              <li>{t('把既有 credential 放在呼叫端的 server-side secret store。', 'Store your credential in the calling project’s server-side secret store.')}</li>
              <li>{localize(locale, <>以授權聲線 alias 呼叫 <code className="text-xs text-amber-200">POST /api/external/v1/speech</code>。</>, <>Call <code className="text-xs text-amber-200">POST /api/external/v1/speech</code> with an authorized voice alias.</>)}</li>
            </ol>
            <Link className="mt-5 inline-flex font-semibold text-amber-200 underline" to="/developers/docs">{t('查看 API 文件 →', 'View API docs →')}</Link>
            <Link className="mt-3 block font-semibold text-amber-200 underline" to={`/developer/playground?project=${encodeURIComponent(project.projectId || project.keyId)}`}>{t('前往 API Playground →', 'Open API Playground →')}</Link>
          </section>
        </aside>
      </div>
    </main>
  )
}
