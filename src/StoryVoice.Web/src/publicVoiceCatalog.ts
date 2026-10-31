import { apiUrl } from './api'

export type PublicVoiceCard = {
  alias: string
  displayName: string
  subtitle: string
  disclosure: string
  styles: string[]
  useCases: string[]
  sampleUrl: string | null
  canPreview: boolean
  ctaKind: string
  subscriptionAvailable: boolean
  status: string
}

export type PublicVoiceDetail = {
  voice: PublicVoiceCard
  license: {
    commercialUseAllowed: boolean
    publicDistributionAllowed: boolean
    crossProjectApiAllowed: boolean
    effectiveAtUtc: string
    expiresAtUtc: string
    territoryMode: 'worldwide' | 'country-list'
    territoryCountryCodes: string[]
  }
}

export function isPublicVoiceDetail(value: unknown, alias: string): value is PublicVoiceDetail {
  if (!value || typeof value !== 'object') return false
  const detail = value as Record<string, unknown>
  if (!isPublicVoiceCard(detail.voice) || detail.voice.alias !== alias
    || !detail.license || typeof detail.license !== 'object') return false
  const license = detail.license as Record<string, unknown>
  const validDates = typeof license.effectiveAtUtc === 'string'
    && typeof license.expiresAtUtc === 'string'
    && Number.isFinite(Date.parse(license.effectiveAtUtc))
    && Number.isFinite(Date.parse(license.expiresAtUtc))
    && Date.parse(license.effectiveAtUtc) < Date.parse(license.expiresAtUtc)
  return validDates
    && typeof license.commercialUseAllowed === 'boolean'
    && typeof license.publicDistributionAllowed === 'boolean'
    && typeof license.crossProjectApiAllowed === 'boolean'
    && Array.isArray(license.territoryCountryCodes)
    && license.territoryCountryCodes.every(code => typeof code === 'string' && /^[A-Z]{2}$/.test(code))
    && ((license.territoryMode === 'worldwide' && license.territoryCountryCodes.length === 0)
      || (license.territoryMode === 'country-list' && license.territoryCountryCodes.length >= 1
        && license.territoryCountryCodes.length <= 249))
}

const PUBLIC_DEMO_PREFIX = '/api/public/v1/voices/'

function isShortText(value: unknown, maxLength: number): value is string {
  return typeof value === 'string' && value.trim().length > 0 && value.length <= maxLength
}

function isOptionalShortText(value: unknown, maxLength: number): value is string {
  return typeof value === 'string' && value.length <= maxLength
}

function isShortTextList(value: unknown): value is string[] {
  return Array.isArray(value)
    && value.length >= 1
    && value.length <= 8
    && value.every((item) => isShortText(item, 40))
}

export function isPublicVoiceCard(value: unknown): value is PublicVoiceCard {
  if (!value || typeof value !== 'object') return false
  const card = value as Record<string, unknown>
  return typeof card.alias === 'string'
    && /^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$/.test(card.alias)
    && isShortText(card.displayName, 120)
    && isOptionalShortText(card.subtitle, 500)
    && isShortText(card.disclosure, 240)
    && isShortTextList(card.styles)
    && isShortTextList(card.useCases)
    && (card.sampleUrl === null || typeof card.sampleUrl === 'string')
    && typeof card.canPreview === 'boolean'
    && typeof card.ctaKind === 'string'
    && typeof card.subscriptionAvailable === 'boolean'
    && typeof card.status === 'string'
}

export function safeSampleUrl(voice: PublicVoiceCard) {
  if (!voice.canPreview || !voice.sampleUrl) return null
  const expectedPath = `${PUBLIC_DEMO_PREFIX}${encodeURIComponent(voice.alias)}/demo`
  return voice.sampleUrl === expectedPath ? apiUrl(voice.sampleUrl) : null
}
