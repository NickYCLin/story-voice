export type CastingVoice = {
  provider: string
  voice: string
  locale: string
  formalNarrationAvailable: boolean
  gender?: string | null
  minimumAge?: number | null
  maximumAge?: number | null
  characterTags?: string[] | null
}

export type CastingCharacter = {
  id: string
  role: 'Main' | 'Supporting' | 'Minor'
  characterProfileId?: string | null
}

export type CastingProfile = {
  id: string
  isActive: boolean
  gender?: string | null
  age?: string | null
  personality?: string | null
  speakingStyle?: string | null
}

export type CastingSuggestion = { voiceKey: string; reason: string }
const key = (voice: CastingVoice) => `${voice.provider}\n${voice.voice}`
const order = (left: string, right: string) => left < right ? -1 : left > right ? 1 : 0
const roles = { Main: 0, Supporting: 1, Minor: 2 }
const normalize = (value: string) => value.trim().toLowerCase()

function gender(value?: string | null) {
  const normalized = normalize(value ?? '')
  if (['女', '女性', '女生', 'female', 'woman'].includes(normalized)) return 'female'
  if (['男', '男性', '男生', 'male', 'man'].includes(normalized)) return 'male'
  return null
}

function age(value?: string | null) {
  const match = value?.trim().match(/^(\d{1,3})\s*(?:歲|years?(?: old)?)?$/i)
  const parsed = match ? Number(match[1]) : NaN
  return Number.isInteger(parsed) && parsed >= 0 && parsed <= 150 ? parsed : null
}

export function suggestCharacterVoices(
  characters: CastingCharacter[],
  profiles: CastingProfile[],
  voices: CastingVoice[],
  narrator: CastingVoice,
  current: Record<string, string>,
  manualOverrides: ReadonlySet<string> = new Set(),
): Record<string, CastingSuggestion> {
  const suggestions: Record<string, CastingSuggestion> = {}
  const usage = new Map<string, number>([[key(narrator), 1]])
  const available = voices.filter(voice => voice.provider === narrator.provider && voice.formalNarrationAvailable)
  const sorted = [...characters].sort((left, right) => roles[left.role] - roles[right.role] || order(left.id, right.id))
  for (const character of sorted) {
    const existing = current[character.id] ?? ''
    const preserve = (reason: string) => {
      suggestions[character.id] = { voiceKey: existing, reason }
      usage.set(existing, (usage.get(existing) ?? 0) + 1)
    }
    if (manualOverrides.has(character.id)) { preserve('保留你手動選擇的聲線。'); continue }
    if (narrator.provider === '3wa-voxcpm2') { preserve('克隆聲線需人工選擇已就緒的角色聲線。'); continue }
    const profile = profiles.find(item => item.id === character.characterProfileId && item.isActive)
    if (!profile) { preserve('尚未連結啟用中的角色庫資料，保留目前聲線。'); continue }
    const targetGender = gender(profile.gender)
    const targetAge = age(profile.age)
    // Match explicitly separated phrases, not substrings: "不安靜" must not match "安靜".
    const traits = new Set([profile.personality ?? '', profile.speakingStyle ?? '']
      .flatMap(value => value.split(/[,，、;；\n|/]+/)).map(normalize).filter(Boolean))
    const candidates = available.filter(voice => !targetGender || voice.gender === targetGender).map(voice => {
      const reasons: string[] = []
      let score = 0
      if (targetGender && voice.gender === targetGender) { score += 10; reasons.push('符合角色庫性別設定') }
      const hasAgeRange = voice.minimumAge != null || voice.maximumAge != null
      if (targetAge != null && hasAgeRange && targetAge >= (voice.minimumAge ?? 0) && targetAge <= (voice.maximumAge ?? 150)) {
        score += 4
        reasons.push('符合目錄標示的年齡範圍')
      }
      const matchedTags = (voice.characterTags ?? []).filter(tag => traits.has(normalize(tag)))
      if (matchedTags.length > 0) {
        score += Math.min(matchedTags.length, 4) * 2
        reasons.push(`符合標籤：${matchedTags.join('、')}`)
      }
      return { voice, score, reasons }
    }).filter(candidate => candidate.score > 0)
    candidates.sort((left, right) => right.score - left.score
      || Number(right.voice.locale === narrator.locale) - Number(left.voice.locale === narrator.locale)
      || Number(key(right.voice) === existing) - Number(key(left.voice) === existing)
      || (usage.get(key(left.voice)) ?? 0) - (usage.get(key(right.voice)) ?? 0)
      || order(key(left.voice), key(right.voice)))
    const chosen = candidates[0]
    if (!chosen) { preserve('角色資料與可用聲線沒有明確的相符項目，保留目前聲線。'); continue }
    const selectedKey = key(chosen.voice)
    usage.set(selectedKey, (usage.get(selectedKey) ?? 0) + 1)
    suggestions[character.id] = { voiceKey: selectedKey, reason: `${chosen.reasons.join('；')}。` }
  }
  return suggestions
}
