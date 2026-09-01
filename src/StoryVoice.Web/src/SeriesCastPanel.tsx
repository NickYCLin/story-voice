import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'

import { fetchBlob, fetchJson } from './api'
import { useAuthedOutletContext } from './authOutletContext'
import { CharacterVoiceProfilesPanel } from './CharacterVoiceProfilesPanel'
import { ConfirmDialog } from './components/ConfirmDialog'
import { SpeechPlanReview, type SeriesCharacterChoice, type SpeechPlanDraft } from './SpeechPlanReview'
import type { BookDetails, BookSummary } from './types'

const CUSTOM_VOICE_PROVIDER = '3wa-voxcpm2'
const EDGE_VOICE_PROVIDER = 'edge'
const VOAI_VOICE_PROVIDER = 'voai'
const BLUE_MAGPIE_PROVIDER = 'bluemagpie'
const BLUE_MAGPIE_VOICES = [
  { voice: 'female_voice', label: 'BlueMagpie 內建女聲' },
  { voice: 'hung_yi_lee', label: 'BlueMagpie 內建男聲' },
] as const
type BlueMagpieVoice = typeof BLUE_MAGPIE_VOICES[number]['voice']
type NarrativeVoiceMode = 'IndependentNarrator' | 'PointOfViewInnerMonologue'

type CharacterProfileSummary = {
  id: string
  canonicalName: string
  hasAvatar: boolean
  isActive: boolean
}

type VoiceOption = {
  provider: string
  voice: string
  displayName: string
  locale: string
  formalNarrationAvailable: boolean
  usageScope: 'standard' | 'private-self-hosted'
}

type SeriesSummary = {
  id: string
  name: string
  bookCount: number
  characterCount: number
  activeCastRevisionId: string | null
  createdAt: string
  updatedAt: string
}

type SeriesBook = {
  id: string
  bookId: string
  bookTitle: string
  volumeLabel: string
  sortOrder: number
  membershipRevision: number
  activeNarrationJobId: string | null
}

type SeriesCharacter = SeriesCharacterChoice & {
  role: 'Main' | 'Supporting' | 'Minor'
  rate: string
  pitch: string
  volume: string
  notes: string | null
  aliases: Array<{ id: string; value: string }>
}

function findCharacterProfileLinkOwner(
  characters: SeriesCharacter[],
  currentCharacterId: string,
  characterProfileId: string,
) {
  if (!characterProfileId) return null
  return characters.find((character) => (
    character.id !== currentCharacterId
    && character.characterProfileId === characterProfileId
  )) ?? null
}

type SeriesDetails = {
  id: string
  name: string
  narratorProvider: string
  narratorVoice: string
  narratorRate: string
  narratorPitch: string
  narratorVolume: string
  defaultSpeakerPauseMs: number
  activeCastRevisionId: string | null
  pointOfViewCharacterId: string | null
  narrativeVoiceMode: NarrativeVoiceMode
  books: SeriesBook[]
  characters: SeriesCharacter[]
}

type RebuildBatch = {
  id: string
  status: 'Building' | 'ReadyToActivate' | 'Activated' | 'Failed'
  members: Array<{ id: string; bookId: string; status: string; stagedNarrationJobId: string | null }>
}

type LoadState = 'idle' | 'loading' | 'ready' | 'error'

type LocalClonePreviewAvailability = {
  available: boolean
  label: string | null
}

type LocalClonePreviewAudio = {
  characterId: string
  url: string
}

type LocalClonePreviewRequest = {
  characterId: string
  controller: AbortController
}

const profileDefaults = { rate: '+0%', pitch: '+0Hz', volume: '+0%' }
const previewSentence = '這是一段不含書籍正文的聲線示範。'
const voiceKey = (voice: VoiceOption) => `${voice.provider}\n${voice.voice}`

function voiceLabel(provider: string, voice: string, options: VoiceOption[]) {
  const option = options.find((candidate) => voiceKey(candidate) === `${provider}\n${voice}`)
  return option ? `${option.displayName}（${option.locale}）` : '已設定的固定聲線'
}

