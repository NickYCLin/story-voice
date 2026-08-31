import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

import {
  fetchDeveloperVoiceOverview,
  fetchDeveloperVoiceUsage,
  formatUtc,
  PROJECT_STATUS_CLASS,
  projectStatusLabel,
  tierLabel,
} from '../developerVoiceConsole'
import type {
  DeveloperVoiceConsoleOverview,
  DeveloperVoiceUsageReport,
} from '../developerVoiceConsole'
import { localize, useLocale } from '../i18n'

type LoadState = 'loading' | 'ready' | 'error'

export function DeveloperConsolePage() {
  const { locale, numberLocale } = useLocale()
  const t = (zh: string, en: string) => localize(locale, zh, en)
  const [overview, setOverview] = useState<DeveloperVoiceConsoleOverview | null>(null)
  const [state, setState] = useState<LoadState>('loading')
  const [usage, setUsage] = useState<DeveloperVoiceUsageReport | null>(null)
  const [usageState, setUsageState] = useState<LoadState>('loading')

  useEffect(() => {
    const controller = new AbortController()
    const toUtc = new Date()
    const fromUtc = new Date(toUtc.getTime() - 24 * 60 * 60 * 1000)

    fetchDeveloperVoiceOverview(controller.signal)
      .then((value) => {
        setOverview(value)
        setState('ready')
      })
      .catch((error: unknown) => {
        if (error instanceof DOMException && error.name === 'AbortError') return
        setState('error')
      })

    fetchDeveloperVoiceUsage({
      fromUtc: fromUtc.toISOString(),
      toUtc: toUtc.toISOString(),
      limit: 1,
    }, controller.signal)
      .then((value) => {
        setUsage(value)
        setUsageState('ready')
      })
      .catch((error: unknown) => {
        if (error instanceof DOMException && error.name === 'AbortError') return
        setUsageState('error')
      })

    return () => controller.abort()
  }, [])

  const failedRequests = usage
    ? Math.max(0, usage.summary.totalRequests - usage.summary.successfulRequests)
    : 0

  return (
    <section className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
      <div className="mb-10">
        <p className="eyebrow">Developer console</p>
        <h1 className="mt-3 font-serif text-4xl tracking-tight sm:text-5xl">
          {t('你的合成聲線 API 接用總覽。', 'Your synthetic voice API at a glance.')}
        </h1>
        <p className="mt-4 max-w-2xl text-sm leading-7 text-stone-600">
          {localize(locale, <>
            這裡唯讀呈現目前核發給你的 API 專案、效期與聲線授權狀態。受管金鑰可到
            <Link className="mx-1 font-semibold text-amber-800 underline" to="/developer/credentials">API 金鑰</Link>
            頁建立、換發或撤銷；完整 secret 只在操作完成後顯示一次。接用方式請參考
            <Link className="mx-1 font-semibold text-amber-800 underline" to="/developers/docs">API 文件</Link>
            ，呼叫結果可到 <Link className="font-semibold text-amber-800 underline" to="/developer/usage">用量與活動</Link> 查看。
            想先確認聲線效果，可以直接使用 <Link className="font-semibold text-amber-800 underline" to="/developer/playground">API Playground</Link>。
          </>, <>
            This read-only view shows your issued API projects, access windows, and voice grants. Create,
            rotate, or revoke managed credentials under
            <Link className="mx-1 font-semibold text-amber-800 underline" to="/developer/credentials">API keys</Link>.
            A complete secret is shown only once after an operation. See the
            <Link className="mx-1 font-semibold text-amber-800 underline" to="/developers/docs">API docs</Link>
            for integration details, review calls under <Link className="font-semibold text-amber-800 underline" to="/developer/usage">usage and activity</Link>,
            or try an authorized voice in the <Link className="font-semibold text-amber-800 underline" to="/developer/playground">API Playground</Link>.
          </>)}
        </p>
      </div>

      {state === 'loading' && <div className="library-state" role="status">{t('正在讀取 API 接用總覽…', 'Loading your API access overview…')}</div>}
      {state === 'error' && <div className="library-state border-rose-300 text-rose-700" role="alert">{t('接用總覽讀取失敗，請重新整理頁面。', 'We could not load your API access overview. Refresh the page to try again.')}</div>}

      {state === 'ready' && overview && (
        <>
          {!overview.serviceEnabled && (
            <div className="mb-6 rounded-2xl border border-amber-300 bg-amber-50 px-5 py-4 text-sm leading-6 text-amber-900">
              {t(
                '合成聲線 API 服務目前未啟用；下方僅為已登錄的核發紀錄，實際呼叫會得到 404。',
                'The synthetic voice API is currently disabled. The records below remain visible, but API calls will return 404.',
              )}
            </div>
          )}

          <section aria-label={t('最近 24 小時用量', 'Usage in the last 24 hours')} className="mb-8">
            <div className="mb-4 flex flex-wrap items-end justify-between gap-3">
              <div>
                <p className="eyebrow">Last 24 hours</p>
                <h2 className="mt-2 font-serif text-2xl text-stone-900">{t('最近 24 小時', 'Last 24 hours')}</h2>
              </div>
              <Link className="text-sm font-semibold text-amber-800 underline" to="/developer/usage">
                {t('查看完整用量與活動', 'View complete usage and activity')}
              </Link>
            </div>

            {usageState === 'loading' && (
              <div className="library-state min-h-28" role="status">{t('正在整理最近用量…', 'Loading recent usage…')}</div>
            )}
            {usageState === 'error' && (
              <div className="library-state min-h-28 border-amber-300 text-amber-800" role="alert">
                {t('最近用量暫時無法讀取，不影響下方專案與金鑰操作。', 'Recent usage is temporarily unavailable. Your projects and key controls remain available below.')}
              </div>
            )}
            {usageState === 'ready' && usage && (
              <>
                <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-5">
                  {[
                    [t('要求數', 'Requests'), usage.summary.totalRequests.toLocaleString(numberLocale)],
                    [t('成功', 'Succeeded'), usage.summary.successfulRequests.toLocaleString(numberLocale)],
                    [t('失敗', 'Failed'), failedRequests.toLocaleString(numberLocale)],
                    [t('429 次數', '429 responses'), usage.summary.rateLimitedRequests.toLocaleString(numberLocale)],
                    [t('平均耗時', 'Average latency'), `${usage.summary.averageLatencyMilliseconds.toFixed(1)} ms`],
                  ].map(([label, value]) => (
                    <article className="rounded-2xl border border-stone-200 bg-white/80 p-5" key={label}>
                      <p className="text-xs text-stone-400">{label}</p>
                      <p className="mt-2 font-serif text-2xl text-stone-900">{value}</p>
                    </article>
                  ))}
                </div>
                {usage.summary.totalRequests === 0 && (
                  <p className="mt-3 text-sm text-stone-500">{t('這段期間尚無 API 活動。', 'There was no API activity during this period.')}</p>
                )}
              </>
            )}
          </section>

          {overview.projects.length === 0 && (
            <div className="library-state min-h-52">
              <div>
                <h3 className="font-serif text-2xl text-stone-800">{t('目前沒有核發給你的 API 專案。', 'No API projects have been issued to you yet.')}</h3>
                <p className="mt-3 text-sm text-stone-500">
                  {localize(locale, <>
                    存取目前由 StoryVoice 團隊手動核發；請先閱讀
                    <Link className="mx-1 font-semibold text-amber-800 underline" to="/developers/docs">API 文件</Link>
                    裡「如何取得存取」的說明。
                  </>, <>
                    StoryVoice currently issues access manually. Read
                    <Link className="mx-1 font-semibold text-amber-800 underline" to="/developers/docs">the API docs</Link>
                    to learn how access is granted.
                  </>)}
                </p>
              </div>
            </div>
          )}

          {overview.projects.length > 0 && (
            <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
              {overview.projects.map((project) => (
                <article className="rounded-2xl border border-stone-200 bg-white/80 p-6 shadow-[0_4px_18px_rgba(180,101,15,.06)]" key={project.keyId}>
                  <div className="flex flex-wrap items-start justify-between gap-3">
                    <div>
                      <h2 className="font-serif text-2xl text-stone-900">{project.displayName || project.projectId || project.keyId}</h2>
                      <p className="mt-1 text-xs text-stone-500">{t('專案 ID：', 'Project ID: ')}{project.projectId || '—'}</p>
                    </div>
                    <span className={`rounded-full border px-3 py-1 text-xs ${PROJECT_STATUS_CLASS[project.status]}`}>
                      {projectStatusLabel(project.status, locale)}
                    </span>
                  </div>

                  <dl className="mt-4 grid grid-cols-1 gap-x-6 gap-y-2 text-sm text-stone-600 sm:grid-cols-2">
                    <div>
                      <dt className="text-xs text-stone-400">{t('存取層級', 'Access tier')}</dt>
                      <dd>{tierLabel(project.accessTier, locale)}</dd>
                    </div>
                    <div>
                      <dt className="text-xs text-stone-400">{t('金鑰識別', 'Key identifier')}</dt>
                      <dd><code className="text-xs">{project.tokenPrefix}{project.keyId}.…</code></dd>
                    </div>
                    <div>
                      <dt className="text-xs text-stone-400">{t('生效時間', 'Effective at')}</dt>
                      <dd>{formatUtc(project.effectiveAtUtc, locale)}</dd>
                    </div>
                    <div>
                      <dt className="text-xs text-stone-400">{t('到期時間', 'Expires at')}</dt>
                      <dd>{formatUtc(project.expiresAtUtc, locale)}</dd>
                    </div>
                    {project.territoryCountryCode && (
                      <div>
                        <dt className="text-xs text-stone-400">{t('授權地區', 'Authorized territory')}</dt>
                        <dd>{project.territoryCountryCode}</dd>
                      </div>
                    )}
                  </dl>

                  <div className="mt-5 border-t border-stone-100 pt-4">
                    <h3 className="text-xs uppercase tracking-[.2em] text-stone-400">{t('已授權聲線', 'Authorized voices')}</h3>
                    {project.voices.length === 0 && <p className="mt-2 text-sm text-stone-500">{t('這個專案還沒有授權任何聲線。', 'No voices are authorized for this project yet.')}</p>}
                    {project.voices.length > 0 && (
                      <ul className="mt-2 space-y-2">
                        {project.voices.map((voice) => (
                          <li className="flex flex-wrap items-center justify-between gap-2 text-sm" key={voice.voiceAlias}>
                            <code className="text-xs text-stone-700">{voice.voiceAlias}</code>
                            {voice.status === 'revoked'
                              ? <span className="rounded-full border border-rose-300 bg-rose-50 px-2 py-0.5 text-xs text-rose-700">{t('已撤銷', 'Revoked')}</span>
                              : <span className="rounded-full border border-emerald-300 bg-emerald-50 px-2 py-0.5 text-xs text-emerald-700">{t('可使用', 'Available')}</span>}
                          </li>
                        ))}
                      </ul>
                    )}
                  </div>

                  <Link
                    className="mt-5 inline-flex rounded-full border border-amber-300 bg-amber-50 px-4 py-2 text-sm font-semibold text-amber-800 transition hover:border-amber-400"
                    to={`/developer/projects/${encodeURIComponent(project.projectId || project.keyId)}`}
                  >
                    {t('查看專案詳情 →', 'View project details →')}
                  </Link>
                  <Link
                    className="mt-5 ml-3 inline-flex rounded-full border border-stone-300 px-4 py-2 text-sm font-semibold text-stone-700 transition hover:border-amber-400"
                    to={`/developer/playground?project=${encodeURIComponent(project.projectId || project.keyId)}`}
                  >
                    {t('試聽聲線 →', 'Try a voice →')}
                  </Link>
                </article>
              ))}
            </div>
          )}

          <p className="mt-8 text-xs leading-6 text-stone-400">
            {localize(locale, <>
              共用限制：每分鐘 {overview.requestsPerMinute.toLocaleString(numberLocale)} 次要求、單次文字上限 {overview.maximumTextCharacters.toLocaleString(numberLocale)} 字（{overview.maximumTextUtf8Bytes.toLocaleString(numberLocale)} bytes UTF-8）。
              詳細契約與錯誤碼請見 <Link className="underline" to="/developers/docs">API 文件</Link>。
            </>, <>
              Shared limits: {overview.requestsPerMinute.toLocaleString(numberLocale)} {overview.requestsPerMinute === 1 ? 'request' : 'requests'} per minute and up to {overview.maximumTextCharacters.toLocaleString(numberLocale)} characters ({overview.maximumTextUtf8Bytes.toLocaleString(numberLocale)} UTF-8 bytes) per request.
              See the <Link className="underline" to="/developers/docs">API docs</Link> for the complete contract and stable error codes.
            </>)}
          </p>
        </>
      )}
    </section>
  )
}
