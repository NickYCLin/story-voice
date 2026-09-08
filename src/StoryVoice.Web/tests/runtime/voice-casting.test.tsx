import { expect, it } from 'vitest'
import { suggestCharacterVoices, type CastingCharacter, type CastingProfile, type CastingVoice } from '../../src/voiceCasting'

const narrator: CastingVoice = { provider: 'edge', voice: 'narrator', locale: 'zh-TW', gender: 'male', formalNarrationAvailable: true }
const female = (voice: string): CastingVoice => ({ ...narrator, voice, gender: 'female' })
const characters: CastingCharacter[] = [{ id: 'character-a', role: 'Main', characterProfileId: 'profile-a' }, { id: 'character-b', role: 'Supporting', characterProfileId: 'profile-b' }]
const profiles: CastingProfile[] = characters.map(character => ({ id: character.characterProfileId!, isActive: true, gender: '女性' }))
const current = Object.fromEntries(characters.map(character => [character.id, 'edge\nnarrator']))

it('建議只選同引擎且正式可用的聲線，並優先使用旁白的語系', () => {
  const suggestions = suggestCharacterVoices(characters, profiles, [
    { ...female('disabled'), formalNarrationAvailable: false },
    { ...female('foreign'), provider: 'other' },
    { ...female('mainland'), locale: 'zh-CN' }, female('taiwan'),
  ], narrator, current)
  expect(suggestions['character-a'].voiceKey).toBe('edge\ntaiwan')
  expect(suggestions['character-a'].reason).toContain('性別設定')
})

it('候選與角色的輸入順序不影響結果，已保存的相符選角不會每章改變', () => {
  const voices = [narrator, female('voice-a'), female('voice-b')]
  const first = suggestCharacterVoices(characters, profiles, voices, narrator, current)
  expect(first['character-a'].voiceKey).not.toBe(first['character-b'].voiceKey)
  expect(suggestCharacterVoices([...characters].reverse(), profiles, [...voices].reverse(), narrator, current)).toEqual(first)
  const saved = Object.fromEntries(Object.entries(first).map(([id, suggestion]) => [id, suggestion.voiceKey]))
  expect(suggestCharacterVoices(characters, profiles, voices, narrator, saved)).toEqual(first)
})

it('使用明確年齡範圍與完整風格標籤，否定詞不會被當成相符個性', () => {
  const configured = [{ ...profiles[0], age: '20 歲', personality: '不安靜', speakingStyle: '健談、活潑' }]
  const suggestions = suggestCharacterVoices([characters[0]], configured, [
    { ...female('older'), minimumAge: 50, maximumAge: 90, characterTags: ['安靜'] },
    { ...female('young'), minimumAge: 18, maximumAge: 30, characterTags: ['健談', '活潑'] },
  ], narrator, current)
  expect(suggestions['character-a'].voiceKey).toBe('edge\nyoung')
  expect(suggestions['character-a'].reason).toContain('年齡範圍')
  expect(suggestions['character-a'].reason).toContain('健談、活潑')
  expect(suggestions['character-a'].reason).not.toContain('安靜')
})

it('沒有明確性別、年齡或風格時不憑名稱猜測，保留原本聲線', () => {
  const unknown = profiles.map(profile => ({ ...profile, gender: '未設定', age: '看起來年輕', personality: '不安靜' }))
  const suggestions = suggestCharacterVoices(characters, unknown, [{ ...female('female'), characterTags: ['安靜'] }], narrator, current)
  expect(Object.values(suggestions).map(suggestion => suggestion.voiceKey)).toEqual(Object.values(current))
})

it('手動調整、未連結／已停用角色庫與克隆聲線都保留現有選擇', () => {
  const voices = [female('female')]
  expect(suggestCharacterVoices(characters, profiles, voices, narrator, current, new Set(['character-a']))['character-a'].reason).toContain('手動')
  const inactive = profiles.map(profile => ({ ...profile, isActive: false }))
  expect(suggestCharacterVoices(characters, inactive, voices, narrator, current)['character-a'].voiceKey).toBe(current['character-a'])
  expect(suggestCharacterVoices(characters, [], voices, narrator, current)['character-a'].voiceKey).toBe(current['character-a'])
  expect(suggestCharacterVoices(characters, profiles, voices, { ...narrator, provider: '3wa-voxcpm2' }, current)['character-a'].reason).toContain('克隆')
})

it('可用聲線不足時容許共用，不為了分散而選用不相符的性別', () => {
  const suggestions = suggestCharacterVoices(characters, profiles, [narrator, female('only-female')], narrator, current)
  expect(Object.values(suggestions).every(suggestion => suggestion.voiceKey === 'edge\nonly-female')).toBe(true)
  const noMatch = suggestCharacterVoices(characters, profiles, [narrator], narrator, current)
  expect(noMatch['character-a'].voiceKey).toBe(current['character-a'])
  expect(noMatch['character-a'].reason).toContain('沒有明確')
})
