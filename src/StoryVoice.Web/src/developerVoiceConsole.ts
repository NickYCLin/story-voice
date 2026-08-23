import { fetchJson } from './api'

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

export const formatUtc = (value: string) => {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString('zh-TW', { hour12: false })
}

export const fetchDeveloperVoiceOverview = (signal: AbortSignal) =>
  fetchJson<DeveloperVoiceConsoleOverview>('/api/developer/external-voice/overview', { signal })
