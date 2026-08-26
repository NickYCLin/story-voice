import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { Link, useOutletContext, useSearchParams } from 'react-router-dom'

import type { AuthedOutletContext } from '../authOutletContext'
import {
  CREDENTIAL_STATUS_LABEL,
  createDeveloperVoiceCredential,
  fetchDeveloperVoiceCredentialAudit,
  fetchDeveloperVoiceCredentials,
  fetchDeveloperVoiceOverview,
  formatUtc,
  revokeDeveloperVoiceCredential,
  rotateDeveloperVoiceCredential,
} from '../developerVoiceConsole'
import type {
  DeveloperVoiceConsoleOverview,
  DeveloperVoiceCredentialAuditSummary,
  DeveloperVoiceCredentialSummary,
  IssuedDeveloperVoiceCredential,
} from '../developerVoiceConsole'

type LoadState = 'loading' | 'ready' | 'error'

const AUDIT_LABEL: Record<DeveloperVoiceCredentialAuditSummary['action'], string> = {
  created: '建立金鑰',
  rotated: '換發金鑰',
  revoked: '撤銷金鑰',
}

export function DeveloperCredentialsPage() {
  const { csrfToken } = useOutletContext<AuthedOutletContext>()
  const [searchParams] = useSearchParams()
  const requestedProject = searchParams.get('project') ?? ''
  const [state, setState] = useState<LoadState>('loading')
  const [overview, setOverview] = useState<DeveloperVoiceConsoleOverview | null>(null)
  const [credentials, setCredentials] = useState<DeveloperVoiceCredentialSummary[]>([])
  const [audit, setAudit] = useState<DeveloperVoiceCredentialAuditSummary[]>([])
  const [projectId, setProjectId] = useState(requestedProject)
  const [name, setName] = useState('正式站後端')
  const [overlapMinutes, setOverlapMinutes] = useState(60)
  const [issued, setIssued] = useState<IssuedDeveloperVoiceCredential | null>(null)
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    Promise.all([
      fetchDeveloperVoiceOverview(controller.signal),
      fetchDeveloperVoiceCredentials(controller.signal),
      fetchDeveloperVoiceCredentialAudit(controller.signal),
    ])
      .then(([nextOverview, nextCredentials, nextAudit]) => {
        setOverview(nextOverview)
        setCredentials(nextCredentials.credentials)
        setAudit(nextAudit)
        const canUseRequested = nextOverview.projects.some((project) =>
          project.projectId === requestedProject || project.keyId === requestedProject)
        if (!canUseRequested) {
          const firstAvailable = nextOverview.projects.find((project) => project.status !== 'expired')
          setProjectId(firstAvailable?.projectId || firstAvailable?.keyId || '')
        }
        setState('ready')
      })
      .catch((error: unknown) => {
        if (error instanceof DOMException && error.name === 'AbortError') return
        setState('error')
      })
    return () => controller.abort()
  }, [requestedProject])

  async function refresh() {
    const [nextCredentials, nextAudit] = await Promise.all([
      fetchDeveloperVoiceCredentials(),
      fetchDeveloperVoiceCredentialAudit(),
    ])
    setCredentials(nextCredentials.credentials)
    setAudit(nextAudit)
  }

  async function createCredential(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setBusy(true)
    setMessage('')
    try {
      const nextIssued = await createDeveloperVoiceCredential(projectId, name, csrfToken)
      setIssued(nextIssued)
      setMessage('金鑰已建立；完整 secret 關閉後不會再次顯示。')
      await refresh()
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '金鑰建立失敗。')
    } finally {
      setBusy(false)
    }
  }

  async function rotateCredential(credential: DeveloperVoiceCredentialSummary) {
    if (!credential.id || !window.confirm(`確定換發「${credential.name}」？`)) return
    setBusy(true)
    setMessage('')
    try {
      const nextIssued = await rotateDeveloperVoiceCredential(
        credential.id,
        overlapMinutes,
        csrfToken,
      )
      setIssued(nextIssued)
      setMessage(`新金鑰已建立；舊金鑰將在 ${overlapMinutes} 分鐘後撤銷。`)
      await refresh()
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '金鑰換發失敗。')
    } finally {
      setBusy(false)
    }
  }

  async function revokeCredential(credential: DeveloperVoiceCredentialSummary) {
    if (!credential.id || !window.confirm(`確定立即撤銷「${credential.name}」？這個動作無法復原。`)) return
    setBusy(true)
    setMessage('')
    try {
      await revokeDeveloperVoiceCredential(credential.id, csrfToken)
      setMessage('金鑰已撤銷。')
      await refresh()
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '金鑰撤銷失敗。')
    } finally {
      setBusy(false)
    }
  }

  async function copyIssuedToken() {
    if (!issued) return
    await navigator.clipboard.writeText(issued.accessToken)
    setMessage('完整金鑰已複製；請保存到伺服器端 secret store。')
  }

  function downloadEnv() {
    if (!issued) return
    const blob = new Blob([`STORYVOICE_VOICE_TOKEN=${issued.accessToken}\n`], {
      type: 'text/plain;charset=utf-8',
    })
    const objectUrl = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = objectUrl
    anchor.download = `storyvoice-${issued.credential.keyId}.env`
    anchor.click()
    URL.revokeObjectURL(objectUrl)
  }

  if (state === 'loading') {
    return <main className="library-state mx-auto my-12 max-w-7xl">正在讀取 API 金鑰…</main>
  }

  if (state === 'error' || !overview) {
    return <main className="library-state mx-auto my-12 max-w-7xl border-rose-300 text-rose-700">API 金鑰讀取失敗，請重新整理頁面。</main>
  }

  return (
    <main className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
      <div className="flex flex-wrap items-end justify-between gap-5">
        <div>
          <p className="eyebrow">Developer credentials</p>
          <h1 className="mt-3 font-serif text-4xl tracking-tight sm:text-5xl">API 金鑰</h1>
          <p className="mt-4 max-w-3xl text-sm leading-7 text-stone-600">
            每組金鑰只在建立或換發後顯示一次。StoryVoice 只保存 SHA-256，不保存可還原的完整 secret。
          </p>
        </div>
        <Link className="font-semibold text-amber-800 underline" to="/developer">返回開發者總覽</Link>
      </div>

      {message && <div aria-live="polite" className="mt-6 rounded-2xl border border-amber-200 bg-amber-50 px-5 py-4 text-sm text-amber-950">{message}</div>}

      {issued && (
        <section aria-label="一次性金鑰" className="mt-6 rounded-2xl border-2 border-amber-400 bg-white p-6 shadow-lg">
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div>
              <p className="eyebrow">只顯示這一次</p>
              <h2 className="mt-2 font-serif text-2xl text-stone-900">立即保存完整金鑰</h2>
              <p className="mt-2 text-sm text-rose-700">關閉後無法重新查看；遺失時只能換發。</p>
            </div>
            <button className="secondary-button" onClick={() => setIssued(null)} type="button">我已保存，關閉</button>
          </div>
          <code className="mt-5 block break-all rounded-xl bg-stone-950 p-4 text-sm text-amber-100">{issued.accessToken}</code>
          <div className="mt-4 flex flex-wrap gap-3">
            <button className="primary-button" onClick={() => void copyIssuedToken()} type="button">複製完整金鑰</button>
            <button className="secondary-button" onClick={downloadEnv} type="button">下載 .env</button>
          </div>
        </section>
      )}

      <section className="mt-8 rounded-2xl border border-stone-200 bg-white/80 p-6">
        <h2 className="font-serif text-2xl text-stone-900">建立金鑰</h2>
        {overview.projects.length === 0 ? (
          <p className="mt-4 text-sm text-stone-500">目前沒有可建立金鑰的 API 專案。</p>
        ) : (
          <form className="mt-5 grid gap-4 md:grid-cols-[minmax(14rem,1fr)_minmax(14rem,1fr)_auto] md:items-end" onSubmit={(event) => void createCredential(event)}>
            <label className="text-sm text-stone-600">
              API 專案
              <select className="auth-input mt-2" onChange={(event) => setProjectId(event.target.value)} required value={projectId}>
                {overview.projects.map((project) => (
                  <option disabled={project.status === 'expired'} key={project.keyId} value={project.projectId || project.keyId}>
                    {project.displayName || project.projectId || project.keyId}
                  </option>
                ))}
              </select>
            </label>
            <label className="text-sm text-stone-600">
              金鑰名稱
              <input className="auth-input mt-2" maxLength={80} minLength={2} onChange={(event) => setName(event.target.value)} required value={name} />
            </label>
            <button className="primary-button" disabled={busy || !projectId} type="submit">建立金鑰</button>
          </form>
        )}
      </section>

      <section className="mt-8">
        <div className="flex flex-wrap items-end justify-between gap-4">
          <div>
            <p className="eyebrow">Credential inventory</p>
            <h2 className="mt-2 font-serif text-3xl text-stone-900">現有金鑰</h2>
          </div>
          <label className="text-xs text-stone-500">
            換發重疊時間
            <select className="auth-input mt-2" onChange={(event) => setOverlapMinutes(Number(event.target.value))} value={overlapMinutes}>
              <option value={0}>立即停用舊金鑰</option>
              <option value={60}>保留 1 小時</option>
              <option value={1440}>保留 24 小時</option>
            </select>
          </label>
        </div>

        <div className="mt-5 grid gap-4 lg:grid-cols-2">
          {credentials.map((credential) => (
            <article className="rounded-2xl border border-stone-200 bg-white/80 p-6" key={`${credential.managed ? 'managed' : 'configured'}-${credential.keyId}`}>
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <h3 className="font-serif text-xl text-stone-900">{credential.name}</h3>
                  <code className="mt-1 block text-xs text-stone-500">{credential.tokenPrefix}{credential.keyId}.…</code>
                </div>
                <span className="rounded-full border border-stone-300 bg-stone-50 px-3 py-1 text-xs text-stone-700">
                  {CREDENTIAL_STATUS_LABEL[credential.status]}
                </span>
              </div>
              <dl className="mt-4 grid grid-cols-2 gap-3 text-sm">
                <div><dt className="text-xs text-stone-400">專案</dt><dd className="mt-1 text-stone-700">{credential.projectId}</dd></div>
                <div><dt className="text-xs text-stone-400">最近使用</dt><dd className="mt-1 text-stone-700">{credential.lastUsedAtUtc ? formatUtc(credential.lastUsedAtUtc) : '尚無紀錄'}</dd></div>
                <div><dt className="text-xs text-stone-400">建立時間</dt><dd className="mt-1 text-stone-700">{credential.createdAtUtc ? formatUtc(credential.createdAtUtc) : '由部署設定提供'}</dd></div>
                <div><dt className="text-xs text-stone-400">到期時間</dt><dd className="mt-1 text-stone-700">{formatUtc(credential.expiresAtUtc)}</dd></div>
              </dl>
              {!credential.managed && <p className="mt-4 text-xs leading-5 text-stone-500">這是既有部署設定金鑰；可建立新的受管金鑰取代，撤銷仍由維運設定處理。</p>}
              {credential.managed && credential.status === 'active' && (
                <div className="mt-5 flex flex-wrap gap-3">
                  <button className="secondary-button" disabled={busy} onClick={() => void rotateCredential(credential)} type="button">換發</button>
                  <button className="rounded-full border border-rose-300 px-4 py-2 text-sm text-rose-700" disabled={busy} onClick={() => void revokeCredential(credential)} type="button">立即撤銷</button>
                </div>
              )}
              {credential.managed && credential.status === 'revocation-scheduled' && (
                <button className="mt-5 rounded-full border border-rose-300 px-4 py-2 text-sm text-rose-700" disabled={busy} onClick={() => void revokeCredential(credential)} type="button">改成立即撤銷</button>
              )}
            </article>
          ))}
          {credentials.length === 0 && <div className="library-state lg:col-span-2">尚未建立任何 API 金鑰。</div>}
        </div>
      </section>

      <section className="mt-10 rounded-2xl border border-stone-200 bg-white/70 p-6">
        <p className="eyebrow">Durable audit</p>
        <h2 className="mt-2 font-serif text-2xl text-stone-900">金鑰異動紀錄</h2>
        {audit.length === 0 ? <p className="mt-4 text-sm text-stone-500">目前沒有異動紀錄。</p> : (
          <ul className="mt-5 divide-y divide-stone-100">
            {audit.map((entry) => (
              <li className="flex flex-wrap items-center justify-between gap-3 py-3 text-sm" key={entry.id}>
                <span className="text-stone-700">{AUDIT_LABEL[entry.action]} · <code className="text-xs">{entry.credentialKeyId}</code></span>
                <time className="text-xs text-stone-400">{formatUtc(entry.occurredAtUtc)}</time>
              </li>
            ))}
          </ul>
        )}
      </section>
    </main>
  )
}
