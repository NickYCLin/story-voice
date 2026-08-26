import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'

import {
  fetchDeveloperVoiceOverview,
  formatUtc,
  PROJECT_STATUS_CLASS,
  PROJECT_STATUS_LABEL,
  TIER_LABEL,
} from '../developerVoiceConsole'
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

const remainingLabel = (expiresAtUtc: string) => {
  const expiresAt = new Date(expiresAtUtc).getTime()
  if (Number.isNaN(expiresAt)) return '無法計算'

  const remainingDays = Math.ceil((expiresAt - Date.now()) / 86_400_000)
  if (remainingDays <= 0) return '已到期'
  if (remainingDays === 1) return '剩餘 1 天'
  return `剩餘 ${remainingDays} 天`
}

export function DeveloperProjectPage() {
  const { projectId = '' } = useParams()
  const [state, setState] = useState<PageState>({ status: 'loading' })

  useEffect(() => {
    const controller = new AbortController()
    fetchDeveloperVoiceOverview(controller.signal)
      .then((overview) => {
        const project = overview.projects.find((candidate) =>
          candidate.projectId === projectId || candidate.keyId === projectId)
        setState(project
          ? { status: 'ready', overview, project }
          : { status: 'not-found' })
      })
      .catch((error: unknown) => {
        if (error instanceof DOMException && error.name === 'AbortError') return
        setState({ status: 'error' })
      })
    return () => controller.abort()
  }, [projectId])

  if (state.status === 'loading') {
    return <main className="library-state mx-auto my-12 max-w-7xl">正在讀取專案詳情…</main>
  }

  if (state.status === 'error') {
    return (
      <main className="mx-auto max-w-7xl px-6 py-12 lg:px-10">
        <div className="library-state border-rose-300 text-rose-700">專案詳情讀取失敗，請重新整理頁面。</div>
      </main>
    )
  }

  if (state.status === 'not-found') {
    return (
      <main className="mx-auto max-w-7xl px-6 py-12 lg:px-10">
        <div className="library-state min-h-52">
          <div>
            <h1 className="font-serif text-2xl text-stone-800">找不到這個 API 專案。</h1>
            <p className="mt-3 text-sm text-stone-500">專案不存在，或目前登入的帳號沒有檢視權限。</p>
            <Link className="mt-5 inline-flex font-semibold text-amber-800 underline" to="/developer">返回開發者總覽</Link>
          </div>
        </div>
      </main>
    )
  }

  const { overview, project } = state
  const credentialLabel = `${project.tokenPrefix}${project.keyId}.…`

  return (
    <main className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
      <Link className="text-sm font-semibold text-amber-800 underline" to="/developer">← 返回開發者總覽</Link>

      <header className="mt-7 flex flex-wrap items-start justify-between gap-5">
        <div>
          <p className="eyebrow">Developer project</p>
          <h1 className="mt-3 font-serif text-4xl tracking-tight sm:text-5xl">
            {project.displayName || project.projectId || project.keyId}
          </h1>
          <p className="mt-3 text-sm text-stone-500">專案 ID：<code>{project.projectId || '—'}</code></p>
        </div>
        <span className={`rounded-full border px-4 py-2 text-sm ${PROJECT_STATUS_CLASS[project.status]}`}>
          {PROJECT_STATUS_LABEL[project.status]}
        </span>
      </header>

      {!overview.serviceEnabled && (
        <div className="mt-8 rounded-2xl border border-amber-300 bg-amber-50 px-5 py-4 text-sm leading-6 text-amber-900">
          合成聲線 API 服務目前未啟用；這份頁面只代表已登錄的專案與授權資料，實際呼叫會得到 404。
        </div>
      )}

      <section className="mt-8 grid grid-cols-1 gap-4 lg:grid-cols-3" aria-label="專案摘要">
        <article className="rounded-2xl border border-stone-200 bg-white/80 p-6">
          <h2 className="text-xs uppercase tracking-[.2em] text-stone-400">存取層級</h2>
          <p className="mt-3 text-sm font-semibold text-stone-800">{TIER_LABEL[project.accessTier] ?? project.accessTier}</p>
          <p className="mt-2 text-xs leading-5 text-stone-500">Consumer：<code>{project.consumerFamilyId || '未提供'}</code></p>
          {project.territoryCountryCode && <p className="mt-1 text-xs text-stone-500">授權地區：{project.territoryCountryCode}</p>}
        </article>

        <article className="rounded-2xl border border-stone-200 bg-white/80 p-6">
          <h2 className="text-xs uppercase tracking-[.2em] text-stone-400">有效期間</h2>
          <dl className="mt-3 space-y-2 text-sm text-stone-700">
            <div><dt className="inline text-stone-400">開始：</dt><dd className="inline">{formatUtc(project.effectiveAtUtc)}</dd></div>
            <div><dt className="inline text-stone-400">到期：</dt><dd className="inline">{formatUtc(project.expiresAtUtc)}</dd></div>
          </dl>
          <p className="mt-3 text-xs font-semibold text-amber-800">{remainingLabel(project.expiresAtUtc)}</p>
        </article>

        <article className="rounded-2xl border border-stone-200 bg-white/80 p-6">
          <h2 className="text-xs uppercase tracking-[.2em] text-stone-400">共用限制</h2>
          <dl className="mt-3 space-y-2 text-sm text-stone-700">
            <div><dt className="inline text-stone-400">速率：</dt><dd className="inline">每分鐘 {overview.requestsPerMinute} 次</dd></div>
            <div><dt className="inline text-stone-400">文字：</dt><dd className="inline">{overview.maximumTextCharacters} 字</dd></div>
            <div><dt className="inline text-stone-400">UTF-8：</dt><dd className="inline">{overview.maximumTextUtf8Bytes} bytes</dd></div>
          </dl>
        </article>
      </section>

      <div className="mt-6 grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1.4fr)_minmax(19rem,.6fr)]">
        <section className="rounded-2xl border border-stone-200 bg-white/80 p-6" aria-labelledby="project-voices-heading">
          <h2 className="font-serif text-2xl text-stone-900" id="project-voices-heading">已授權聲線</h2>
          <p className="mt-2 text-sm leading-6 text-stone-500">撤銷狀態會在每次正式 API 呼叫前重新驗證。</p>

          {project.voices.length === 0 && <div className="library-state mt-5 min-h-36">這個專案還沒有授權任何聲線。</div>}
          {project.voices.length > 0 && (
            <ul className="mt-5 divide-y divide-stone-100">
              {project.voices.map((voice) => (
                <li className="flex flex-wrap items-center justify-between gap-3 py-4" key={voice.voiceAlias}>
                  <div>
                    <code className="text-sm text-stone-800">{voice.voiceAlias}</code>
                    {voice.revokedAtUtc && <p className="mt-1 text-xs text-stone-400">撤銷時間：{formatUtc(voice.revokedAtUtc)}</p>}
                  </div>
                  {voice.status === 'revoked'
                    ? <span className="rounded-full border border-rose-300 bg-rose-50 px-3 py-1 text-xs text-rose-700">已撤銷</span>
                    : <span className="rounded-full border border-emerald-300 bg-emerald-50 px-3 py-1 text-xs text-emerald-700">可使用</span>}
                </li>
              ))}
            </ul>
          )}
        </section>

        <aside className="space-y-6">
          <section className="rounded-2xl border border-stone-200 bg-white/80 p-6" aria-labelledby="credential-heading">
            <h2 className="font-serif text-2xl text-stone-900" id="credential-heading">Credential 摘要</h2>
            <p className="mt-4 text-xs text-stone-400">金鑰識別</p>
            <code className="mt-1 block break-all text-sm text-stone-800">{credentialLabel}</code>
            <p className="mt-4 text-xs text-stone-400">最近使用</p>
            <Link
              className="mt-1 inline-flex text-sm font-semibold text-amber-800 underline"
              to={`/developer/usage?project=${encodeURIComponent(project.projectId || project.keyId)}`}
            >
              查看這個專案的用量與活動 →
            </Link>
            <p className="mt-4 text-xs leading-5 text-stone-500">
              完整 secret 不會在頁面重新顯示。受管金鑰可自行建立、換發與撤銷；既有設定檔金鑰仍由維運流程管理。
            </p>
            <Link
              className="mt-4 inline-flex font-semibold text-amber-800 underline"
              to={`/developer/credentials?project=${encodeURIComponent(project.projectId || project.keyId)}`}
            >
              管理 API 金鑰 →
            </Link>
          </section>

          <section className="rounded-2xl border border-stone-200 bg-stone-900 p-6 text-stone-100">
            <h2 className="font-serif text-2xl">快速開始</h2>
            <ol className="mt-4 list-decimal space-y-3 pl-5 text-sm leading-6 text-stone-300">
              <li>閱讀 request、header 與錯誤碼契約。</li>
              <li>把既有 credential 放在呼叫端的 server-side secret store。</li>
              <li>以授權聲線 alias 呼叫 <code className="text-xs text-amber-200">POST /api/external/v1/speech</code>。</li>
            </ol>
            <Link className="mt-5 inline-flex font-semibold text-amber-200 underline" to="/developers/docs">查看 API 文件 →</Link>
          </section>
        </aside>
      </div>
    </main>
  )
}
