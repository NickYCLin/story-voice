import { apiUrl, fetchJson } from './api'

export type DeveloperVoiceGrantSummary = {
  voiceAlias: string
  status: 'active' | 'revoked'
  revokedAtUtc: string | null
}

export type DeveloperVoiceProjectSummary = {
  keyId: string
  displayName: string
  projectId: string
  accessTier: string
  tokenPrefix: string
  consumerFamilyId: string
  territoryCountryCode: string
  effectiveAtUtc: string
  expiresAtUtc: string
  status: 'not-yet-effective' | 'active' | 'expiring-soon' | 'expired'
  voices: DeveloperVoiceGrantSummary[]
}

export type DeveloperVoiceConsoleOverview = {
  serviceEnabled: boolean
  requestsPerMinute: number
  maximumTextCharacters: number
  maximumTextUtf8Bytes: number
  projects: DeveloperVoiceProjectSummary[]
}

export type DeveloperVoiceCredentialStatus =
  | 'not-yet-effective'
  | 'active'
  | 'revocation-scheduled'
  | 'expired'
  | 'revoked'

export type DeveloperVoiceCredentialSummary = {
  id: string | null
  keyId: string
  name: string
  projectId: string
  accessTier: string
  tokenPrefix: string
  managed: boolean
  createdAtUtc: string | null
  lastUsedAtUtc: string | null
  expiresAtUtc: string
  revokedAtUtc: string | null
  status: DeveloperVoiceCredentialStatus
}

export type DeveloperVoiceCredentialList = {
  credentials: DeveloperVoiceCredentialSummary[]
}

export type IssuedDeveloperVoiceCredential = {
  credential: DeveloperVoiceCredentialSummary
  accessToken: string
  notice: string
}

export type DeveloperVoiceCredentialAuditSummary = {
  id: string
  credentialKeyId: string
  action: 'created' | 'rotated' | 'revoked'
  occurredAtUtc: string
  relatedCredentialKeyId: string | null
}

export type DeveloperVoiceUsageSummary = {
  totalRequests: number
  successfulRequests: number
  successRatePercent: number
  rateLimitedRequests: number
  averageLatencyMilliseconds: number
  outputBytes: number
  outputDurationMilliseconds: number
}

export type DeveloperVoiceUsageActivity = {
  requestId: string
  projectId: string
  voiceAlias: string | null
  occurredAtUtc: string
  statusCode: number
  outcome: string
  durationMilliseconds: number
  textCharacters: number | null
  responseBytes: number
  audioDurationMilliseconds: number
}

export type DeveloperVoiceUsageReport = {
  fromUtc: string
  toUtc: string
  summary: DeveloperVoiceUsageSummary
  activities: DeveloperVoiceUsageActivity[]
}

export type DeveloperVoiceUsageFilters = {
  fromUtc: string
  toUtc: string
  projectId?: string
  voice?: string
  limit?: number
}

export type DeveloperVoicePlaygroundAudio = {
  audio: Blob
  requestId: string
  idempotencyKey: string
  latencyMilliseconds: number
  audioDurationMilliseconds: number
  responseBytes: number
}

export class DeveloperVoicePlaygroundError extends Error {
  readonly status: number
  readonly code: string
  readonly requestId: string
  readonly retryAfterSeconds: number | null

  constructor(
    message: string,
    status: number,
    code: string,
    requestId: string,
    retryAfterSeconds: number | null,
  ) {
    super(message)
    this.name = 'DeveloperVoicePlaygroundError'
    this.status = status
    this.code = code
    this.requestId = requestId
    this.retryAfterSeconds = retryAfterSeconds
  }
}

export const PROJECT_STATUS_LABEL: Record<DeveloperVoiceProjectSummary['status'], string> = {
  'not-yet-effective': '尚未生效',
  active: '有效',
  'expiring-soon': '即將到期',
  expired: '已到期',
}

export const PROJECT_STATUS_CLASS: Record<DeveloperVoiceProjectSummary['status'], string> = {
  'not-yet-effective': 'border-stone-300 bg-stone-50 text-stone-600',
  active: 'border-emerald-300 bg-emerald-50 text-emerald-700',
  'expiring-soon': 'border-amber-300 bg-amber-50 text-amber-800',
  expired: 'border-rose-300 bg-rose-50 text-rose-700',
}