export function SeriesCastPanel() {
  const { csrfToken } = useAuthedOutletContext()
  const [searchParams] = useSearchParams()
  const requestedSeriesId = searchParams.get('seriesId') ?? ''
  const [series, setSeries] = useState<SeriesSummary[]>([])
  const [voiceOptions, setVoiceOptions] = useState<VoiceOption[]>([])
  const [libraryBooks, setLibraryBooks] = useState<BookSummary[]>([])
  const [characterProfiles, setCharacterProfiles] = useState<CharacterProfileSummary[]>([])
  const [selectedSeriesId, setSelectedSeriesId] = useState('')
  const [details, setDetails] = useState<SeriesDetails | null>(null)
  const [state, setState] = useState<LoadState>('loading')
  const [message, setMessage] = useState('')
  const [blueMagpieVoice, setBlueMagpieVoice] = useState<BlueMagpieVoice>(BLUE_MAGPIE_VOICES[0].voice)
  const [blueMagpiePreviewUrl, setBlueMagpiePreviewUrl] = useState<string | null>(null)
  const [blueMagpiePreviewState, setBlueMagpiePreviewState] = useState<'idle' | 'loading'>('idle')
  const blueMagpiePreviewRequest = useRef<AbortController | null>(null)
  const [localCloneAvailability, setLocalCloneAvailability] = useState<Record<string, LocalClonePreviewAvailability>>({})
  const [localClonePreviewingCharacterId, setLocalClonePreviewingCharacterId] = useState<string | null>(null)
  const [localClonePreviewAudio, setLocalClonePreviewAudio] = useState<LocalClonePreviewAudio | null>(null)
  const localCloneAvailabilityGeneration = useRef(0)
  const detailsGenerationRef = useRef(0)
  const localClonePreviewRequest = useRef<LocalClonePreviewRequest | null>(null)
  const localClonePreviewAudioRef = useRef<LocalClonePreviewAudio | null>(null)

  const [seriesName, setSeriesName] = useState('')
  const [narratorVoiceKey, setNarratorVoiceKey] = useState('')
  const [configuredNarratorVoiceKey, setConfiguredNarratorVoiceKey] = useState('')
  const [configuredCharacterVoiceKeys, setConfiguredCharacterVoiceKeys] = useState<Record<string, string>>({})
  const [bookId, setBookId] = useState('')
  const [volumeLabel, setVolumeLabel] = useState('')
  const [characterName, setCharacterName] = useState('')
  const [characterRole, setCharacterRole] = useState<SeriesCharacter['role']>('Supporting')
  const [characterVoiceKey, setCharacterVoiceKey] = useState('')
  const [selectedCharacterProfileId, setSelectedCharacterProfileId] = useState('')
  const [aliasCharacterId, setAliasCharacterId] = useState('')
  const [alias, setAlias] = useState('')
  const [formState, setFormState] = useState<LoadState>('idle')

  const [bookDetails, setBookDetails] = useState<Record<string, BookDetails>>({})
  const [draftsByChapter, setDraftsByChapter] = useState<Record<string, SpeechPlanDraft>>({})
  const [batch, setBatch] = useState<RebuildBatch | null>(null)
  const [activateDialogOpen, setActivateDialogOpen] = useState(false)
  const [expandedCharacterId, setExpandedCharacterId] = useState<string | null>(null)
  const [characterProfileSelections, setCharacterProfileSelections] = useState<Record<string, string>>({})
  const [savingCharacterProfileId, setSavingCharacterProfileId] = useState<string | null>(null)

  const loadSeries = useCallback(async () => {
    setState('loading')
    try {
      const [items, voices, books, profiles] = await Promise.all([
        fetchJson<SeriesSummary[]>('/api/series/'),
        fetchJson<VoiceOption[]>('/api/series/voice-options'),
        fetchJson<BookSummary[]>('/api/books'),
        fetchJson<CharacterProfileSummary[]>('/api/character-profiles'),
      ])
      setSeries(items)
      setVoiceOptions(voices)
      setLibraryBooks(books)
      setCharacterProfiles(profiles)
      setNarratorVoiceKey((current) => current || (voices[0] ? voiceKey(voices[0]) : ''))
      setSelectedSeriesId((current) => current || (
        items.some((item) => item.id === requestedSeriesId)
          ? requestedSeriesId
          : items[0]?.id ?? ''))
      setState('ready')
    } catch (error) {
      setState('error')
      setMessage(error instanceof Error ? error.message : '無法讀取系列配音資料。')
    }
  }, [requestedSeriesId])

  const loadDetails = useCallback(async (seriesId: string) => {
    // 換系列時舊回應不可以蓋掉新系列：沒有 guard 的話，晚到的舊 GET 會讓
    // 側欄高亮 B、實際 details 是 A，之後所有表單都寫到錯的系列。
    const generation = detailsGenerationRef.current + 1
    detailsGenerationRef.current = generation
    if (!seriesId) {
      setDetails(null)
      return
    }
    try {
      const detail = await fetchJson<SeriesDetails>(`/api/series/${seriesId}`)
      if (detailsGenerationRef.current !== generation) return
      setDetails(detail)
      setConfiguredNarratorVoiceKey(`${detail.narratorProvider}\n${detail.narratorVoice}`)
      setConfiguredCharacterVoiceKeys(Object.fromEntries(
        detail.characters.map((character) => [character.id, `${character.voiceProvider}\n${character.voice}`]),
      ))
      setAliasCharacterId((current) => current || detail.characters[0]?.id || '')
      setBookId('')
      setVolumeLabel('')
      setBatch(null)
      setExpandedCharacterId(null)
      setCharacterProfileSelections(Object.fromEntries(
        detail.characters.map((character) => [character.id, character.characterProfileId ?? '']),
      ))
    } catch (error) {
      if (detailsGenerationRef.current !== generation) return
      setDetails(null)
      setMessage(error instanceof Error ? error.message : '無法讀取這個系列。')
    }
  }, [])

  useEffect(() => {
    void loadSeries()
  }, [loadSeries])

  useEffect(() => {
    void loadDetails(selectedSeriesId)
  }, [loadDetails, selectedSeriesId])

  useEffect(() => {
    if (!details) {
      setConfiguredNarratorVoiceKey('')
      setConfiguredCharacterVoiceKeys({})
      setCharacterProfileSelections({})
      return
    }

    setConfiguredNarratorVoiceKey(`${details.narratorProvider}\n${details.narratorVoice}`)
    setConfiguredCharacterVoiceKeys(Object.fromEntries(
      details.characters.map((character) => [character.id, `${character.voiceProvider}\n${character.voice}`]),
    ))
    setCharacterProfileSelections(Object.fromEntries(
      details.characters.map((character) => [character.id, character.characterProfileId ?? '']),
    ))
  }, [details])

  useEffect(() => () => {
    if (blueMagpiePreviewUrl) URL.revokeObjectURL(blueMagpiePreviewUrl)
  }, [blueMagpiePreviewUrl])

  useEffect(() => () => {
    const pending = blueMagpiePreviewRequest.current
    blueMagpiePreviewRequest.current = null
    pending?.abort()
  }, [])

  useEffect(() => {
    const generation = localCloneAvailabilityGeneration.current + 1
    localCloneAvailabilityGeneration.current = generation
    const controller = new AbortController()
    setLocalCloneAvailability({})

    if (details) {
      const requestedSeriesId = details.id
      void Promise.all(details.characters.map(async (character) => {
        try {
          const availability = await fetchJson<LocalClonePreviewAvailability>(
            `/api/series/${requestedSeriesId}/characters/${character.id}/local-clone-preview`,
            { signal: controller.signal },
          )
          return [character.id, availability] as const
        } catch {
          return [character.id, { available: false, label: null }] as const
        }
      })).then((entries) => {
        if (controller.signal.aborted || localCloneAvailabilityGeneration.current !== generation) return
        setLocalCloneAvailability(Object.fromEntries(entries))
      })
    }

    return () => {
      localCloneAvailabilityGeneration.current += 1
      controller.abort()
    }
  }, [details])

  useEffect(() => {
    const request = localClonePreviewRequest.current
    localClonePreviewRequest.current = null
    request?.controller.abort()
    setLocalClonePreviewingCharacterId(null)

    const audio = localClonePreviewAudioRef.current
    localClonePreviewAudioRef.current = null
    if (audio) URL.revokeObjectURL(audio.url)
    setLocalClonePreviewAudio(null)
  }, [selectedSeriesId])

  useEffect(() => () => {
    const request = localClonePreviewRequest.current
    localClonePreviewRequest.current = null
    request?.controller.abort()
    const audio = localClonePreviewAudioRef.current
    localClonePreviewAudioRef.current = null
    if (audio) URL.revokeObjectURL(audio.url)
  }, [])

  useEffect(() => {
    if (!details) {
      setBookDetails({})
      setDraftsByChapter({})
      return
    }

    let stale = false
    void (async () => {
      // 一本書失敗不可以讓整批消失（否則審核區永久顯示「正在讀取…」）：
      // 保留成功的部分，失敗時給出明確訊息。
      const results = await Promise.allSettled(details.books.map(async (member) => {
        const book = await fetchJson<BookDetails>(`/api/books/${member.bookId}`)
        return [member.bookId, book] as const
      }))
      if (stale) return
      const loadedBooks = results
        .filter((result): result is PromiseFulfilledResult<readonly [string, BookDetails]> => result.status === 'fulfilled')
        .map((result) => result.value)
      if (results.some((result) => result.status === 'rejected')) {
        setMessage('部分書籍資料讀取失敗，劇本審核清單可能不完整；請重新整理頁面。')
      }
      const detailsById = Object.fromEntries(loadedBooks)
      setBookDetails(detailsById)

      const loadedDrafts = await Promise.all(loadedBooks.flatMap(([, book]) => book.chapters.map(async (chapter) => {
        try {
          const draft = await fetchJson<SpeechPlanDraft>(`/api/series/${details.id}/books/${book.id}/chapters/${chapter.id}/speech-plan`)
          return [chapter.id, draft] as const
        } catch {
          return null
        }
      })))
      if (stale) return
      setDraftsByChapter(Object.fromEntries(loadedDrafts.filter((item): item is readonly [string, SpeechPlanDraft] => item !== null)))
    })()

    return () => {
      stale = true
    }
  }, [details])

  useEffect(() => {
    if (!details || !batch || batch.status !== 'Building') return
    // 批次可能在輪詢期間被清除（例如另一個分頁重建時清掉 Failed 批次，GET 變 404）；
    // 連續失敗三次就停止追蹤，不要無限空轉。
    let consecutiveFailures = 0
    const timer = window.setInterval(() => {
      fetchJson<RebuildBatch>(`/api/series/${details.id}/narration-rebuilds/${batch.id}`)
        .then((updated) => {
          consecutiveFailures = 0
          setBatch(updated)
        })
        .catch(() => {
          consecutiveFailures += 1
          if (consecutiveFailures >= 3) {
            window.clearInterval(timer)
            setBatch(null)
            setMessage('無法再取得重建批次狀態（批次可能已被清除），已停止追蹤；請重新整理頁面確認。')
          }
        })
    }, 2_000)
    return () => window.clearInterval(timer)
  }, [batch, details])

  const eligibleBooks = useMemo(() => {
    const memberIds = new Set(details?.books.map((member) => member.bookId) ?? [])
    return libraryBooks.filter((book) => book.authorizedTextAvailable && book.status !== 'Linked' && !memberIds.has(book.id))
  }, [details, libraryBooks])

  const manualCharacterVoiceOptions = useMemo(() => {
    if (!details) return []
    return voiceOptions.filter((option) => option.provider !== CUSTOM_VOICE_PROVIDER
      && (option.provider === details.narratorProvider
        || (details.narratorProvider === CUSTOM_VOICE_PROVIDER && option.provider === EDGE_VOICE_PROVIDER)))
  }, [details, voiceOptions])

  const configuredNarratorVoice = useMemo(
    () => voiceOptions.find((option) => voiceKey(option) === configuredNarratorVoiceKey) ?? null,
    [configuredNarratorVoiceKey, voiceOptions],
  )

  const configuredCharacterVoiceOptions = useMemo(() => {
    if (!configuredNarratorVoice) return []
    return voiceOptions.filter((option) => option.provider === configuredNarratorVoice.provider
      || (configuredNarratorVoice.provider === CUSTOM_VOICE_PROVIDER
        && option.provider === EDGE_VOICE_PROVIDER))
  }, [configuredNarratorVoice, voiceOptions])

  useEffect(() => {
    if (!details) return
    setCharacterVoiceKey((current) => manualCharacterVoiceOptions.some((option) => voiceKey(option) === current)
      ? current
      : manualCharacterVoiceOptions[0] ? voiceKey(manualCharacterVoiceOptions[0]) : '')
    if (details.narratorProvider !== CUSTOM_VOICE_PROVIDER) {
      setSelectedCharacterProfileId('')
    }
  }, [details, manualCharacterVoiceOptions])

  const reviewEntries = useMemo(() => {
    if (!details) return []
    return details.books
      .slice()
      .sort((left, right) => left.sortOrder - right.sortOrder)
      .flatMap((member) => {
        const book = bookDetails[member.bookId]
        return book ? book.chapters.map((chapter) => ({ book, chapter, draft: draftsByChapter[chapter.id] ?? null })) : []
      })
  }, [bookDetails, details, draftsByChapter])

  function previewVoice(provider: string, voice: string) {
    if (provider === BLUE_MAGPIE_PROVIDER
      && BLUE_MAGPIE_VOICES.some((candidate) => candidate.voice === voice)) {
      setBlueMagpieVoice(voice as BlueMagpieVoice)
      void previewBlueMagpieVoice(voice as BlueMagpieVoice)
      return
    }
    if (provider === VOAI_VOICE_PROVIDER) {
      setMessage('VoAI 聲線無法由瀏覽器 speechSynthesis 模擬；尚需伺服器試音功能，請以正式配音成品為準。')
      return
    }
    if (!('speechSynthesis' in window)) {
      setMessage('這個瀏覽器不支援安全的固定示範句 preview。')
      return
    }
    window.speechSynthesis.cancel()
    const utterance = new SpeechSynthesisUtterance(previewSentence)
    const exactVoice = window.speechSynthesis.getVoices().find((candidate) => candidate.name === voice)
    if (exactVoice) utterance.voice = exactVoice
    utterance.lang = exactVoice?.lang ?? 'zh-TW'
    window.speechSynthesis.speak(utterance)
    setMessage('正在播放固定示範句；瀏覽器找不到同名 voice 時會使用本機語音，最終以系列目前設定的語音服務成品為準。')
  }

  async function previewBlueMagpieVoice(requestedVoice: BlueMagpieVoice = blueMagpieVoice) {
    blueMagpiePreviewRequest.current?.abort()
    const controller = new AbortController()
    blueMagpiePreviewRequest.current = controller
    setBlueMagpiePreviewUrl(null)
    setBlueMagpiePreviewState('loading')
    setMessage('正在由主機本機生成固定示範句；不會呼叫 VoAI 或傳送小說正文。')
    try {
      const audio = await fetchBlob('/api/series/voice-preview', {
        method: 'POST',
        csrfToken,
        body: { provider: BLUE_MAGPIE_PROVIDER, voice: requestedVoice },
        signal: controller.signal,
      })
      if (controller.signal.aborted) return
      setBlueMagpiePreviewUrl(URL.createObjectURL(audio))
      setMessage('本機台灣華語試音已完成；這只是固定示範句，尚未啟用整本配音。')
    } catch (error) {
      if (controller.signal.aborted) return
      setMessage(error instanceof Error ? error.message : '本機台灣華語試音失敗。')
    } finally {
      if (blueMagpiePreviewRequest.current === controller) {
        blueMagpiePreviewRequest.current = null
        setBlueMagpiePreviewState('idle')
      }
    }
  }

  function selectConfiguredNarrator(nextKey: string) {
    setConfiguredNarratorVoiceKey(nextKey)
    if (!details) return
    const narrator = voiceOptions.find((option) => voiceKey(option) === nextKey)
    if (!narrator) return
    const compatible = voiceOptions.filter((option) => option.provider === narrator.provider
      || (narrator.provider === CUSTOM_VOICE_PROVIDER && option.provider === EDGE_VOICE_PROVIDER))
    setConfiguredCharacterVoiceKeys((current) => Object.fromEntries(details.characters.map((character) => {
      const selected = compatible.find((option) => voiceKey(option) === current[character.id])
      return [character.id, voiceKey(selected ?? compatible[0] ?? narrator)]
    })))
  }

  async function configureSeriesVoices(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!details || !configuredNarratorVoice) return
    const characters = details.characters.map((character) => {
      const selected = configuredCharacterVoiceOptions.find(
        (option) => voiceKey(option) === configuredCharacterVoiceKeys[character.id],
      )
      return selected ? {
        characterId: character.id,
        voiceProvider: selected.provider,
        voice: selected.voice,
      } : null
    })
    if (characters.some((character) => character === null)) {
      setMessage('請先替每一位角色選擇與旁白 provider 相容的聲線。')
      return
    }

    setFormState('loading')
    try {
      const updated = await fetchJson<SeriesDetails>(`/api/series/${details.id}/voices`, {
        method: 'PUT',
        csrfToken,
        body: {
          narratorProvider: configuredNarratorVoice.provider,
          narratorVoice: configuredNarratorVoice.voice,
          characters,
        },
      })
      setDetails(updated)
      setBatch(null)
      setConfiguredNarratorVoiceKey(`${updated.narratorProvider}\n${updated.narratorVoice}`)
      setConfiguredCharacterVoiceKeys(Object.fromEntries(
        updated.characters.map((character) => [character.id, `${character.voiceProvider}\n${character.voice}`]),
      ))
      setMessage('已原子切換整個系列聲線；舊成品仍保持啟用，請重新建立 staged 配音後再人工切換。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '系列聲線切換失敗。')
    } finally {
      setFormState('idle')
    }
  }

  async function createSeries(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!seriesName.trim() || !narratorVoiceKey) return
    const selectedVoice = voiceOptions.find((option) => voiceKey(option) === narratorVoiceKey)
    if (!selectedVoice) return
    setFormState('loading')
    try {
      const created = await fetchJson<SeriesDetails>('/api/series/', {
        method: 'POST',
        csrfToken,
        body: {
          name: seriesName.trim(),
          narratorProvider: selectedVoice.provider,
          narratorVoice: selectedVoice.voice,
          narratorRate: profileDefaults.rate,
          narratorPitch: profileDefaults.pitch,
          narratorVolume: profileDefaults.volume,
          defaultSpeakerPauseMs: 180,
        },
      })
      setSeries((current) => [{ id: created.id, name: created.name, bookCount: 0, characterCount: 0, activeCastRevisionId: null, createdAt: '', updatedAt: '' }, ...current])
      setSelectedSeriesId(created.id)
      setDetails(created)
      setSeriesName('')
      setMessage('已建立系列；接著加入每一冊與固定角色聲線。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '建立系列失敗。')
    } finally {
      setFormState('idle')
    }
  }

  async function addBook(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!details || !bookId) return
    setFormState('loading')
    try {
      const updated = await fetchJson<SeriesDetails>(`/api/series/${details.id}/books`, {
        method: 'POST',
        csrfToken,
        body: { bookId, volumeLabel: volumeLabel.trim() || '未命名冊次', sortOrder: details.books.length + 1 },
      })
      setDetails(updated)
      setBookId('')
      setVolumeLabel('')
      setMessage('已加入系列。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '加入系列失敗。')
    } finally {
      setFormState('idle')
    }
  }

  async function addCharacter(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const usingLibraryCharacter = details?.narratorProvider === CUSTOM_VOICE_PROVIDER
      && Boolean(selectedCharacterProfileId)
    const libraryCharacter = characterProfiles.find((profile) => profile.id === selectedCharacterProfileId)
    const libraryCharacterAlreadyLinked = details?.characters.some(
      (character) => character.characterProfileId === selectedCharacterProfileId,
    ) ?? false
    const resolvedName = usingLibraryCharacter ? libraryCharacter?.canonicalName ?? '' : characterName.trim()
    if (!details || !resolvedName) return
    if (!usingLibraryCharacter && !characterVoiceKey) return
    if (usingLibraryCharacter && (!libraryCharacter?.isActive || libraryCharacterAlreadyLinked)) {
      setMessage('這個角色庫角色已停用或已被本系列使用，請重新選擇。')
      return
    }

    const customVoice = voiceOptions.find((option) => option.provider === CUSTOM_VOICE_PROVIDER)
    const selectedVoice = usingLibraryCharacter
      ? customVoice
      : manualCharacterVoiceOptions.find((option) => voiceKey(option) === characterVoiceKey)
    if (!selectedVoice) return

    setFormState('loading')
    try {
      const updated = await fetchJson<SeriesDetails>(`/api/series/${details.id}/characters`, {
        method: 'POST',
        csrfToken,
        body: {
          canonicalName: resolvedName,
          role: characterRole,
          voiceProvider: selectedVoice.provider,
          voice: selectedVoice.voice,
          rate: profileDefaults.rate,
          pitch: profileDefaults.pitch,
          volume: profileDefaults.volume,
          notes: null,
          characterProfileId: usingLibraryCharacter ? selectedCharacterProfileId : null,
        },
      })
      setDetails(updated)
      setCharacterName('')
      setSelectedCharacterProfileId('')
      setAliasCharacterId((current) => current || updated.characters.at(-1)?.id || '')
      setMessage(usingLibraryCharacter ? '已從角色庫選入這個角色，聲線沿用角色庫設定。' : '角色與固定聲線已加入這個系列。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '新增角色失敗。')
    } finally {
      setFormState('idle')
    }
  }

  async function previewLocalClone(character: SeriesCharacter) {
    if (!details || localClonePreviewingCharacterId !== null) return
    const availability = localCloneAvailability[character.id]
    if (!availability?.available) {
      setMessage('這個角色目前沒有可用的本機私人試音資產。')
      return
    }

    const previousAudio = localClonePreviewAudioRef.current
    localClonePreviewAudioRef.current = null
    if (previousAudio) URL.revokeObjectURL(previousAudio.url)
    setLocalClonePreviewAudio(null)

    const controller = new AbortController()
    const request = { characterId: character.id, controller }
    localClonePreviewRequest.current = request
    setLocalClonePreviewingCharacterId(character.id)
    setMessage(`正在用「${availability.label ?? '本機私人試音聲線'}」生成私人固定句試音；不會改變正式配音或 active audio。`)
    try {
      const blob = await fetchBlob(
        `/api/series/${details.id}/characters/${character.id}/local-clone-preview`,
        {
          method: 'POST',
          csrfToken,
          body: { text: previewSentence },
          signal: controller.signal,
        },
      )
      if (controller.signal.aborted || localClonePreviewRequest.current !== request) return
      const nextAudio = { characterId: character.id, url: URL.createObjectURL(blob) }
      localClonePreviewAudioRef.current = nextAudio
      setLocalClonePreviewAudio(nextAudio)
      setMessage('本機私人試音已完成；系列正式 provider、cast revision 與已啟用音訊均未變更。')
    } catch (error) {
      if (controller.signal.aborted) return
      setMessage(error instanceof Error ? error.message : '本機私人試音失敗。')
    } finally {
      if (localClonePreviewRequest.current === request) {
        localClonePreviewRequest.current = null
        setLocalClonePreviewingCharacterId(null)
      }
    }
  }

  async function saveCharacterProfileLink(event: FormEvent<HTMLFormElement>, character: SeriesCharacter) {
    event.preventDefault()
    if (!details || formState === 'loading' || savingCharacterProfileId !== null) return

    const characterProfileId = characterProfileSelections[character.id] ?? ''
    const selectedProfile = characterProfiles.find((profile) => profile.id === characterProfileId)
    if (characterProfileId
      && (!selectedProfile || (!selectedProfile.isActive && character.characterProfileId !== characterProfileId))) {
      setMessage('選擇的角色庫角色已停用或不存在，請重新選擇。')
      return
    }
    const linkedByAnotherCharacter = findCharacterProfileLinkOwner(
      details.characters,
      character.id,
      characterProfileId,
    )
    if (linkedByAnotherCharacter) {
      setMessage(`角色庫「${selectedProfile?.canonicalName ?? '未知角色'}」已連結到 ${linkedByAnotherCharacter.canonicalName}，同一系列不可重複使用。`)
      return
    }

    setFormState('loading')
    setSavingCharacterProfileId(character.id)
    try {
      const updated = await fetchJson<SeriesDetails>(`/api/series/${details.id}/characters/${character.id}/character-profile`, {
        method: 'PUT',
        csrfToken,
        body: { characterProfileId: characterProfileId || null },
      })
      setDetails(updated)
      setBatch(null)
      if (!characterProfileId && expandedCharacterId === character.id) {
        setExpandedCharacterId(null)
      }
      setMessage(selectedProfile
        ? `已將 ${character.canonicalName} 連結到角色庫「${selectedProfile.canonicalName}」；目前固定聲線與已啟用音訊不變。`
        : `已取消 ${character.canonicalName} 的角色庫連結；目前固定聲線與已啟用音訊不變。`)
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '儲存角色庫連結失敗。')
    } finally {
      setSavingCharacterProfileId(null)
      setFormState('idle')
    }
  }

  async function setPointOfViewCharacter(characterId: string) {
    if (!details) return
    if (!characterId && details.narrativeVoiceMode === 'PointOfViewInnerMonologue') {
      setMessage('視角角色內心敘事模式必須保留視角角色；請先切回獨立旁白再取消。')
      return
    }
    setFormState('loading')
    try {
      const updated = await fetchJson<SeriesDetails>(`/api/series/${details.id}/point-of-view-character`, {
        method: 'PUT',
        csrfToken,
        body: { characterId: characterId || null },
      })
      setDetails(updated)
      setBatch(null)
      setMessage(characterId
        ? '已設定視角角色；逐章說話者草稿需要重新建立，目前已啟用音訊不變。'
        : '已取消視角角色設定；逐章說話者草稿需要重新建立，目前已啟用音訊不變。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '設定視角角色失敗。')
    } finally {
      setFormState('idle')
    }
  }

  async function configureNarrativeVoice(mode: NarrativeVoiceMode) {
    if (!details || mode === details.narrativeVoiceMode) return
    if (mode === 'PointOfViewInnerMonologue' && !details.pointOfViewCharacterId) {
      setMessage('請先選擇視角角色，才能啟用視角角色內心敘事。')
      return
    }

    setFormState('loading')
    try {
      const updated = await fetchJson<SeriesDetails>(`/api/series/${details.id}/narrative-voice`, {
        method: 'PUT',
        csrfToken,
        body: {
          mode,
          pointOfViewCharacterId: details.pointOfViewCharacterId,
        },
      })
      setDetails(updated)
      setBatch(null)
      setMessage(mode === 'PointOfViewInnerMonologue'
        ? '已改用視角角色的聲線朗讀正文內心敘事；逐章說話者草稿需要重新建立，目前已啟用音訊不變。'
        : '已改回獨立旁白朗讀正文；逐章說話者草稿需要重新建立，目前已啟用音訊不變。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '設定正文敘事聲線失敗。')
    } finally {
      setFormState('idle')
    }
  }

  async function addAlias(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!details || !aliasCharacterId || !alias.trim()) return
    setFormState('loading')
    try {
      const updated = await fetchJson<SeriesDetails>(`/api/series/${details.id}/characters/${aliasCharacterId}/aliases`, {
        method: 'POST',
        csrfToken,
        body: { alias: alias.trim() },
      })
      setDetails(updated)
      setAlias('')
      setMessage('別名已加入；重複或跨角色衝突會由系列規則拒絕。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '加入別名失敗。')
    } finally {
      setFormState('idle')
    }
  }

  async function activateBatch() {
    if (!details || !batch) return
    setActivateDialogOpen(false)
    try {
      const activated = await fetchJson<RebuildBatch>(`/api/series/${details.id}/narration-rebuilds/${batch.id}/activate`, {
        method: 'POST',
        csrfToken,
        body: {},
      })
      setBatch(activated)
      setMessage('已原子啟用完整 series cast epoch；舊音訊保留為歷史版本。')
      void loadDetails(details.id)
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '尚未符合啟用條件。')
    }
  }

  if (state === 'loading') {
    return <main className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10"><div className="library-state">正在讀取系列配音控制台…</div></main>
  }

  if (state === 'error') {
    return <main className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10"><div className="library-state border-rose-300 text-rose-700">{message || '系列配音控制台暫時無法使用。'}</div></main>
  }

  return (
    <main className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
      <section className="rounded-3xl border border-stone-200 bg-white p-5 sm:p-7">
        <p className="text-xs font-semibold uppercase tracking-[.22em] text-amber-700">Series cast</p>
        <h1 className="mt-2 font-serif text-3xl text-stone-900">多角色系列配音</h1>
        <p className="mt-3 max-w-3xl text-sm leading-6 text-stone-500">同一系列固定旁白與角色聲線。先逐章校正、鎖定劇本，再建立整批 staged 音訊；任何一冊失敗都不會偷換目前版本。</p>

        <form className="mt-6 grid grid-cols-1 gap-3 border-t border-stone-200 pt-5 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] sm:items-end" onSubmit={createSeries}>
          <label className="text-xs text-stone-500">新系列名稱<input className="auth-input mt-2" maxLength={200} onChange={(event) => setSeriesName(event.target.value)} required value={seriesName} /></label>
          <label className="text-xs text-stone-500">固定旁白聲線<select className="auth-input mt-2" onChange={(event) => setNarratorVoiceKey(event.target.value)} value={narratorVoiceKey}><option value="">選擇聲線</option>{voiceOptions.map((option) => <option key={voiceKey(option)} value={voiceKey(option)}>{option.displayName}（{option.locale}）</option>)}</select></label>
          <button className="secondary-button disabled:cursor-wait disabled:opacity-60" disabled={formState === 'loading' || !seriesName.trim() || !narratorVoiceKey} type="submit">建立系列</button>
        </form>
      </section>

      <section className="mt-6 rounded-3xl border border-sky-200 bg-sky-50 p-5 sm:p-7" aria-label="本機台灣華語試音">
        <p className="text-xs font-semibold uppercase tracking-[.22em] text-sky-700">Self-hosted preview</p>
        <h2 className="mt-2 font-serif text-2xl text-stone-900">本機台灣華語試音</h2>
        <p className="mt-2 max-w-3xl text-sm leading-6 text-stone-600">僅生成伺服器內建的固定示範句，不傳送小說正文、不呼叫 VoAI，也不會建立正式配音工作。</p>
        <div className="mt-4 flex flex-wrap items-end gap-3">
          <label className="min-w-64 text-xs text-stone-500">
            BlueMagpie 聲線
            <select
              className="auth-input mt-2"
              onChange={(event) => {
                blueMagpiePreviewRequest.current?.abort()
                blueMagpiePreviewRequest.current = null
                setBlueMagpiePreviewState('idle')
                setBlueMagpiePreviewUrl(null)
                setBlueMagpieVoice(event.target.value as BlueMagpieVoice)
              }}
              value={blueMagpieVoice}
            >
              {BLUE_MAGPIE_VOICES.map((option) => <option key={option.voice} value={option.voice}>{option.label}</option>)}
            </select>
          </label>
          <button className="secondary-button disabled:cursor-wait disabled:opacity-60" disabled={blueMagpiePreviewState === 'loading'} onClick={() => void previewBlueMagpieVoice()} type="button">
            {blueMagpiePreviewState === 'loading' ? '本機生成中…' : '生成固定試音'}
          </button>
        </div>
        {blueMagpiePreviewUrl && <audio autoPlay className="mt-4 w-full max-w-xl" controls src={blueMagpiePreviewUrl}>你的瀏覽器無法播放這段試音。</audio>}
      </section>

      <section className="mt-6 grid grid-cols-1 gap-6 lg:grid-cols-[18rem_minmax(0,1fr)]">
        <aside className="rounded-3xl border border-stone-200 bg-white p-4">
          <h2 className="font-serif text-xl text-stone-900">我的系列</h2>
          <div className="mt-3 space-y-2">
            {series.map((item) => <button className={`w-full rounded-xl border p-3 text-left text-sm transition ${item.id === selectedSeriesId ? 'border-amber-300 bg-amber-50 text-amber-800' : 'border-stone-200 bg-stone-50 text-stone-500 hover:text-stone-800'}`} key={item.id} onClick={() => setSelectedSeriesId(item.id)} type="button"><span className="block truncate">{item.name}</span><span className="mt-1 block text-xs opacity-70">{item.bookCount} 冊 · {item.characterCount} 角色</span></button>)}
            {series.length === 0 && <p className="px-1 text-sm leading-6 text-stone-500">先建立第一個系列；不會用書名自動猜測成員。</p>}
          </div>
        </aside>

        <section>
          {!details && <div className="library-state">選擇或建立一個系列，開始設定固定角色聲線。</div>}
          {details && (
            <>
              <div className="rounded-3xl border border-stone-200 bg-white p-5 sm:p-7">
                <div className="flex flex-wrap items-start justify-between gap-4"><div><p className="text-xs text-stone-500">系列</p><h2 className="mt-1 font-serif text-3xl text-stone-900">{details.name}</h2><p className="mt-2 text-sm text-stone-500">旁白：{voiceLabel(details.narratorProvider, details.narratorVoice, voiceOptions)} · 對白間隔 {details.defaultSpeakerPauseMs}ms</p></div><button className="secondary-button px-3 py-2 text-xs disabled:cursor-not-allowed disabled:opacity-60" disabled={details.narratorProvider === VOAI_VOICE_PROVIDER} onClick={() => previewVoice(details.narratorProvider, details.narratorVoice)} type="button">{details.narratorProvider === VOAI_VOICE_PROVIDER ? 'VoAI 需伺服器試音' : '播放固定示範句'}</button></div>

                <form className="mt-6 border-t border-stone-200 pt-5" onSubmit={configureSeriesVoices}>
                  <div className="flex flex-wrap items-start justify-between gap-3">
                    <div>
                      <h3 className="font-serif text-xl text-stone-900">整系列聲線設定</h3>
                      <p className="mt-1 max-w-2xl text-sm leading-6 text-stone-500">旁白與所有角色會一次驗證、一次切換，避免留下無法合成的 mixed-provider 設定。這是下一次 staged rebuild 的聲線設定；儲存後不會立刻替換既有已啟用音訊。</p>
                    </div>
                    <button
                      className="secondary-button px-3 py-2 text-xs disabled:cursor-not-allowed disabled:opacity-60"
                      disabled={formState === 'loading' || !configuredNarratorVoice || details.characters.some((character) => !configuredCharacterVoiceKeys[character.id])}
                      type="submit"
                    >
                      儲存整系列聲線
                    </button>
                  </div>
                  <label className="mt-4 block max-w-md text-xs text-stone-500">
                    固定旁白聲線
                    <select className="auth-input mt-2" onChange={(event) => selectConfiguredNarrator(event.target.value)} value={configuredNarratorVoiceKey}>
                      <option value="">選擇聲線</option>
                      {voiceOptions.map((option) => <option key={voiceKey(option)} value={voiceKey(option)}>{option.displayName}（{option.locale}）</option>)}
                    </select>
                  </label>
                  {configuredNarratorVoice?.provider === BLUE_MAGPIE_PROVIDER && (
                    <p className="mt-3 rounded-xl border border-sky-200 bg-sky-50 px-3 py-2 text-xs leading-5 text-sky-800">BlueMagpie BM1 目前只有內建女聲與男聲兩種 timbre；整系列切換會把旁白與角色的 rate、pitch、volume 重設為中性，下一次 staged rebuild 才會使用。模型權重授權標示為 other，目前限定私人自架使用，不代表可重新散布或商業使用。</p>
                  )}
                  {details.characters.length > 0 && (
                    <div className="mt-4 grid grid-cols-1 gap-3 sm:grid-cols-2">
                      {details.characters.map((character) => (
                        <label className="text-xs text-stone-500" key={character.id}>
                          {character.canonicalName}
                          <select
                            className="auth-input mt-2"
                            onChange={(event) => setConfiguredCharacterVoiceKeys((current) => ({ ...current, [character.id]: event.target.value }))}
                            value={configuredCharacterVoiceKeys[character.id] ?? ''}
                          >
                            <option value="">選擇相容聲線</option>
                            {configuredCharacterVoiceOptions.map((option) => <option key={voiceKey(option)} value={voiceKey(option)}>{option.displayName}（{option.locale}）</option>)}
                          </select>
                        </label>
                      ))}
                    </div>
                  )}
                </form>

                <section className="mt-6 border-t border-stone-200 pt-5" aria-label="系列書籍"><h3 className="font-serif text-xl text-stone-900">系列書籍</h3><div className="mt-3 space-y-2">{details.books.slice().sort((left, right) => left.sortOrder - right.sortOrder).map((member) => <div className="rounded-xl border border-stone-200 bg-stone-50 p-3" key={member.id}><p className="text-sm text-stone-800">{member.volumeLabel} · {member.bookTitle}</p><p className="mt-1 text-xs text-stone-500">membership revision {member.membershipRevision}</p></div>)}{details.books.length === 0 && <p className="text-sm text-stone-500">尚未加入正文書籍。</p>}</div>
                  <form className="mt-4 grid grid-cols-1 gap-3 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] sm:items-end" onSubmit={addBook}><label className="text-xs text-stone-500">從可用正文加入<select className="auth-input mt-2" onChange={(event) => setBookId(event.target.value)} value={bookId}><option value="">選擇書籍</option>{eligibleBooks.map((book) => <option key={book.id} value={book.id}>{book.title}</option>)}</select></label><label className="text-xs text-stone-500">冊次標籤<input className="auth-input mt-2" maxLength={100} onChange={(event) => setVolumeLabel(event.target.value)} placeholder="第一冊" value={volumeLabel} /></label><button className="secondary-button" disabled={formState === 'loading' || !bookId} type="submit">加入系列</button></form>
                </section>

                <section className="mt-7 border-t border-stone-200 pt-5" aria-label="固定角色聲線"><h3 className="font-serif text-xl text-stone-900">固定角色聲線</h3><p className="mt-1 text-sm text-stone-500">角色與別名都限制在這個 owner 的系列；跨角色 alias 衝突會直接拒絕。角色也可以直接從<Link className="text-amber-700 underline" to="/characters">角色庫</Link>選入，跨系列共用同一組自訂聲線。</p>
                  <p className="mt-2 rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-xs leading-5 text-amber-800">每位既有角色都能在下方後補或更換角色庫連結。儲存時會完整保留名稱、定位、provider、voice、rate、pitch、volume 與 notes；也不會立刻替換已啟用音訊。系統允許跨系列共用角色庫角色，但同一系列中已被另一角色連結的選項會停用，避免誤用同一個聲線身份。</p>
                  <label className="mt-4 block max-w-sm text-xs text-stone-500">
                    正文敘事聲線
                    <select
                      className="auth-input mt-2 disabled:opacity-60"
                      disabled={formState === 'loading'}
                      onChange={(event) => void configureNarrativeVoice(event.target.value as NarrativeVoiceMode)}
                      value={details.narrativeVoiceMode}
                    >
                      <option value="IndependentNarrator">獨立旁白</option>
                      <option disabled={!details.pointOfViewCharacterId} value="PointOfViewInnerMonologue">視角角色內心獨白</option>
                    </select>
                  </label>
                  <p className="mt-1 max-w-xl text-xs text-stone-400">「視角角色內心獨白」會使用視角角色聲線朗讀正文與內心想法；必須先選好視角角色。切換後需重新建立逐章說話者草稿，既有已啟用音訊不會立即變更。</p>
                  <label className="mt-4 block max-w-sm text-xs text-stone-500">
                    視角角色（第一人稱敘事者）
                    <select
                      className="auth-input mt-2 disabled:opacity-60"
                      disabled={formState === 'loading' || details.characters.length === 0}
                      onChange={(event) => void setPointOfViewCharacter(event.target.value)}
                      value={details.pointOfViewCharacterId ?? ''}
                    >
                      <option disabled={details.narrativeVoiceMode === 'PointOfViewInnerMonologue'} value="">{details.narrativeVoiceMode === 'PointOfViewInnerMonologue' ? '內心敘事模式必須保留視角角色' : '不設定（不影響「我」的對白判斷）'}</option>
                      {details.characters.map((character) => <option key={character.id} value={character.id}>{character.canonicalName}</option>)}
                    </select>
                  </label>
                  <p className="mt-1 max-w-sm text-xs text-stone-400">設定後，全書「我＋說／問／道」等第一人稱自述對白會自動歸給這個角色，適合第一人稱主角敘事的書。</p>
                  <div className="mt-3 space-y-2">
                    {details.characters.map((character) => {
                      const selectedProfileId = characterProfileSelections[character.id]
                        ?? character.characterProfileId
                        ?? ''
                      const selectedProfileOwner = findCharacterProfileLinkOwner(
                        details.characters,
                        character.id,
                        selectedProfileId,
                      )
                      return (
                        <div className="rounded-xl border border-stone-200 bg-stone-50 p-3" key={character.id}>
                        <div className="flex flex-wrap items-center justify-between gap-2">
                          <p className="text-sm text-stone-800">{character.canonicalName} · {character.role}</p>
                          <div className="flex items-center gap-3">
                            <button className="text-xs text-amber-700 disabled:cursor-not-allowed disabled:text-stone-400" disabled={character.voiceProvider === VOAI_VOICE_PROVIDER} onClick={() => previewVoice(character.voiceProvider, character.voice)} type="button">{character.voiceProvider === VOAI_VOICE_PROVIDER ? 'VoAI 需伺服器試音' : '播放固定示範句'}</button>
                            {localCloneAvailability[character.id]?.available && (
                              <button
                                className="text-xs font-medium text-sky-700 disabled:cursor-wait disabled:text-stone-400"
                                disabled={localClonePreviewingCharacterId !== null}
                                onClick={() => void previewLocalClone(character)}
                                type="button"
                              >
                                {localClonePreviewingCharacterId === character.id ? '本機合成中…' : '▶ 本機 Clone 私人試音'}
                              </button>
                            )}
                            {character.characterProfileId && (
                              <button
                                className="text-xs text-amber-700"
                                onClick={() => setExpandedCharacterId((current) => (current === character.id ? null : character.id))}
                                type="button"
                              >
                                {expandedCharacterId === character.id ? '收起自訂聲線' : '自訂聲線'}
                              </button>
                            )}
                          </div>
                        </div>
                        <p className="mt-1 text-xs text-stone-500">{voiceLabel(character.voiceProvider, character.voice, voiceOptions)} · 別名：{character.aliases.map((item) => item.value).join('、') || '—'}</p>
                        {localCloneAvailability[character.id]?.available && (
                          <p className="mt-1 text-xs text-sky-700">私人試音：{localCloneAvailability[character.id].label ?? '本機私人試音聲線'}。只用於比較音色，不會切換目前的 Edge 正式聲線。</p>
                        )}
                        {localClonePreviewAudio?.characterId === character.id && (
                          <div className="mt-3 rounded-xl border border-sky-200 bg-sky-50 p-3">
                            <audio autoPlay className="w-full" controls src={localClonePreviewAudio.url}>你的瀏覽器無法播放這段本機私人試音。</audio>
                            <a className="mt-2 inline-block text-xs text-sky-800 underline" download="local-clone-private-preview.wav" href={localClonePreviewAudio.url}>下載本機私人試音</a>
                          </div>
                        )}
                        <form className="mt-3 flex flex-col gap-2 border-t border-stone-200 pt-3 sm:flex-row sm:items-end" onSubmit={(event) => void saveCharacterProfileLink(event, character)}>
                          <label className="min-w-0 flex-1 text-xs text-stone-500">
                            角色庫連結
                            <select
                              aria-label={`${character.canonicalName} 的角色庫連結`}
                              className="auth-input mt-2"
                              disabled={formState === 'loading' || savingCharacterProfileId === character.id}
                              onChange={(event) => setCharacterProfileSelections((current) => ({
                                ...current,
                                [character.id]: event.target.value,
                              }))}
                              value={selectedProfileId}
                            >
                              <option value="">不連結角色庫（保留目前固定聲線）</option>
                              {characterProfiles.map((profile) => {
                                const linkedBy = findCharacterProfileLinkOwner(details.characters, character.id, profile.id)
                                const isCurrentProfile = character.characterProfileId === profile.id
                                const unavailable = Boolean(linkedBy) || (!profile.isActive && !isCurrentProfile)
                                const suffix = linkedBy
                                  ? `（已連結：${linkedBy.canonicalName}）`
                                  : !profile.isActive ? '（未啟用）' : ''
                                return <option disabled={unavailable} key={profile.id} value={profile.id}>{profile.canonicalName}{suffix}</option>
                              })}
                            </select>
                          </label>
                          <button
                            className="secondary-button px-3 py-2 text-xs disabled:cursor-not-allowed disabled:opacity-60"
                            disabled={formState === 'loading'
                              || savingCharacterProfileId !== null
                              || Boolean(selectedProfileOwner)
                              || selectedProfileId === (character.characterProfileId ?? '')}
                            type="submit"
                          >
                            {savingCharacterProfileId === character.id ? '儲存中…' : '儲存單角連結'}
                          </button>
                        </form>
                        {expandedCharacterId === character.id && character.characterProfileId && (
                          <div className="mt-3">
                            <CharacterVoiceProfilesPanel
                              characterName={character.canonicalName}
                              characterProfileId={character.characterProfileId}
                              csrfToken={csrfToken}
                            />
                          </div>
                        )}
                        </div>
                      )
                    })}
                  </div>
                  <form className="mt-4 grid grid-cols-1 gap-3 sm:grid-cols-3" onSubmit={addCharacter}>
                    {details.narratorProvider === CUSTOM_VOICE_PROVIDER ? (
                      <label className="text-xs text-stone-500 sm:col-span-3">
                        從角色庫選擇（可選，選了就沿用角色庫的 3wa 自訂聲線）
                        <select
                          className="auth-input mt-2"
                          onChange={(event) => setSelectedCharacterProfileId(event.target.value)}
                          value={selectedCharacterProfileId}
                        >
                          <option value="">不使用角色庫，改用 Edge fallback</option>
                          {characterProfiles.map((profile) => {
                            const linkedBy = details.characters.find((character) => character.characterProfileId === profile.id)
                            const unavailable = !profile.isActive || Boolean(linkedBy)
                            const suffix = linkedBy
                              ? `（已連結：${linkedBy.canonicalName}）`
                              : !profile.isActive ? '（未啟用）' : ''
                            return <option disabled={unavailable} key={profile.id} value={profile.id}>{profile.canonicalName}{suffix}</option>
                          })}
                        </select>
                      </label>
                    ) : (
                      <p className="rounded-xl border border-stone-200 bg-stone-50 px-3 py-2 text-xs leading-5 text-stone-500 sm:col-span-3">
                        {details.narratorProvider === VOAI_VOICE_PROVIDER
                          ? '這個系列使用 VoAI；不可混用 3wa 角色庫自訂聲線，請從下方選擇 VoAI 聲線。'
                          : '角色庫自訂聲線只支援 3wa 系列；請從下方選擇與旁白相同 provider 的聲線。'}
                      </p>
                    )}
                    <label className="text-xs text-stone-500">
                      角色名稱
                      <input
                        className="auth-input mt-2 disabled:opacity-60"
                        disabled={Boolean(selectedCharacterProfileId)}
                        maxLength={160}
                        onChange={(event) => setCharacterName(event.target.value)}
                        required={!selectedCharacterProfileId}
                        value={selectedCharacterProfileId
                          ? characterProfiles.find((profile) => profile.id === selectedCharacterProfileId)?.canonicalName ?? ''
                          : characterName}
                      />
                    </label>
                    <label className="text-xs text-stone-500">
                      角色定位
                      <select className="auth-input mt-2" onChange={(event) => setCharacterRole(event.target.value as SeriesCharacter['role'])} value={characterRole}>
                        <option value="Main">主要</option>
                        <option value="Supporting">配角</option>
                        <option value="Minor">次要</option>
                      </select>
                    </label>
                    <label className="text-xs text-stone-500">
                      聲線
                      <select
                        className="auth-input mt-2 disabled:opacity-60"
                        disabled={Boolean(selectedCharacterProfileId)}
                        onChange={(event) => setCharacterVoiceKey(event.target.value)}
                        value={selectedCharacterProfileId ? '' : characterVoiceKey}
                      >
                        {selectedCharacterProfileId && <option value="">沿用角色庫自訂聲線</option>}
                        {manualCharacterVoiceOptions.map((option) => (
                          <option key={voiceKey(option)} value={voiceKey(option)}>{option.displayName}（{option.locale}）</option>
                        ))}
                      </select>
                    </label>
                    <button
                      className="secondary-button sm:col-span-3"
                      disabled={formState === 'loading' || (!selectedCharacterProfileId && (!characterName.trim() || !characterVoiceKey))}
                      type="submit"
                    >
                      加入角色與固定聲線
                    </button>
                  </form>
                  <form className="mt-4 grid grid-cols-1 gap-3 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] sm:items-end" onSubmit={addAlias}><label className="text-xs text-stone-500">角色<select className="auth-input mt-2" onChange={(event) => setAliasCharacterId(event.target.value)} value={aliasCharacterId}><option value="">選擇角色</option>{details.characters.map((character) => <option key={character.id} value={character.id}>{character.canonicalName}</option>)}</select></label><label className="text-xs text-stone-500">別名<input className="auth-input mt-2" maxLength={160} onChange={(event) => setAlias(event.target.value)} placeholder="例如：小明" value={alias} /></label><button className="secondary-button" disabled={formState === 'loading' || !aliasCharacterId || !alias.trim()} type="submit">加入別名</button></form>
                </section>
              </div>

              {reviewEntries.length > 0 && (
                <SpeechPlanReview
                  characterVoiceOptions={manualCharacterVoiceOptions}
                  characters={details.characters}
                  csrfToken={csrfToken}
                  entries={reviewEntries}
                  narratorProvider={details.narratorProvider}
                  onAddCharacter={async (request) => {
                    const updated = await fetchJson<SeriesDetails>(`/api/series/${details.id}/characters`, {
                      method: 'POST',
                      csrfToken,
                      body: {
                        canonicalName: request.canonicalName,
                        role: request.role,
                        voiceProvider: request.voiceProvider,
                        voice: request.voice,
                        rate: profileDefaults.rate,
                        pitch: profileDefaults.pitch,
                        volume: profileDefaults.volume,
                        notes: null,
                        characterProfileId: null,
                      },
                    })
                    setDetails(updated)
                    return updated.characters.find((character) => character.canonicalName === request.canonicalName) ?? null
                  }}
                  onDraftUpdated={(draft) => setDraftsByChapter((current) => ({ ...current, [draft.chapterId]: draft }))}
                  onRebuildCreated={(created) => { setBatch({ ...created, members: [] }); void fetchJson<RebuildBatch>(`/api/series/${details.id}/narration-rebuilds/${created.id}`).then(setBatch).catch(() => undefined) }}
                  seriesId={details.id}
                />
              )}
              {details.books.length > 0 && reviewEntries.length === 0 && <div className="library-state mt-8">正在讀取僅屬於你的章節與劇本狀態…</div>}

              {batch && <section className="mt-8 rounded-3xl border border-stone-200 bg-white p-5 sm:p-7" aria-label="staged rebuild 狀態"><div className="flex flex-wrap items-center justify-between gap-3"><div><p className="text-xs font-semibold uppercase tracking-[.22em] text-amber-700">Staged rebuild</p><h2 className="mt-1 font-serif text-2xl text-stone-900">{batch.status}</h2></div>{batch.status === 'ReadyToActivate' && <button className="secondary-button" onClick={() => setActivateDialogOpen(true)} type="button">人工啟用完整系列音訊</button>}</div><ul className="mt-4 space-y-2">{batch.members.map((member) => <li className="flex items-center justify-between gap-3 rounded-xl border border-stone-200 bg-stone-50 p-3 text-sm" key={member.id}><span className="text-stone-700">{details.books.find((book) => book.bookId === member.bookId)?.bookTitle ?? '系列書籍已變更'}</span><span className="text-xs text-stone-500">{member.status}</span></li>)}</ul></section>}
            </>
          )}
        </section>
      </section>
      <p aria-live="polite" className="mt-5 min-h-5 text-sm text-stone-500">{message}</p>
      <ConfirmDialog confirmLabel="啟用完整系列" description="這會在單一交易中切換整個系列的 current audio；只有所有 staged 冊次都完成時才能成功。" onCancel={() => setActivateDialogOpen(false)} onConfirm={() => void activateBatch()} open={activateDialogOpen} title="確定啟用完整多角色系列音訊？" />
    </main>
  )
}
