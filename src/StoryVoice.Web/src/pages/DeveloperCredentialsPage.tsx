import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { Link, useOutletContext, useSearchParams } from 'react-router-dom'

import type { AuthedOutletContext } from '../authOutletContext'
import { ConfirmDialog } from '../components/ConfirmDialog'
import {
  canonicalDeveloperProjectReference,
  findDeveloperProjectByReference,
} from '../developerProjectReference'
import {
  credentialStatusLabel,
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
import { localize, useLocale, type SupportedLocale } from '../i18n'

type LoadState = 'loading' | 'ready' | 'error'
type PendingCredentialAction = {
  kind: 'rotate' | 'revoke'
  credential: DeveloperVoiceCredentialSummary
}

const AUDIT_LABEL: Record<DeveloperVoiceCredentialAuditSummary['action'], string> = {
  created: '建立金鑰',
  rotated: '換發金鑰',
  revoked: '撤銷金鑰',
}

const AUDIT_LABEL_EN: Record<DeveloperVoiceCredentialAuditSummary['action'], string> = {
  created: 'Created key',
  rotated: 'Rotated key',
  revoked: 'Revoked key',
}

const auditLabel = (
  action: DeveloperVoiceCredentialAuditSummary['action'],
  locale: SupportedLocale,
) => localize(locale, AUDIT_LABEL[action], AUDIT_LABEL_EN[action])

export function DeveloperCredentialsPage() {
  const { locale } = useLocale()
  const t = (zh: string, en: string) => localize(locale, zh, en)
  const { csrfToken } = useOutletContext<AuthedOutletContext>()
  const [searchParams] = useSearchParams()
  const requestedProject = searchParams.get('project') ?? ''
  const [state, setState] = useState<LoadState>('loading')
  const [overview, setOverview] = useState<DeveloperVoiceConsoleOverview | null>(null)
  const [credentials, setCredentials] = useState<DeveloperVoiceCredentialSummary[]>([])
  const [audit, setAudit] = useState<DeveloperVoiceCredentialAuditSummary[]>([])
  const [projectId, setProjectId] = useState(requestedProject)
  const [name, setName] = useState(() => t('正式站後端', 'Production backend'))
  const [overlapMinutes, setOverlapMinutes] = useState(60)
  const [issued, setIssued] = useState<IssuedDeveloperVoiceCredential | null>(null)
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)
  const [pendingAction, setPendingAction] = useState<PendingCredentialAction | null>(null)
  const [loadedRequestedProject, setLoadedRequestedProject] = useState<string | null>(null)
  const routeTransitioning = loadedRequestedProject !== requestedProject

  useEffect(() => {
    setState('loading')
    setPendingAction(null)
    const controller = new AbortController()
    Promise.all([
      fetchDeveloperVoiceOverview(controller.signal),
      fetchDeveloperVoiceCredentials(controller.signal),
      fetchDeveloperVoiceCredentialAudit(controller.signal),
    ])
      .then(([nextOverview, nextCredentials, nextAudit]) => {
        if (controller.signal.aborted) return
        setOverview(nextOverview)
        setCredentials(nextCredentials.credentials)
        setAudit(nextAudit)
        const requested = findDeveloperProjectByReference(nextOverview.projects, requestedProject)
        const firstAvailable = nextOverview.projects.find((project) => project.status !== 'expired')
        const selected = requested && requested.status !== 'expired' ? requested : firstAvailable
        setProjectId(selected ? canonicalDeveloperProjectReference(selected) : '')
        setLoadedRequestedProject(requestedProject)
        setState('ready')
      })
      .catch((error: unknown) => {
        if (error instanceof DOMException && error.name === 'AbortError') return
        if (controller.signal.aborted) return
        setLoadedRequestedProject(requestedProject)
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

  async function refreshAfterMutation(successMessage: string) {
    setMessage(successMessage)
    try {
      await refresh()
    } catch {
      setMessage(`${successMessage} ${t(
        '但金鑰清單與異動紀錄重新整理失敗；畫面內容可能仍是舊資料，請稍後重新載入頁面。',
        'However, the key list and activity log could not be refreshed. The page may show stale data; reload it later.',
      )}`)
    }
  }

  async function createCredential(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (routeTransitioning || state !== 'ready') {
      setMessage(t('正在切換 API 專案，請等資料更新完成後再操作。', 'Wait for the API project switch to finish before continuing.'))
      return
    }
    if (pendingAction) return
    if (issued) {
      setMessage(t('請先保存並關閉目前的一次性金鑰，再建立另一組金鑰。', 'Save and close the current one-time key before creating another key.'))
      return
    }
    if (!overview?.serviceEnabled) {
      setMessage(t('語音 API 目前未啟用，無法建立金鑰。', 'The voice API is disabled, so a key cannot be created.'))
      return
    }
    const selectedProject = findDeveloperProjectByReference(overview.projects, projectId)
    if (!selectedProject || selectedProject.status === 'expired') {
      setMessage(t('請選擇尚未到期的 API 專案。', 'Select an API project that has not expired.'))
      return
    }
    setBusy(true)
    setMessage('')
    try {
      const nextIssued = await createDeveloperVoiceCredential(projectId, name, csrfToken)
      setIssued(nextIssued)
      await refreshAfterMutation(t('金鑰已建立；完整 secret 關閉後不會再次顯示。', 'Key created. The complete secret cannot be shown again after you close it.'))
    } catch (error) {
      setMessage(error instanceof Error ? error.message : t('金鑰建立失敗。', 'We could not create the key.'))
    } finally {
      setBusy(false)
    }
  }

  async function rotateCredential(credential: DeveloperVoiceCredentialSummary) {
    if (!credential.id) return
    if (routeTransitioning || state !== 'ready') {
      setMessage(t('正在切換 API 專案，請等資料更新完成後再操作。', 'Wait for the API project switch to finish before continuing.'))
      return
    }
    if (issued) {
      setMessage(t('請先保存並關閉目前的一次性金鑰，再換發另一組金鑰。', 'Save and close the current one-time key before rotating another key.'))
      return
    }
    if (!overview?.serviceEnabled) {
      setMessage(t('語音 API 目前未啟用，無法換發金鑰；現有受管金鑰仍可撤銷。', 'The voice API is disabled, so keys cannot be rotated. Existing managed keys can still be revoked.'))
      return
    }
    setBusy(true)
    setMessage('')
    try {
      const nextIssued = await rotateDeveloperVoiceCredential(
        credential.id,
        overlapMinutes,
        csrfToken,
      )
      setIssued(nextIssued)
      await refreshAfterMutation(localize(locale, `新金鑰已建立；舊金鑰將在 ${overlapMinutes} 分鐘後撤銷。`, `New key created. The previous key will be revoked in ${overlapMinutes} minutes.`))
    } catch (error) {
      setMessage(error instanceof Error ? error.message : t('金鑰換發失敗。', 'We could not rotate the key.'))
    } finally {
      setBusy(false)
    }
  }

  async function revokeCredential(credential: DeveloperVoiceCredentialSummary) {
    if (!credential.id) return
    if (routeTransitioning || state !== 'ready') {
      setMessage(t('正在切換 API 專案，請等資料更新完成後再操作。', 'Wait for the API project switch to finish before continuing.'))
      return
    }
    setBusy(true)
    setMessage('')
    try {
      await revokeDeveloperVoiceCredential(credential.id, csrfToken)
      await refreshAfterMutation(t('金鑰已撤銷。', 'Key revoked.'))
    } catch (error) {
      setMessage(error instanceof Error ? error.message : t('金鑰撤銷失敗。', 'We could not revoke the key.'))
    } finally {
      setBusy(false)
    }
  }

  function confirmPendingAction() {
    const action = pendingAction
    setPendingAction(null)
    if (!action) return
    if (action.kind === 'rotate') {
      void rotateCredential(action.credential)
    } else {
      void revokeCredential(action.credential)
    }
  }

  async function copyIssuedToken() {
    if (!issued) return
    try {
      await navigator.clipboard.writeText(issued.accessToken)
      setMessage(t('完整金鑰已複製；請保存到伺服器端 secret store。', 'Complete key copied. Store it in a server-side secret store.'))
    } catch {
      setMessage(t('瀏覽器無法自動複製；請手動選取上方完整金鑰，或下載 .env。', 'The browser could not copy the key. Select it manually above or download the .env file.'))
    }
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

  const selectedProject = overview
    ? findDeveloperProjectByReference(overview.projects, projectId)
    : undefined
  const issuableProjects = overview?.projects.filter((project) => project.status !== 'expired') ?? []
  const issuedCredentialPanel = issued && (
    <section aria-label={t('一次性金鑰', 'One-time API key')} className="mt-6 rounded-2xl border-2 border-amber-400 bg-white p-6 shadow-lg">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <p className="eyebrow">{t('只顯示這一次', 'Shown only once')}</p>
          <h2 className="mt-2 font-serif text-2xl text-stone-900">{t('立即保存完整金鑰', 'Save the complete key now')}</h2>
          <p className="mt-2 text-sm text-rose-700">{t('關閉後無法重新查看；遺失時只能換發。', 'You cannot reveal it again after closing this panel. Rotate the key if it is lost.')}</p>
          <p className="mt-2 text-xs leading-5 text-stone-500">{t('保存並關閉這組金鑰前，建立與換發功能會暫停，避免完整 secret 被下一組金鑰取代。', 'Creating and rotating keys is paused until you save and close this key, so another secret cannot replace it.')}</p>
        </div>
        <button className="secondary-button" onClick={() => setIssued(null)} type="button">{t('我已保存，關閉', 'I saved it — close')}</button>
      </div>
      <code className="mt-5 block break-all rounded-xl bg-stone-950 p-4 text-sm text-amber-100">{issued.accessToken}</code>
      <div className="mt-4 flex flex-wrap gap-3">
        <button className="primary-button" onClick={() => void copyIssuedToken()} type="button">{t('複製完整金鑰', 'Copy complete key')}</button>
        <button className="secondary-button" onClick={downloadEnv} type="button">{t('下載 .env', 'Download .env')}</button>
      </div>
    </section>
  )

  if (routeTransitioning || state === 'loading') {
    return (
      <main className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
        {issuedCredentialPanel}
        <div className="library-state mt-6">
          <span role="status">{loadedRequestedProject === null
            ? t('正在讀取 API 金鑰…', 'Loading API keys…')
            : t('正在切換 API 專案…', 'Switching API projects…')}</span>
        </div>
      </main>
    )
  }

  if (state === 'error' || !overview) {
    return (
      <main className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
        {issuedCredentialPanel}
        <div className="library-state mt-6 border-rose-300 text-rose-700">
          <span role="alert">{t('API 金鑰讀取失敗，請重新整理頁面。', 'We could not load API keys. Refresh the page to try again.')}</span>
        </div>
      </main>
    )
  }

  return (
    <main className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
      <div className="flex flex-wrap items-end justify-between gap-5">
        <div>
          <p className="eyebrow">Developer credentials</p>
          <h1 className="mt-3 font-serif text-4xl tracking-tight sm:text-5xl">{t('API 金鑰', 'API keys')}</h1>
          <p className="mt-4 max-w-3xl text-sm leading-7 text-stone-600">
            {t('每組金鑰只在建立或換發後顯示一次。StoryVoice 只保存 SHA-256，不保存可還原的完整 secret。', 'Each complete key is shown only once after creation or rotation. StoryVoice stores only its SHA-256 hash, never a recoverable secret.')}
          </p>
        </div>
        <Link className="font-semibold text-amber-800 underline" to="/developer">{t('返回開發者總覽', 'Back to developer overview')}</Link>
      </div>

      {message && <div aria-live="polite" className="mt-6 rounded-2xl border border-amber-200 bg-amber-50 px-5 py-4 text-sm text-amber-950">{message}</div>}

      {issuedCredentialPanel}

      <section className="mt-8 rounded-2xl border border-stone-200 bg-white/80 p-6">
        <h2 className="font-serif text-2xl text-stone-900">{t('建立金鑰', 'Create a key')}</h2>
        {!overview.serviceEnabled && (
          <p className="mt-4 text-sm text-amber-800" role="status">{t('語音 API 目前未啟用，暫時無法建立或換發金鑰；現有受管金鑰仍可撤銷。', 'The voice API is disabled, so keys cannot be created or rotated. Existing managed keys can still be revoked.')}</p>
        )}
        {issuableProjects.length === 0 ? (
          <p className="mt-4 text-sm text-stone-500">{t('目前沒有尚未到期、可建立金鑰的 API 專案。', 'No unexpired API project is currently eligible for a new key.')}</p>
        ) : (
          <form className="mt-5 grid gap-4 md:grid-cols-[minmax(14rem,1fr)_minmax(14rem,1fr)_auto] md:items-end" onSubmit={(event) => void createCredential(event)}>
            <label className="text-sm text-stone-600">
              {t('API 專案', 'API project')}
              <select className="auth-input mt-2" onChange={(event) => setProjectId(event.target.value)} required value={projectId}>
                {issuableProjects.map((project) => (
                  <option key={project.keyId} value={project.projectId || project.keyId}>
                    {project.displayName || project.projectId || project.keyId}
                  </option>
                ))}
              </select>
            </label>
            <label className="text-sm text-stone-600">
              {t('金鑰名稱', 'Key name')}
              <input className="auth-input mt-2" maxLength={80} minLength={2} onChange={(event) => setName(event.target.value)} required value={name} />
            </label>
            <button className="primary-button" disabled={routeTransitioning || busy || Boolean(issued) || Boolean(pendingAction) || !overview.serviceEnabled || !selectedProject || selectedProject.status === 'expired'} type="submit">{t('建立金鑰', 'Create key')}</button>
          </form>
        )}
      </section>

      <section className="mt-8">
        <div className="flex flex-wrap items-end justify-between gap-4">
          <div>
            <p className="eyebrow">Credential inventory</p>
            <h2 className="mt-2 font-serif text-3xl text-stone-900">{t('現有金鑰', 'Existing keys')}</h2>
          </div>
          <label className="text-xs text-stone-500">
            {t('換發重疊時間', 'Rotation overlap')}
            <select className="auth-input mt-2" onChange={(event) => setOverlapMinutes(Number(event.target.value))} value={overlapMinutes}>
              <option value={0}>{t('立即停用舊金鑰', 'Disable the old key immediately')}</option>
              <option value={60}>{t('保留 1 小時', 'Keep for 1 hour')}</option>
              <option value={1440}>{t('保留 24 小時', 'Keep for 24 hours')}</option>
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
                  {credentialStatusLabel(credential.status, locale)}
                </span>
              </div>
              <dl className="mt-4 grid grid-cols-2 gap-3 text-sm">
                <div><dt className="text-xs text-stone-400">{t('專案', 'Project')}</dt><dd className="mt-1 text-stone-700">{credential.projectId}</dd></div>
                <div><dt className="text-xs text-stone-400">{t('最近使用', 'Last used')}</dt><dd className="mt-1 text-stone-700">{credential.lastUsedAtUtc ? formatUtc(credential.lastUsedAtUtc, locale) : t('尚無紀錄', 'No activity yet')}</dd></div>
                <div><dt className="text-xs text-stone-400">{t('建立時間', 'Created at')}</dt><dd className="mt-1 text-stone-700">{credential.createdAtUtc ? formatUtc(credential.createdAtUtc, locale) : t('由部署設定提供', 'Provided by deployment configuration')}</dd></div>
                <div><dt className="text-xs text-stone-400">{t('到期時間', 'Expires at')}</dt><dd className="mt-1 text-stone-700">{formatUtc(credential.expiresAtUtc, locale)}</dd></div>
                {credential.revokedAtUtc && (
                  <div>
                    <dt className="text-xs text-stone-400">{credential.status === 'revocation-scheduled'
                      ? t('預定撤銷', 'Scheduled revocation')
                      : t('撤銷時間', 'Revoked at')}</dt>
                    <dd className="mt-1 text-stone-700">{formatUtc(credential.revokedAtUtc, locale)}</dd>
                  </div>
                )}
              </dl>
              {!credential.managed && <p className="mt-4 text-xs leading-5 text-stone-500">{t('這是既有部署設定金鑰；可建立新的受管金鑰取代，撤銷仍由維運設定處理。', 'This key comes from deployment configuration. You can replace it with a managed key, but revocation remains an operations task.')}</p>}
              {credential.managed && credential.status === 'active' && (
                <div className="mt-5 flex flex-wrap gap-3">
                  <button className="secondary-button" disabled={routeTransitioning || busy || Boolean(issued) || Boolean(pendingAction) || !overview.serviceEnabled} onClick={() => setPendingAction({ kind: 'rotate', credential })} type="button">{t('換發', 'Rotate')}</button>
                  <button className="rounded-full border border-rose-300 px-4 py-2 text-sm text-rose-700" disabled={routeTransitioning || busy || Boolean(pendingAction)} onClick={() => setPendingAction({ kind: 'revoke', credential })} type="button">{t('立即撤銷', 'Revoke now')}</button>
                </div>
              )}
              {credential.managed && credential.status === 'revocation-scheduled' && (
                <button className="mt-5 rounded-full border border-rose-300 px-4 py-2 text-sm text-rose-700" disabled={routeTransitioning || busy || Boolean(pendingAction)} onClick={() => setPendingAction({ kind: 'revoke', credential })} type="button">{t('改成立即撤銷', 'Revoke immediately instead')}</button>
              )}
            </article>
          ))}
          {credentials.length === 0 && <div className="library-state lg:col-span-2">{t('尚未建立任何 API 金鑰。', 'No API keys have been created yet.')}</div>}
        </div>
      </section>

      <section className="mt-10 rounded-2xl border border-stone-200 bg-white/70 p-6">
        <p className="eyebrow">Durable audit</p>
        <h2 className="mt-2 font-serif text-2xl text-stone-900">{t('金鑰異動紀錄', 'Key activity log')}</h2>
        {audit.length === 0 ? <p className="mt-4 text-sm text-stone-500">{t('目前沒有異動紀錄。', 'There is no key activity yet.')}</p> : (
          <ul className="mt-5 divide-y divide-stone-100">
            {audit.map((entry) => (
              <li className="flex flex-wrap items-center justify-between gap-3 py-3 text-sm" key={entry.id}>
                <span className="text-stone-700">{auditLabel(entry.action, locale)} · <code className="text-xs">{entry.credentialKeyId}</code></span>
                <time className="text-xs text-stone-400">{formatUtc(entry.occurredAtUtc, locale)}</time>
              </li>
            ))}
          </ul>
        )}
      </section>

      <ConfirmDialog
        cancelLabel={t('取消', 'Cancel')}
        confirmLabel={pendingAction?.kind === 'rotate' ? t('確認換發', 'Rotate key') : t('立即撤銷', 'Revoke now')}
        description={pendingAction?.kind === 'rotate'
          ? localize(locale, `StoryVoice 會建立新金鑰，舊金鑰將在 ${overlapMinutes} 分鐘後撤銷。`, `StoryVoice will create a new key and revoke the old key in ${overlapMinutes} minutes.`)
          : t('撤銷後這組金鑰會立即失效，而且無法復原。', 'The key will stop working immediately and cannot be restored.')}
        destructive={pendingAction?.kind === 'revoke'}
        onCancel={() => setPendingAction(null)}
        onConfirm={confirmPendingAction}
        open={pendingAction !== null}
        title={pendingAction?.kind === 'rotate'
          ? localize(locale, `確定換發「${pendingAction.credential.name}」？`, `Rotate “${pendingAction.credential.name}”?`)
          : localize(locale, `確定立即撤銷「${pendingAction?.credential.name ?? ''}」？這個動作無法復原。`, `Revoke “${pendingAction?.credential.name ?? ''}” immediately? This action cannot be undone.`)}
      />
    </main>
  )
}
