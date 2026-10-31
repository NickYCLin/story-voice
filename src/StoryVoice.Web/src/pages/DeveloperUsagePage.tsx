import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'

import {
  findDeveloperProjectByReference,
  normalizeDeveloperProjectReference,
} from '../developerProjectReference'
import {
  fetchDeveloperVoiceOverview,
  fetchDeveloperVoiceUsage,
  formatUtc,
} from '../developerVoiceConsole'
import type {
  DeveloperVoiceConsoleOverview,
  DeveloperVoiceUsageReport,
} from '../developerVoiceConsole'
import { localize, useLocale, type SupportedLocale } from '../i18n'

type LoadState = 'loading' | 'ready' | 'error' | 'not-found'

const OUTCOME_LABEL: Record<string, string> = {
  succeeded: '成功',
  invalid_request: '要求格式錯誤',
  voice_not_available: '聲線不可用',
  idempotency_conflict: '冪等鍵衝突',
  rate_limited: '超過速率限制',
  synthesis_unavailable: '合成暫時不可用',
  request_cancelled: '呼叫端取消',
}

const OUTCOME_LABEL_EN: Record<string, string> = {
  succeeded: 'Succeeded',
  invalid_request: 'Invalid request',
  voice_not_available: 'Voice unavailable',
  idempotency_conflict: 'Idempotency conflict',
  rate_limited: 'Rate limited',
  synthesis_unavailable: 'Synthesis unavailable',
  request_cancelled: 'Caller cancelled',
}

const outcomeLabel = (outcome: string, locale: SupportedLocale) =>
  localize(locale, OUTCOME_LABEL[outcome], OUTCOME_LABEL_EN[outcome]) ?? outcome

const formatBytes = (bytes: number, numberLocale: string) => {
  if (bytes < 1024) return `${bytes.toLocaleString(numberLocale)} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toLocaleString(numberLocale, { maximumFractionDigits: 1 })} KiB`
  return `${(bytes / (1024 * 1024)).toLocaleString(numberLocale, { maximumFractionDigits: 1 })} MiB`
}

const formatAudioDuration = (
  milliseconds: number,
  locale: SupportedLocale,
  numberLocale: string,
) => {
  const seconds = milliseconds / 1000
  return seconds < 60
    ? `${seconds.toLocaleString(numberLocale, { maximumFractionDigits: 1 })} ${localize(locale, '秒', 'seconds')}`
    : `${(seconds / 60).toLocaleString(numberLocale, { maximumFractionDigits: 1 })} ${localize(locale, '分鐘', 'minutes')}`
}

