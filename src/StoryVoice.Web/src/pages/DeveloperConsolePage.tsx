import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

import {
  fetchDeveloperVoiceOverview,
  fetchDeveloperVoiceUsage,
  formatUtc,
  PROJECT_STATUS_CLASS,
  PROJECT_STATUS_LABEL,
  TIER_LABEL,
} from '../developerVoiceConsole'
import type {
  DeveloperVoiceConsoleOverview,
  DeveloperVoiceUsageReport,
} from '../developerVoiceConsole'

type LoadState = 'loading' | 'ready' | 'error'

export function DeveloperConsolePage() {
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
        <h1 className="mt-3 font-serif text-4xl tracking-tight sm:text-5xl">你的合成聲線 API 接用總覽。</h1>
        <p className="mt-4 max-w-2xl text-sm leading-7 text-stone-600">
          這裡唯讀呈現目前核發給你的 API 專案、效期與聲線授權狀態。受管金鑰可到
          <Link className="mx-1 font-semibold text-amber-800 underline" to="/developer/credentials">API 金鑰</Link>
          頁建立、換發或撤銷；完整 secret 只在操作完成後顯示一次。接用方式請參考
          <Link className="mx-1 font-semibold text-amber-800 underline" to="/developers/docs">API 文件</Link>
          ，呼叫結果可到 <Link className="font-semibold text-amber-800 underline" to="/developer/usage">用量與活動</Link> 查看。
          想先確認聲線效果，可以直接使用 <Link className="font-semibold text-amber-800 underline" to="/developer/playground">API Playground</Link>。
        </p>
      </div>

      {state === 'loading' && <div className="library-state" role="status">正在讀取 API 接用總覽…</div>}
      {state === 'error' && <div className="library-state border-rose-300 text-rose-700" role="alert">接用總覽讀取失敗，請重新整理頁面。</div>}

      {state === 'ready' && overview && (
        <>
          {!overview.serviceEnabled && (
            <div className="mb-6 rounded-2xl border border-amber-300 bg-amber-50 px-5 py-4 text-sm leading-6 text-amber-900">
              合成聲線 API 服務目前未啟用；下方僅為已登錄的核發紀錄，實際呼叫會得到 404。
            </div>
          )}

          <section aria-label="最近 24 小時用量" className="mb-8">
            <div className="mb-4 flex flex-wrap items-end justify-between gap-3">
              <div>
                <p className="eyebrow">Last 24 hours</p>
                <h2 className="mt-2 font-serif text-2xl text-stone-900">最近 24 小時</h2>
              </div>
              <Link className="text-sm font-semibold text-amber-800 underline" to="/developer/usage">
                查看完整用量與活動
              </Link>
            </div>

            {usageState === 'loading' && (
              <div className="library-state min-h-28" role="status">正在整理最近用量…</div>
            )}
            {usageState === 'error' && (
              <div className="library-state min-h-28 border-amber-300 text-amber-800" role="alert">
                最近用量暫時無法讀取，不影響下方專案與金鑰操作。
              </div>
            )}
            {usageState === 'ready' && usage && (
              <>
                <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-5">
                  {[
                    ['要求數', usage.summary.totalRequests.toLocaleString('zh-TW')],
                    ['成功', usage.summary.successfulRequests.toLocaleString('zh-TW')],
                    ['失敗', failedRequests.toLocaleString('zh-TW')],
                    ['429 次數', usage.summary.rateLimitedRequests.toLocaleString('zh-TW')],
                    ['平均耗時', `${usage.summary.averageLatencyMilliseconds.toFixed(1)} ms`],
                  ].map(([label, value]) => (
                    <article className="rounded-2xl border border-stone-200 bg-white/80 p-5" key={label}>
                      <p className="text-xs text-stone-400">{label}</p>
                      <p className="mt-2 font-serif text-2xl text-stone-900">{value}</p>
                    </article>
                  ))}
                </div>
                {usage.summary.totalRequests === 0 && (
                  <p className="mt-3 text-sm text-stone-500">這段期間尚無 API 活動。</p>
                )}
              </>
            )}
          </section>

          {overview.projects.length === 0 && (
            <div className="library-state min-h-52">
              <div>
                <h3 className="font-serif text-2xl text-stone-800">目前沒有核發給你的 API 專案。</h3>
                <p className="mt-3 text-sm text-stone-500">
                  存取目前由 StoryVoice 團隊手動核發；請先閱讀
                  <Link className="mx-1 font-semibold text-amber-800 underline" to="/developers/docs">API 文件</Link>
                  裡「如何取得存取」的說明。
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
                      <p className="mt-1 text-xs text-stone-500">專案 ID：{project.projectId || '—'}</p>
                    </div>
                    <span className={`rounded-full border px-3 py-1 text-xs ${PROJECT_STATUS_CLASS[project.status]}`}>
                      {PROJECT_STATUS_LABEL[project.status]}
                    </span>
                  </div>

                  <dl className="mt-4 grid grid-cols-1 gap-x-6 gap-y-2 text-sm text-stone-600 sm:grid-cols-2">
                    <div>
                      <dt className="text-xs text-stone-400">存取層級</dt>
                      <dd>{TIER_LABEL[project.accessTier] ?? project.accessTier}</dd>
                    </div>
                    <div>
                      <dt className="text-xs text-stone-400">金鑰識別</dt>
                      <dd><code className="text-xs">{project.tokenPrefix}{project.keyId}.…</code></dd>
                    </div>
                    <div>
                      <dt className="text-xs text-stone-400">生效時間</dt>
                      <dd>{formatUtc(project.effectiveAtUtc)}</dd>
                    </div>
                    <div>
                      <dt className="text-xs text-stone-400">到期時間</dt>
                      <dd>{formatUtc(project.expiresAtUtc)}</dd>
                    </div>
                    {project.territoryCountryCode && (
                      <div>
                        <dt className="text-xs text-stone-400">授權地區</dt>
                        <dd>{project.territoryCountryCode}</dd>
                      </div>
                    )}
                  </dl>

                  <div className="mt-5 border-t border-stone-100 pt-4">
                    <h3 className="text-xs uppercase tracking-[.2em] text-stone-400">已授權聲線</h3>
                    {project.voices.length === 0 && <p className="mt-2 text-sm text-stone-500">這個專案還沒有授權任何聲線。</p>}
                    {project.voices.length > 0 && (
                      <ul className="mt-2 space-y-2">
                        {project.voices.map((voice) => (
                          <li className="flex flex-wrap items-center justify-between gap-2 text-sm" key={voice.voiceAlias}>
                            <code className="text-xs text-stone-700">{voice.voiceAlias}</code>
                            {voice.status === 'revoked'
                              ? <span className="rounded-full border border-rose-300 bg-rose-50 px-2 py-0.5 text-xs text-rose-700">已撤銷</span>
                              : <span className="rounded-full border border-emerald-300 bg-emerald-50 px-2 py-0.5 text-xs text-emerald-700">可使用</span>}
                          </li>
                        ))}
                      </ul>
                    )}
                  </div>

                  <Link
                    className="mt-5 inline-flex rounded-full border border-amber-300 bg-amber-50 px-4 py-2 text-sm font-semibold text-amber-800 transition hover:border-amber-400"
                    to={`/developer/projects/${encodeURIComponent(project.projectId || project.keyId)}`}
                  >
                    查看專案詳情 →
                  </Link>
                  <Link
                    className="mt-5 ml-3 inline-flex rounded-full border border-stone-300 px-4 py-2 text-sm font-semibold text-stone-700 transition hover:border-amber-400"
                    to={`/developer/playground?project=${encodeURIComponent(project.projectId || project.keyId)}`}
                  >
                    試聽聲線 →
                  </Link>
                </article>
              ))}
            </div>
          )}

          <p className="mt-8 text-xs leading-6 text-stone-400">
            共用限制：每分鐘 {overview.requestsPerMinute} 次要求、單次文字上限 {overview.maximumTextCharacters} 字（{overview.maximumTextUtf8Bytes} bytes UTF-8）。
            詳細契約與錯誤碼請見 <Link className="underline" to="/developers/docs">API 文件</Link>。
          </p>
        </>
      )}
    </section>
  )
}