export const TIER_LABEL: Record<string, string> = {
  'private-development': '私人開發（private-development）',
  'subscription-commercial': '訂閱商用（subscription-commercial）',
}

export const CREDENTIAL_STATUS_LABEL: Record<DeveloperVoiceCredentialStatus, string> = {
  'not-yet-effective': '尚未生效',
  active: '有效',
  'revocation-scheduled': '已排程撤銷',
  expired: '已到期',
  revoked: '已撤銷',
}

export const formatUtc = (value: string) => {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString('zh-TW', { hour12: false })
}

export const fetchDeveloperVoiceOverview = (signal: AbortSignal) =>
  fetchJson<DeveloperVoiceConsoleOverview>('/api/developer/external-voice/overview', { signal })

export const fetchDeveloperVoiceCredentials = (signal?: AbortSignal) =>
  fetchJson<DeveloperVoiceCredentialList>('/api/developer/external-voice/credentials', { signal })

export const fetchDeveloperVoiceCredentialAudit = (signal?: AbortSignal) =>
  fetchJson<DeveloperVoiceCredentialAuditSummary[]>(
    '/api/developer/external-voice/credentials/audit',
    { signal },
  )

export const fetchDeveloperVoiceUsage = (
  filters: DeveloperVoiceUsageFilters,
  signal?: AbortSignal,
) => {
  const query = new URLSearchParams({
    fromUtc: filters.fromUtc,
    toUtc: filters.toUtc,
    limit: String(filters.limit ?? 50),
  })
  if (filters.projectId) query.set('projectId', filters.projectId)
  if (filters.voice) query.set('voice', filters.voice)
  return fetchJson<DeveloperVoiceUsageReport>(
    `/api/developer/external-voice/usage?${query.toString()}`,
    { signal },
  )
}

export async function synthesizeDeveloperVoicePlayground(
  projectId: string,
  voice: string,
  text: string,
  idempotencyKey: string,
  csrfToken: string,
  signal?: AbortSignal,
): Promise<DeveloperVoicePlaygroundAudio> {
  const response = await fetch(apiUrl('/api/developer/external-voice/playground'), {
    method: 'POST',
    credentials: 'same-origin',
    headers: {
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': csrfToken,
    },
    body: JSON.stringify({ projectId, voice, text, idempotencyKey }),
    signal,
  })
  const requestId = response.headers.get('X-StoryVoice-Request-Id') ?? ''
  const retryAfterHeader = response.headers.get('Retry-After')
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as {
      detail?: string
      code?: string
      requestId?: string
    } | null
    throw new DeveloperVoicePlaygroundError(
      problem?.detail ?? `語音產生失敗（${response.status}）`,
      response.status,
      problem?.code ?? 'synthesis_unavailable',
      problem?.requestId ?? requestId,
      retryAfterHeader ? Number.parseInt(retryAfterHeader, 10) : null,
    )
  }

  const audio = await response.blob()
  return {
    audio,
    requestId,
    idempotencyKey,
    latencyMilliseconds: Number.parseInt(response.headers.get('X-StoryVoice-Latency-Ms') ?? '0', 10),
    audioDurationMilliseconds: Number.parseInt(response.headers.get('X-StoryVoice-Audio-Duration-Ms') ?? '0', 10),
    responseBytes: audio.size,
  }
}

export const createDeveloperVoiceCredential = (
  projectId: string,
  name: string,
  csrfToken: string,
) => fetchJson<IssuedDeveloperVoiceCredential>('/api/developer/external-voice/credentials', {
  method: 'POST',
  csrfToken,
  body: { projectId, name },
})

export const rotateDeveloperVoiceCredential = (
  credentialId: string,
  overlapMinutes: number,
  csrfToken: string,
) => fetchJson<IssuedDeveloperVoiceCredential>(
  `/api/developer/external-voice/credentials/${encodeURIComponent(credentialId)}/rotate`,
  {
    method: 'POST',
    csrfToken,
    body: { overlapMinutes },
  },
)

export const revokeDeveloperVoiceCredential = (
  credentialId: string,
  csrfToken: string,
) => fetchJson<void>(
  `/api/developer/external-voice/credentials/${encodeURIComponent(credentialId)}/revoke`,
  {
    method: 'POST',
    csrfToken,
    body: {},
  },
)