export function DeveloperUsagePage() {
  const { locale, numberLocale } = useLocale()
  const t = (zh: string, en: string) => localize(locale, zh, en)
  const [searchParams] = useSearchParams()
  const requestedProject = searchParams.get('project') ?? ''
  const [state, setState] = useState<LoadState>('loading')
  const [overview, setOverview] = useState<DeveloperVoiceConsoleOverview | null>(null)
  const [report, setReport] = useState<DeveloperVoiceUsageReport | null>(null)
  const [hours, setHours] = useState(24)
  const [projectId, setProjectId] = useState(requestedProject)
  const [voice, setVoice] = useState('')
  const [loadedRequestedProject, setLoadedRequestedProject] = useState<string | null>(null)
  const loadedRequestedProjectRef = useRef<string | null>(null)
  const requestSequenceRef = useRef(0)
  const routeTransitioning = loadedRequestedProject !== requestedProject

  useEffect(() => {
    setProjectId(requestedProject)
    setVoice('')
  }, [requestedProject])

  useEffect(() => {
    const requestSequence = requestSequenceRef.current + 1
    requestSequenceRef.current = requestSequence
    const controller = new AbortController()
    const toUtc = new Date()
    const fromUtc = new Date(toUtc.getTime() - hours * 60 * 60 * 1000)
    const projectReference = loadedRequestedProjectRef.current !== requestedProject
      ? requestedProject
      : projectId
    setState('loading')
    void (async () => {
      try {
        const nextOverview = await fetchDeveloperVoiceOverview(controller.signal)
        if (controller.signal.aborted || requestSequenceRef.current !== requestSequence) return
        const matchingProject = projectReference
          ? findDeveloperProjectByReference(nextOverview.projects, projectReference)
          : undefined
        if (projectReference && !matchingProject) {
          setOverview(nextOverview)
          setReport(null)
          setVoice('')
          loadedRequestedProjectRef.current = requestedProject
          setLoadedRequestedProject(requestedProject)
          setState('not-found')
          return
        }

        const normalizedProjectId = normalizeDeveloperProjectReference(
          nextOverview.projects,
          projectReference,
        )
        if (normalizedProjectId !== projectId) {
          setOverview(nextOverview)
          setReport(null)
          setProjectId(normalizedProjectId)
          setVoice('')
          return
        }

        const nextReport = await fetchDeveloperVoiceUsage({
          fromUtc: fromUtc.toISOString(),
          toUtc: toUtc.toISOString(),
          projectId: normalizedProjectId || undefined,
          voice: voice || undefined,
        }, controller.signal)
        if (controller.signal.aborted || requestSequenceRef.current !== requestSequence) return
        setOverview(nextOverview)
        setReport(nextReport)
        loadedRequestedProjectRef.current = requestedProject
        setLoadedRequestedProject(requestedProject)
        setState('ready')
      } catch (error: unknown) {
        if (error instanceof DOMException && error.name === 'AbortError') return
        if (controller.signal.aborted || requestSequenceRef.current !== requestSequence) return
        loadedRequestedProjectRef.current = requestedProject
        setLoadedRequestedProject(requestedProject)
        setState('error')
      }
    })()
    return () => {
      controller.abort()
      if (requestSequenceRef.current === requestSequence) {
        requestSequenceRef.current += 1
      }
    }
  }, [hours, projectId, requestedProject, voice])

  const voices = useMemo(() => {
    if (!overview) return []
    return Array.from(new Set(
      overview.projects
        .filter((project) => !projectId || project.projectId === projectId || project.keyId === projectId)
        .flatMap((project) => project.voices.map((grant) => grant.voiceAlias)),
    )).sort()
  }, [overview, projectId])

  const selectedProject = overview?.projects.find((project) =>
    project.projectId === projectId || project.keyId === projectId)
  const relevantProjects = overview?.projects
    .filter((project) => !projectId || project.projectId === projectId || project.keyId === projectId)
    ?? []
  const nearestUpcomingExpiry = relevantProjects
    .filter((project) => project.status !== 'expired')
    .map((project) => project.expiresAtUtc)
    .sort()[0]
  const expirySummary = selectedProject?.status === 'expired'
    ? localize(locale, `${selectedProject.displayName} 已於 ${formatUtc(selectedProject.expiresAtUtc, locale)} 到期。`, `${selectedProject.displayName} expired at ${formatUtc(selectedProject.expiresAtUtc, locale)}.`)
    : nearestUpcomingExpiry
      ? localize(locale, `最近一個尚未到期的專案將於 ${formatUtc(nearestUpcomingExpiry, locale)} 到期。`, `The nearest unexpired project expires at ${formatUtc(nearestUpcomingExpiry, locale)}.`)
      : relevantProjects.length > 0
        ? t('目前沒有尚未到期的專案。', 'There are no unexpired projects.')
        : ''

  if (routeTransitioning) {
    return (
      <main className="library-state mx-auto my-12 max-w-7xl">
        <span role="status">{loadedRequestedProject === null
          ? t('正在整理使用量…', 'Loading usage…')
          : t('正在切換 API 專案…', 'Switching API projects…')}</span>
      </main>
    )
  }

  if (state === 'not-found') {
    return (
      <main className="mx-auto max-w-7xl px-6 py-12 lg:px-10">
        <div className="library-state min-h-52">
          <div>
            <h1 className="font-serif text-2xl text-stone-800">{t('找不到這個 API 專案。', 'API project not found.')}</h1>
            <p className="mt-3 text-sm text-stone-500">{t('專案不存在，或目前登入的帳號沒有檢視權限；未執行全部專案的用量查詢。', 'The project does not exist, or your account cannot view it. Usage across all projects was not queried.')}</p>
            <Link className="mt-5 inline-flex font-semibold text-amber-800 underline" to="/developer/usage">{t('查看全部專案用量', 'View usage for all projects')}</Link>
          </div>
        </div>
      </main>
    )
  }

  return (
    <main className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
      <div className="flex flex-wrap items-end justify-between gap-5">
        <div>
          <p className="eyebrow">Usage and activity</p>
          <h1 className="mt-3 font-serif text-4xl tracking-tight sm:text-5xl">{t('用量與活動', 'Usage and activity')}</h1>
          <p className="mt-4 max-w-3xl text-sm leading-7 text-stone-600">
            {t(
              '查看 external API 與可歸屬到目前 owner／project 的 Playground 合成要求。活動紀錄不保存輸入文字、完整金鑰、冪等鍵、參考音檔或逐字稿；寫入採 best-effort，不能作為計費或 hard quota 的唯一依據。',
              'Review synthesis requests authenticated with an API key and Playground requests attributable to the current owner and project. Activity never stores input text, complete keys, idempotency keys, reference audio, or transcripts. Writes are best-effort and cannot be the sole source for billing or a hard quota.',
            )}
          </p>
        </div>
        <Link className="font-semibold text-amber-800 underline" to="/developer">{t('返回開發者總覽', 'Back to developer overview')}</Link>
      </div>

      <section aria-label={t('用量篩選', 'Usage filters')} className="mt-8 grid gap-4 rounded-2xl border border-stone-200 bg-white/80 p-5 md:grid-cols-3">
        <label className="text-sm text-stone-600">
          {t('時間範圍', 'Time range')}
          <select className="auth-input mt-2" onChange={(event) => setHours(Number(event.target.value))} value={hours}>
            <option value={24}>{t('最近 24 小時', 'Last 24 hours')}</option>
            <option value={168}>{t('最近 7 天', 'Last 7 days')}</option>
            <option value={720}>{t('最近 30 天', 'Last 30 days')}</option>
          </select>
        </label>
        <label className="text-sm text-stone-600">
          {t('API 專案', 'API project')}
          <select className="auth-input mt-2" onChange={(event) => { setProjectId(event.target.value); setVoice('') }} value={projectId}>
            <option value="">{t('全部專案', 'All projects')}</option>
            {overview?.projects.map((project) => (
              <option key={project.keyId} value={project.projectId || project.keyId}>{project.displayName}</option>
            ))}
          </select>
        </label>
        <label className="text-sm text-stone-600">
          {t('聲線', 'Voice')}
          <select className="auth-input mt-2" onChange={(event) => setVoice(event.target.value)} value={voice}>
            <option value="">{t('全部聲線', 'All voices')}</option>
            {voices.map((alias) => <option key={alias} value={alias}>{alias}</option>)}
          </select>
        </label>
      </section>

      {state === 'loading' && <div className="library-state mt-8" role="status">{t('正在整理使用量…', 'Loading usage…')}</div>}
      {state === 'error' && <div className="library-state mt-8 border-rose-300 text-rose-700" role="alert">{t('使用量讀取失敗，請重新整理頁面。', 'We could not load usage. Refresh the page to try again.')}</div>}

      {state === 'ready' && overview && report && (
        <>
          <section aria-label={t('用量摘要', 'Usage summary')} className="mt-8 grid gap-4 sm:grid-cols-2 xl:grid-cols-6">
            {[
              [t('要求數', 'Requests'), report.summary.totalRequests.toLocaleString(numberLocale)],
              [t('成功率', 'Success rate'), `${report.summary.successRatePercent.toLocaleString(numberLocale, { maximumFractionDigits: 1 })}%`],
              [t('429 次數', '429 responses'), report.summary.rateLimitedRequests.toLocaleString(numberLocale)],
              [t('平均耗時', 'Average latency'), `${report.summary.averageLatencyMilliseconds.toLocaleString(numberLocale, { maximumFractionDigits: 1 })} ms`],
              [t('產出長度', 'Output duration'), formatAudioDuration(report.summary.outputDurationMilliseconds, locale, numberLocale)],
              [t('產出大小', 'Output size'), formatBytes(report.summary.outputBytes, numberLocale)],
            ].map(([label, value]) => (
              <article className="rounded-2xl border border-stone-200 bg-white/80 p-5" key={label}>
                <p className="text-xs text-stone-400">{label}</p>
                <p className="mt-2 font-serif text-2xl text-stone-900">{value}</p>
              </article>
            ))}
          </section>

          <div className="mt-6 rounded-2xl border border-amber-200 bg-amber-50 px-5 py-4 text-sm leading-6 text-amber-950">
            {selectedProject
              ? localize(locale, `${selectedProject.displayName}：`, `${selectedProject.displayName}: `)
              : t('目前專案：', 'Current scope: ')}
            {localize(locale, `每分鐘上限 ${overview.requestsPerMinute.toLocaleString(numberLocale)} 次。`, `Limit: ${overview.requestsPerMinute.toLocaleString(numberLocale)} requests per minute.`)}
            {expirySummary && <> {expirySummary}</>}
          </div>

          <section className="mt-10 overflow-hidden rounded-2xl border border-stone-200 bg-white/80">
            <div className="border-b border-stone-100 px-6 py-5">
              <p className="eyebrow">Recent activity</p>
              <h2 className="mt-2 font-serif text-2xl text-stone-900">{t('最近活動', 'Recent activity')}</h2>
            </div>
            {report.activities.length === 0 ? (
              <div className="library-state min-h-44">{t('這個篩選範圍目前沒有 API 活動。', 'There is no API activity in this filter range.')}</div>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full min-w-[760px] text-left text-sm">
                  <thead className="bg-stone-50 text-xs text-stone-500">
                    <tr>
                      <th className="px-6 py-3 font-medium">{t('時間', 'Time')}</th>
                      <th className="px-4 py-3 font-medium">Request ID</th>
                      <th className="px-4 py-3 font-medium">{t('專案／聲線', 'Project / voice')}</th>
                      <th className="px-4 py-3 font-medium">{t('結果', 'Outcome')}</th>
                      <th className="px-4 py-3 font-medium">{t('耗時', 'Latency')}</th>
                      <th className="px-6 py-3 text-right font-medium">{t('輸出', 'Output')}</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-stone-100">
                    {report.activities.map((activity) => (
                      <tr key={activity.requestId}>
                        <td className="px-6 py-4 text-xs text-stone-500">{formatUtc(activity.occurredAtUtc, locale)}</td>
                        <td className="px-4 py-4"><code className="text-xs text-stone-700">{activity.requestId}</code></td>
                        <td className="px-4 py-4 text-stone-700">{activity.projectId}<span className="block text-xs text-stone-400">{activity.voiceAlias ?? t('未解析聲線', 'Unresolved voice')}</span></td>
                        <td className="px-4 py-4"><span className={activity.statusCode === 200 ? 'text-emerald-700' : 'text-rose-700'}>{activity.statusCode} · {outcomeLabel(activity.outcome, locale)}</span></td>
                        <td className="px-4 py-4 text-stone-600">{activity.durationMilliseconds.toLocaleString(numberLocale)} ms</td>
                        <td className="px-6 py-4 text-right text-stone-600">{activity.responseBytes > 0 ? formatBytes(activity.responseBytes, numberLocale) : '—'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}
    </main>
  )
}
