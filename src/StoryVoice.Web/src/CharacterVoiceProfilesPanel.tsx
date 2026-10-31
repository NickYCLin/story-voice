import { useCallback, useEffect, useRef, useState, type ChangeEvent, type FormEvent } from 'react'

import { apiUrl, fetchJson, responseProblem } from './api'

type VoiceProfile = {
  id: string
  characterProfileId: string
  kind: 'Base' | 'Scene'
  sceneCode: string | null
  mode: 'Design' | 'Clone'
  consentType: string | null
  voicePromptText: string | null
  transcript: string | null
  asrDraftTranscript: string | null
  confirmationTranscriptIntent: string | null
  transcriptConfirmed: boolean
  status: 'Pending' | 'AwaitingTranscriptConfirmation' | 'Ready' | 'Failed'
  referenceAudioDurationSeconds: number | null
  createdAt: string
  updatedAt: string
}

type Slot = { sceneCode: string | null; label: string; description: string }

type PreviewAudio = {
  characterProfileId: string
  profileId: string
  url: string
  fileName: string
}

type PreviewRequestState = {
  characterProfileId: string
  profileId: string
}

type PreviewRequest = PreviewRequestState & {
  controller: AbortController
}

type LoadedProfiles = {
  characterProfileId: string
  items: VoiceProfile[]
}

type CloneOperation = {
  id: string
  newProfileId: string
  oldProfileId: string | null
  type: 'Create' | 'Replace'
  state: 'Staged' | 'RemotePrepared' | 'Activated' | 'NeedsAttention' | 'Rejected'
  kind: 'Base' | 'Scene'
  sceneCode: string | null
  hasRemoteTask: boolean
  attentionCode: string | null
}

type LoadedOperations = {
  characterProfileId: string
  items: CloneOperation[]
}

type ConsentReceiptSummary = {
  fileIdentity: string
  schema: string
  recorderName: string
  recordingDate: string
  consentSignedDate: string
  consentType: string
  usageScopes: string[]
}

type ConsentReceiptState = {
  summary: ConsentReceiptSummary | null
  error: string | null
}

const SLOTS: Slot[] = [
  { sceneCode: null, label: '基礎聲線', description: '沒有更精確情境時的預設聲音，也是日常對話狀態使用的聲音' },
  { sceneCode: 'nervous', label: '緊張', description: '感到緊張或不安時' },
  { sceneCode: 'happy', label: '開心', description: '開心、興奮時' },
  { sceneCode: 'angry', label: '生氣', description: '生氣、提高音量時' },
  { sceneCode: 'sad', label: '難過', description: '難過、低落時' },
]

const DEFAULT_PREVIEW_TEXT = '你好，這是我的聲音示範。'
const MAX_REFERENCE_AUDIO_BYTES = 10 * 1024 * 1024
const MAX_CONSENT_RECEIPT_BYTES = 32 * 1024
const CONSENT_RECEIPT_SCHEMA = 'storyvoice-clone-consent-receipt/v2'
const SUBJECT_ATTESTATION_VERSION = 'storyvoice-clone-subject-consent/v1'
const PRIVATE_EVALUATION_SCOPE = 'storyvoice_private_evaluation'
const FORMAL_NARRATION_SCOPE = 'storyvoice_formal_narration'
const CONSENT_TYPES = new Set(['self_recorded', 'explicit_permission', 'licensed_voice'])
const CONSENT_SCOPES = new Set([
  PRIVATE_EVALUATION_SCOPE,
  FORMAL_NARRATION_SCOPE,
  'public_distribution',
  'commercial_use',
])
const RECEIPT_PROPERTIES = new Set([
  'schema',
  'recorderName',
  'recordingDate',
  'consentSignedDate',
  'consentType',
  'usageScopes',
  'recordingSha256',
  'expectedTranscriptCanonicalSha256',
  'consentSha256',
  'subjectAttestationVersion',
  'generatedAtUtc',
])
// Code-pinned to the verified provider contract; this must not be an environment toggle.
const DESIGN_VOICE_AVAILABLE = false

function formatDuration(seconds: number | null) {
  if (seconds === null) return '—'
  const totalSeconds = Math.round(seconds)
  const minutes = Math.floor(totalSeconds / 60)
  const remainder = totalSeconds % 60
  return `${minutes}:${remainder.toString().padStart(2, '0')}`
}

function safeDownloadFileNamePart(value: string) {
  const withoutControlCharacters = Array.from(value, (character) => {
    const codePoint = character.codePointAt(0) ?? 0
    return codePoint < 32 || codePoint === 127 ? '_' : character
  }).join('')
  const cleaned = withoutControlCharacters
    .trim()
    .replace(/[<>:"/\\|?*]/g, '_')
    .replace(/[. ]+$/g, '')
    .slice(0, 80)
    .replace(/[. ]+$/g, '')
  const candidate = cleaned || '角色'
  return /^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(candidate) ? `_${candidate}` : candidate
}

function audioFileExtension(contentType: string) {
  const mediaType = contentType.split(';', 1)[0].trim().toLowerCase()
  if (mediaType === 'audio/wav' || mediaType === 'audio/x-wav' || mediaType === 'audio/vnd.wave') return 'wav'
  if (mediaType === 'audio/mpeg') return 'mp3'
  if (mediaType === 'audio/mp4' || mediaType === 'audio/x-m4a' || mediaType === 'audio/aac') return 'm4a'
  if (mediaType === 'audio/ogg') return 'ogg'

  const subtype = mediaType.startsWith('audio/') ? mediaType.slice('audio/'.length).replace(/^x-/, '') : ''
  return /^[a-z0-9]+$/.test(subtype) ? subtype : 'audio'
}

function previewSlotLabel(profile: VoiceProfile) {
  if (profile.kind === 'Base') return '基礎聲線'
  return SLOTS.find((slot) => slot.sceneCode === profile.sceneCode)?.label ?? '情境聲線'
}

const CONSENT_TYPE_LABEL: Record<string, string> = {
  self_recorded: '本人親自錄製',
  explicit_permission: '已取得聲音所有人明確同意',
  licensed_voice: '已取得合法授權的聲音',
}

const CONSENT_SCOPE_LABEL: Record<string, string> = {
  [PRIVATE_EVALUATION_SCOPE]: 'StoryVoice 私人評估／試音',
  [FORMAL_NARRATION_SCOPE]: 'StoryVoice 正式有聲書製作',
  public_distribution: '公開散布',
  commercial_use: '商業使用',
}

function receiptFileIdentity(file: File) {
  return `${file.name}:${file.size}:${file.lastModified}`
}

async function parseConsentReceipt(file: File): Promise<ConsentReceiptSummary> {
  if (file.size === 0) throw new Error('授權 receipt 不可為空白。')
  if (file.size > MAX_CONSENT_RECEIPT_BYTES) throw new Error('授權 receipt 不可超過 32 KiB。')
  if (!file.name.toLowerCase().endsWith('.json')) throw new Error('授權 receipt 必須是 JSON 檔案。')

  let receipt: unknown
  try {
    receipt = JSON.parse(await file.text())
  } catch {
    throw new Error('授權 receipt 不是有效的 JSON。')
  }

  if (!receipt || typeof receipt !== 'object' || Array.isArray(receipt)) {
    throw new Error('授權 receipt 根節點必須是 JSON object。')
  }

  const data = receipt as Record<string, unknown>
  const keys = Object.keys(data)
  if (keys.length !== RECEIPT_PROPERTIES.size || keys.some((key) => !RECEIPT_PROPERTIES.has(key))) {
    throw new Error('授權 receipt 的必要欄位或版本不完整。')
  }
  if (data.schema !== CONSENT_RECEIPT_SCHEMA
    || data.subjectAttestationVersion !== SUBJECT_ATTESTATION_VERSION) {
    throw new Error('授權 receipt 版本不受支援。')
  }
  if (typeof data.recorderName !== 'string' || !data.recorderName.trim()
    || typeof data.recordingDate !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(data.recordingDate)
    || typeof data.consentSignedDate !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(data.consentSignedDate)
    || typeof data.consentType !== 'string' || !CONSENT_TYPES.has(data.consentType)) {
    throw new Error('授權 receipt 的錄音者、日期或同意類型無效。')
  }
  if (!Array.isArray(data.usageScopes)
    || data.usageScopes.some((scope) => typeof scope !== 'string' || !CONSENT_SCOPES.has(scope))
    || new Set(data.usageScopes).size !== data.usageScopes.length
    || !data.usageScopes.includes(PRIVATE_EVALUATION_SCOPE)) {
    throw new Error('授權 receipt 必須明確包含有效且不重複的私人評估 scope。')
  }

  return {
    fileIdentity: receiptFileIdentity(file),
    schema: data.schema,
    recorderName: data.recorderName,
    recordingDate: data.recordingDate,
    consentSignedDate: data.consentSignedDate,
    consentType: data.consentType,
    usageScopes: data.usageScopes as string[],
  }
}

const STATUS_LABEL: Record<VoiceProfile['status'], string> = {
  Pending: '處理中',
  AwaitingTranscriptConfirmation: '待確認文字稿',
  Ready: '已完成',
  Failed: '失敗',
}

const ATTENTION_LABEL: Record<string, string> = {
  local_activation_uncertain: '遠端建立已完成，只剩本地啟用未確認；可以直接繼續啟用。',
  remote_prepare_uncertain: '上傳到 3wa 的結果不明；operation、task 與錄音已保留，需人工確認。',
  remote_task_persistence_uncertain: '3wa 已接受，但遠端 task 的本地保存結果不明，需人工對帳。',
  remote_draft_contract_mismatch: '3wa 回傳的辨識草稿不符合約，需人工處理。',
  remote_task_id_contract_mismatch: '3wa 回傳的 task 代號不符合約，需人工處理。',
}

const STATUS_STYLE: Record<VoiceProfile['status'], string> = {
  Pending: 'border-stone-200 bg-stone-100 text-stone-600',
  AwaitingTranscriptConfirmation: 'border-amber-200 bg-amber-50 text-amber-700',
  Ready: 'border-emerald-200 bg-emerald-50 text-emerald-700',
  Failed: 'border-rose-200 bg-rose-50 text-rose-700',
}

function basePath(characterProfileId: string) {
  return `/api/character-profiles/${characterProfileId}/voice-profiles`
}

function slotPath(characterProfileId: string, sceneCode: string | null) {
  const root = basePath(characterProfileId)
  return sceneCode ? `${root}/scenes/${sceneCode}` : `${root}/base`
}

type Props = {
  characterProfileId: string
  characterName: string
  csrfToken: string
}

export function CharacterVoiceProfilesPanel({ characterProfileId, characterName, csrfToken }: Props) {
  const [profilesState, setProfilesState] = useState<LoadedProfiles | null>(null)
  const [operationsState, setOperationsState] = useState<LoadedOperations | null>(null)
  const [message, setMessage] = useState('')
  const [busySlot, setBusySlot] = useState<string | null>(null)
  const [openSlot, setOpenSlot] = useState<string | null>(null)
  const [mode, setMode] = useState<Record<string, 'Design' | 'Clone'>>({})
  const [promptText, setPromptText] = useState<Record<string, string>>({})
  const [consentReceipts, setConsentReceipts] = useState<Record<string, ConsentReceiptState>>({})
  const [transcriptDraft, setTranscriptDraft] = useState<Record<string, string>>({})
  const [previewing, setPreviewing] = useState<PreviewRequestState | null>(null)
  const [previewAudio, setPreviewAudio] = useState<PreviewAudio | null>(null)
  const currentCharacterProfileIdRef = useRef(characterProfileId)
  const loadGenerationRef = useRef(0)
  const previewAudioRef = useRef<PreviewAudio | null>(null)
  const previewRequestRef = useRef<PreviewRequest | null>(null)

  currentCharacterProfileIdRef.current = characterProfileId

  const profiles = profilesState?.characterProfileId === characterProfileId ? profilesState.items : null
  const operations = operationsState?.characterProfileId === characterProfileId ? operationsState.items : null
  const previewingId = previewing?.characterProfileId === characterProfileId ? previewing.profileId : null

  const load = useCallback(async (signal?: AbortSignal) => {
    const requestedCharacterProfileId = characterProfileId
    const requestedGeneration = loadGenerationRef.current
    try {
      const [list, operationList] = await Promise.all([
        fetchJson<VoiceProfile[]>(basePath(requestedCharacterProfileId), { signal }),
        fetchJson<CloneOperation[]>(`${basePath(requestedCharacterProfileId)}/clone-operations`, { signal }),
      ])
      if (signal?.aborted
        || loadGenerationRef.current !== requestedGeneration
        || currentCharacterProfileIdRef.current !== requestedCharacterProfileId) return
      setProfilesState({ characterProfileId: requestedCharacterProfileId, items: list })
      setOperationsState({ characterProfileId: requestedCharacterProfileId, items: operationList })
    } catch (error) {
      if (signal?.aborted
        || loadGenerationRef.current !== requestedGeneration
        || currentCharacterProfileIdRef.current !== requestedCharacterProfileId) return
      setMessage(error instanceof Error ? error.message : '無法讀取這個角色的自訂聲線。')
    }
  }, [characterProfileId])

  useEffect(() => {
    loadGenerationRef.current += 1
    const controller = new AbortController()
    setProfilesState(null)
    setOperationsState(null)
    setConsentReceipts({})
    void load(controller.signal)
    return () => {
      loadGenerationRef.current += 1
      controller.abort()
    }
  }, [load])

  useEffect(() => {
    setPreviewAudio(null)
    setPreviewing(null)
    return () => {
      const request = previewRequestRef.current
      previewRequestRef.current = null
      request?.controller.abort()

      const audio = previewAudioRef.current
      previewAudioRef.current = null
      if (audio) URL.revokeObjectURL(audio.url)
    }
  }, [characterProfileId])

  function profileFor(sceneCode: string | null) {
    return profiles?.find((profile) => (sceneCode === null ? profile.kind === 'Base' : profile.sceneCode === sceneCode)) ?? null
  }

  function blockingOperationFor(sceneCode: string | null) {
    return operations?.find((operation) =>
      (operation.state === 'Staged' || operation.state === 'RemotePrepared' || operation.state === 'NeedsAttention')
      && (sceneCode === null ? operation.kind === 'Base' : operation.sceneCode === sceneCode)) ?? null
  }

  async function resumeActivation(operation: CloneOperation) {
    setBusySlot(operation.id)
    try {
      await fetchJson(
        `${basePath(characterProfileId)}/clone-operations/${operation.id}/resume-activation`,
        { method: 'POST', csrfToken, body: {} })
      await load()
      setMessage('已完成先前中斷的啟用。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '繼續啟用失敗。')
    } finally {
      setBusySlot(null)
    }
  }

  async function readConsentReceipt(event: ChangeEvent<HTMLInputElement>, receiptKey: string) {
    const input = event.currentTarget
    const file = input.files?.[0]
    if (!file) {
      setConsentReceipts((current) => ({
        ...current,
        [receiptKey]: { summary: null, error: '請選擇授權 receipt JSON。' },
      }))
      return
    }

    const identity = receiptFileIdentity(file)
    try {
      const summary = await parseConsentReceipt(file)
      if (input.files?.[0] && receiptFileIdentity(input.files[0]) === identity) {
        setConsentReceipts((current) => ({ ...current, [receiptKey]: { summary, error: null } }))
      }
    } catch (error) {
      if (input.files?.[0] && receiptFileIdentity(input.files[0]) === identity) {
        setConsentReceipts((current) => ({
          ...current,
          [receiptKey]: {
            summary: null,
            error: error instanceof Error ? error.message : '無法讀取授權 receipt。',
          },
        }))
      }
    }
  }

  function clearConsentReceipt(receiptKey: string) {
    setConsentReceipts((current) => {
      const next = { ...current }
      delete next[receiptKey]
      return next
    })
  }

  function validateCloneForm(form: HTMLFormElement, receiptKey: string) {
    const formData = new FormData(form)
    const file = formData.get('referenceAudio')
    if (!(file instanceof File) || file.size === 0) {
      setMessage('請先選擇 WAV 參考錄音。')
      return null
    }
    if (file.size > MAX_REFERENCE_AUDIO_BYTES) {
      setMessage('參考音檔不可超過 10 MiB。')
      return null
    }

    const expectedTranscript = formData.get('expectedTranscript')
    if (typeof expectedTranscript !== 'string' || !expectedTranscript.trim()) {
      setMessage('請輸入與參考錄音逐字一致的文字稿。')
      return null
    }
    const canonicalTranscript = expectedTranscript.normalize('NFKC').trim()
    if (/\p{Cc}|\p{Cf}/u.test(canonicalTranscript)) {
      setMessage('逐字稿不可包含內嵌換行、控制或格式字元。')
      return null
    }

    const receipt = formData.get('consentReceipt')
    const receiptState = consentReceipts[receiptKey]
    if (!(receipt instanceof File) || receipt.size === 0) {
      setMessage('請選擇授權 receipt JSON。')
      return null
    }
    if (receipt.size > MAX_CONSENT_RECEIPT_BYTES) {
      setMessage('授權 receipt 不可超過 32 KiB。')
      return null
    }
    if (!receiptState?.summary
      || receiptState.summary.fileIdentity !== receiptFileIdentity(receipt)) {
      setMessage(receiptState?.error ?? '請等待授權 receipt 驗證完成。')
      return null
    }
    if (formData.get('rightsAttested') !== 'true') {
      setMessage('請明確勾選已核對錄音者本人授權與 receipt 內容。')
      return null
    }

    return formData
  }

  async function createDesigned(slotKey: string, sceneCode: string | null) {
    if (!DESIGN_VOICE_AVAILABLE) {
      setMessage('3wa 目前不會把 voice_prompt 傳給 VoxCPM2，文字設計聲線無法區分角色；請改用已授權的錄音克隆。')
      return
    }

    const text = promptText[slotKey]?.trim()
    if (!text) return
    setBusySlot(slotKey)
    try {
      await fetchJson(`${slotPath(characterProfileId, sceneCode)}/design`, {
        method: 'POST',
        csrfToken,
        body: { voicePrompt: text },
      })
      setPromptText((current) => ({ ...current, [slotKey]: '' }))
      setOpenSlot(null)
      await load()
      setMessage('聲線已建立，文字設計模式不需要等待處理。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '建立聲線失敗。')
    } finally {
      setBusySlot(null)
    }
  }

  async function createCloned(event: FormEvent<HTMLFormElement>, slotKey: string, sceneCode: string | null) {
    event.preventDefault()
    const form = event.currentTarget
    const receiptKey = `create:${slotKey}`
    const formData = validateCloneForm(form, receiptKey)
    if (!formData) return

    setBusySlot(slotKey)
    try {
      const response = await fetch(apiUrl(slotPath(characterProfileId, sceneCode)), {
        method: 'POST',
        body: formData,
        credentials: 'same-origin',
        headers: { 'X-CSRF-TOKEN': csrfToken },
      })
      if (!response.ok) throw new Error(await responseProblem(response, '建立聲線失敗'))
      form.reset()
      clearConsentReceipt(receiptKey)
      setOpenSlot(null)
      await load()
      setMessage('參考錄音已上傳，正在等待語音辨識草稿；完成後請回來確認文字稿。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '建立聲線失敗。')
    } finally {
      setBusySlot(null)
    }
  }

  async function replaceDesignedWithClone(
    event: FormEvent<HTMLFormElement>,
    profile: VoiceProfile,
  ) {
    event.preventDefault()
    const form = event.currentTarget
    const receiptKey = `replace:${profile.id}`
    const formData = validateCloneForm(form, receiptKey)
    if (!formData) return

    setBusySlot(profile.id)
    try {
      const response = await fetch(apiUrl(`${basePath(characterProfileId)}/${profile.id}/replace-with-clone`), {
        method: 'POST',
        body: formData,
        credentials: 'same-origin',
        headers: { 'X-CSRF-TOKEN': csrfToken },
      })
      if (!response.ok) throw new Error(await responseProblem(response, '取代聲線失敗'))
      form.reset()
      clearConsentReceipt(receiptKey)
      await load()
      setMessage('錄音已送出處理；原文字設計聲線只會在新任務建立成功後才被安全取代。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '取代聲線失敗。')
    } finally {
      setBusySlot(null)
    }
  }

  async function refreshStatus(profile: VoiceProfile) {
    setBusySlot(profile.id)
    try {
      await fetchJson(`${basePath(characterProfileId)}/${profile.id}/refresh-status`, { method: 'POST', csrfToken, body: {} })
      await load()
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '更新狀態失敗。')
    } finally {
      setBusySlot(null)
    }
  }

  async function confirmTranscript(profile: VoiceProfile) {
    const transcript = (transcriptDraft[profile.id] ?? profile.transcript ?? '').trim()
    if (!transcript) return
    setBusySlot(profile.id)
    try {
      await fetchJson(`${basePath(characterProfileId)}/${profile.id}/confirm-transcript`, {
        method: 'POST',
        csrfToken,
        body: { transcript },
      })
      await load()
      setMessage('文字稿已確認並鎖定，聲線可以開始使用。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '確認文字稿失敗。')
    } finally {
      setBusySlot(null)
    }
  }

  async function playPreview(profile: VoiceProfile, text: string) {
    if (previewingId !== null) return

    previewRequestRef.current?.controller.abort()
    const controller = new AbortController()
    const request = { characterProfileId, profileId: profile.id, controller }
    previewRequestRef.current = request
    setPreviewing({ characterProfileId, profileId: profile.id })
    try {
      const response = await fetch(apiUrl(`${basePath(characterProfileId)}/${profile.id}/preview`), {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
        body: JSON.stringify({ text }),
        signal: controller.signal,
      })
      if (!response.ok) throw new Error(await responseProblem(response, '試講失敗'))
      const blob = await response.blob()
      const url = URL.createObjectURL(blob)
      if (controller.signal.aborted) {
        URL.revokeObjectURL(url)
        return
      }

      const previousAudio = previewAudioRef.current
      const nextAudio = {
        characterProfileId,
        profileId: profile.id,
        url,
        fileName: `${safeDownloadFileNamePart(characterName)}-${safeDownloadFileNamePart(previewSlotLabel(profile))}-試音.${audioFileExtension(blob.type)}`,
      }
      previewAudioRef.current = nextAudio
      setPreviewAudio(nextAudio)
      if (previousAudio) URL.revokeObjectURL(previousAudio.url)
      setMessage('試講已產生，可以重複播放或下載；聲音直接來自這組聲線的即時合成。')
    } catch (error) {
      if (controller.signal.aborted) return
      setMessage(error instanceof Error ? error.message : '試講失敗。')
    } finally {
      if (previewRequestRef.current === request) {
        previewRequestRef.current = null
        setPreviewing((current) => current?.characterProfileId === characterProfileId && current.profileId === profile.id ? null : current)
      }
    }
  }

  function releasePreviewAfterError(url: string) {
    const current = previewAudioRef.current
    if (current?.url !== url) return
    previewAudioRef.current = null
    URL.revokeObjectURL(current.url)
    setPreviewAudio((audio) => audio?.url === current.url ? null : audio)
    setMessage('瀏覽器無法播放這段試講，已釋放暫存音訊，請重新產生。')
  }

  function releasePreviewForProfile(profileId: string) {
    const request = previewRequestRef.current
    if (request?.characterProfileId === characterProfileId && request.profileId === profileId) {
      previewRequestRef.current = null
      request.controller.abort()
      setPreviewing((current) => current?.characterProfileId === characterProfileId && current.profileId === profileId ? null : current)
    }

    const current = previewAudioRef.current
    if (current?.characterProfileId !== characterProfileId || current.profileId !== profileId) return
    previewAudioRef.current = null
    URL.revokeObjectURL(current.url)
    setPreviewAudio((audio) => audio?.url === current.url ? null : audio)
  }

  async function remove(profile: VoiceProfile) {
    releasePreviewForProfile(profile.id)
    setBusySlot(profile.id)
    try {
      await fetchJson(`${basePath(characterProfileId)}/${profile.id}`, { method: 'DELETE', csrfToken })
      await load()
      setMessage('聲線已刪除。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '刪除聲線失敗。')
    } finally {
      setBusySlot(null)
    }
  }

  function consentReceiptFields(receiptKey: string) {
    const state = consentReceipts[receiptKey]
    const summary = state?.summary
    const privateOnly = summary
      && !summary.usageScopes.includes(FORMAL_NARRATION_SCOPE)

    return (
      <>
        <label className="block space-y-1 text-xs text-stone-600">
          <span>授權 receipt v2（JSON，最多 32 KiB）</span>
          <input
            accept="application/json,.json"
            className="auth-input w-full text-xs"
            name="consentReceipt"
            onChange={(event) => void readConsentReceipt(event, receiptKey)}
            required
            type="file"
          />
        </label>
        {state?.error && (
          <p className="rounded-lg border border-rose-200 bg-rose-50 px-2 py-1 text-xs leading-5 text-rose-700">
            {state.error}
          </p>
        )}
        {summary && (
          <div className="space-y-1 rounded-lg border border-stone-200 bg-stone-50 p-2 text-xs leading-5 text-stone-700">
            <p className="font-medium text-stone-800">Receipt 唯讀摘要</p>
            <p>錄音者：{summary.recorderName}</p>
            <p>錄音日：{summary.recordingDate} · 簽署日：{summary.consentSignedDate}</p>
            <p>同意類型：{CONSENT_TYPE_LABEL[summary.consentType]}</p>
            <p>允許範圍：{summary.usageScopes.map((scope) => CONSENT_SCOPE_LABEL[scope]).join('、')}</p>
            {privateOnly && (
              <p className="rounded-md border border-amber-200 bg-amber-50 px-2 py-1 font-medium text-amber-800">
                這份 receipt 只允許私人評估／試音；不會因此開啟正式有聲書製作。
              </p>
            )}
            {!privateOnly && (
              <p className="rounded-md border border-amber-200 bg-amber-50 px-2 py-1 font-medium text-amber-800">
                Receipt 內的正式／公開 scope 目前只作為稽核聲明保存；尚未經 StoryVoice
                可信簽章或人工核准，仍只能私人試音，不會開啟正式有聲書。
              </p>
            )}
          </div>
        )}
        <label className="flex items-start gap-2 rounded-lg border border-amber-200 bg-amber-50 p-2 text-xs leading-5 text-amber-900">
          <input className="mt-1" name="rightsAttested" required type="checkbox" value="true" />
          <span>
            我已逐項核對錄音者本人授權、錄音檔與 receipt；提交只依 receipt 既有 scope，不會擴張授權。
          </span>
        </label>
      </>
    )
  }

  return (
    <div className="rounded-xl border border-stone-200 bg-white p-4">
      <p className="text-sm text-stone-500">
        {characterName} 的自訂聲線 — 基礎聲線之外，可以替緊張／開心／生氣／難過各自克隆一組聲線；找不到的情境會自動退回基礎聲線。
      </p>
      {!DESIGN_VOICE_AVAILABLE && (
        <p className="mt-3 rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-xs leading-5 text-amber-800">
          3wa 目前沒有把 voice_prompt 傳給 VoxCPM2，不同文字描述會產生同一聲音。文字設計已停用，現有資料仍會保留但不可試音或正式配音；請使用已取得授權的 WAV 錄音克隆。
        </p>
      )}
      <div className="mt-3 grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {SLOTS.map((slot) => {
          const slotKey = `${characterProfileId}:${slot.sceneCode ?? 'base'}`
          const profile = profileFor(slot.sceneCode)
          const blockingOperation = blockingOperationFor(slot.sceneCode)
          const isOpen = openSlot === slotKey
          const isBusy = busySlot === slotKey
            || (profile !== null && busySlot === profile.id)
            || (blockingOperation !== null && busySlot === blockingOperation.id)
          const slotMode = mode[slotKey] ?? 'Clone'
          const designUnavailable = profile?.mode === 'Design' && !DESIGN_VOICE_AVAILABLE
          const currentPreview = profile
            && previewAudio?.characterProfileId === characterProfileId
            && previewAudio.profileId === profile.id
            ? previewAudio
            : null

          return (
            <div className="rounded-xl border border-stone-200 bg-stone-50 p-3" key={slotKey}>
              <div className="flex items-center justify-between gap-2">
                <p className="text-sm font-medium text-stone-800">{slot.label}</p>
                {profile && (
                  <span className={`rounded-full border px-3 py-1 text-xs ${designUnavailable ? 'border-amber-200 bg-amber-50 text-amber-700' : STATUS_STYLE[profile.status]}`}>
                    {designUnavailable ? '不可用' : STATUS_LABEL[profile.status]}
                  </span>
                )}
              </div>
              <p className="mt-0.5 text-[11px] text-stone-400">{slot.description}</p>

              {blockingOperation && (
                <div className={`mt-2 space-y-2 rounded-lg border px-2 py-2 text-xs leading-5 ${blockingOperation.state === 'NeedsAttention' ? 'border-amber-300 bg-amber-50 text-amber-900' : 'border-stone-200 bg-white text-stone-600'}`}>
                  <p>
                    {blockingOperation.state === 'NeedsAttention'
                      ? (ATTENTION_LABEL[blockingOperation.attentionCode ?? ''] ?? `這筆 Clone operation 需要人工處理（代碼 ${blockingOperation.attentionCode ?? '未知'}）。`)
                      : '這個位置有一筆處理中的 Clone operation；完成前無法建立新聲線。'}
                  </p>
                  {blockingOperation.state === 'NeedsAttention' && blockingOperation.attentionCode === 'local_activation_uncertain' && (
                    <button
                      className="secondary-button w-full px-3 py-2 text-xs disabled:cursor-wait disabled:opacity-60"
                      disabled={isBusy}
                      onClick={() => void resumeActivation(blockingOperation)}
                      type="button"
                    >
                      繼續啟用
                    </button>
                  )}
                </div>
              )}

              {!profile && !blockingOperation && (
                <div className="mt-2">
                  {!isOpen && (
                    <button className="secondary-button w-full px-3 py-2 text-xs" onClick={() => setOpenSlot(slotKey)} type="button">
                      建立聲線
                    </button>
                  )}
                  {isOpen && (
                    <div className="mt-1 space-y-2">
                      <div className="flex gap-2 text-xs">
                        <button
                          className={`rounded-full border px-3 py-1 ${slotMode === 'Design' ? 'border-amber-300 bg-amber-50 text-amber-800' : 'border-stone-200 text-stone-500'}`}
                          disabled={!DESIGN_VOICE_AVAILABLE}
                          onClick={() => setMode((current) => ({ ...current, [slotKey]: 'Design' }))}
                          type="button"
                        >
                          文字設計（目前不支援）
                        </button>
                        <button
                          className={`rounded-full border px-3 py-1 ${slotMode === 'Clone' ? 'border-amber-300 bg-amber-50 text-amber-800' : 'border-stone-200 text-stone-500'}`}
                          onClick={() => setMode((current) => ({ ...current, [slotKey]: 'Clone' }))}
                          type="button"
                        >
                          上傳錄音克隆
                        </button>
                      </div>

                      {slotMode === 'Design' && (
                        <div className="space-y-2">
                          <textarea
                            className="auth-input min-h-20 w-full"
                            maxLength={2000}
                            onChange={(event) => setPromptText((current) => ({ ...current, [slotKey]: event.target.value }))}
                            placeholder="描述這個聲線的音色與語氣，例如：溫柔、略帶沙啞的年輕女聲"
                            value={promptText[slotKey] ?? ''}
                          />
                          <div className="flex gap-2">
                            <button
                              className="secondary-button flex-1 px-3 py-2 text-xs disabled:cursor-wait disabled:opacity-60"
                              disabled={isBusy || !promptText[slotKey]?.trim()}
                              onClick={() => void createDesigned(slotKey, slot.sceneCode)}
                              type="button"
                            >
                              建立
                            </button>
                            <button className="px-3 py-2 text-xs text-stone-500" onClick={() => setOpenSlot(null)} type="button">
                              取消
                            </button>
                          </div>
                        </div>
                      )}

                      {slotMode === 'Clone' && (
                        <form className="space-y-2" onSubmit={(event) => void createCloned(event, slotKey, slot.sceneCode)}>
                          <input accept="audio/wav" className="auth-input w-full text-xs" name="referenceAudio" required type="file" />
                          <textarea
                            className="auth-input min-h-16 w-full"
                            maxLength={2000}
                            name="expectedTranscript"
                            placeholder="逐字輸入錄音中實際說出的內容，不要加入角色名或舞台指示"
                            required
                          />
                          {consentReceiptFields(`create:${slotKey}`)}
                          <div className="flex gap-2">
                            <button
                              className="secondary-button flex-1 px-3 py-2 text-xs disabled:cursor-wait disabled:opacity-60"
                              disabled={isBusy}
                              type="submit"
                            >
                              上傳並建立
                            </button>
                            <button
                              className="px-3 py-2 text-xs text-stone-500"
                              onClick={() => {
                                clearConsentReceipt(`create:${slotKey}`)
                                setOpenSlot(null)
                              }}
                              type="button"
                            >
                              取消
                            </button>
                          </div>
                        </form>
                      )}
                    </div>
                  )}
                </div>
              )}

              {profile && (
                <div className="mt-2 space-y-2 text-xs text-stone-600">
                  <p>
                    {profile.mode === 'Design' ? '文字設計（供應商目前不支援）' : '錄音克隆'}
                    {' · 試講使用本機 AI 主機 · 字數 '}
                    {(profile.transcript ?? profile.voicePromptText ?? '').length}
                    {' · 時長 '}
                    {formatDuration(profile.referenceAudioDurationSeconds)}
                  </p>

                  {profile.status === 'Ready' && !designUnavailable && (
                    <button
                      className="secondary-button w-full px-3 py-2 text-xs disabled:cursor-wait disabled:opacity-60"
                      disabled={previewingId !== null}
                      onClick={() => void playPreview(profile, DEFAULT_PREVIEW_TEXT)}
                      type="button"
                    >
                      {previewingId === profile.id ? '合成中…' : '▶ 播放試講（本機）'}
                    </button>
                  )}

                  {currentPreview && (
                    <div className="space-y-2 rounded-lg border border-stone-200 bg-white p-2">
                      <audio
                        aria-label={`${characterName}的${previewSlotLabel(profile)}試音`}
                        autoPlay
                        className="w-full"
                        controls
                        onError={() => releasePreviewAfterError(currentPreview.url)}
                        preload="metadata"
                        src={currentPreview.url}
                      >
                        你的瀏覽器無法播放這段試音。
                      </audio>
                      <a className="block text-center text-xs text-amber-700 underline" download={currentPreview.fileName} href={currentPreview.url}>
                        下載試音
                      </a>
                    </div>
                  )}

                  {profile.status === 'Pending' && (
                    <button
                      className="secondary-button w-full px-3 py-2 text-xs disabled:cursor-wait disabled:opacity-60"
                      disabled={isBusy}
                      onClick={() => void refreshStatus(profile)}
                      type="button"
                    >
                      更新處理狀態
                    </button>
                  )}

                  {profile.status === 'AwaitingTranscriptConfirmation' && (
                    <div className="space-y-2">
                      {profile.confirmationTranscriptIntent !== null && (
                        <p className="rounded-lg border border-amber-200 bg-amber-50 px-2 py-1 leading-5 text-amber-800">
                          先前已送出一份等待遠端確認的逐字稿（已帶入下方欄位）；只能以完全相同的內容重送確認。
                        </p>
                      )}
                      <textarea
                        className="auth-input min-h-16 w-full"
                        maxLength={2000}
                        onChange={(event) => setTranscriptDraft((current) => ({ ...current, [profile.id]: event.target.value }))}
                        value={transcriptDraft[profile.id] ?? profile.confirmationTranscriptIntent ?? profile.transcript ?? ''}
                      />
                      <button
                        className="secondary-button w-full px-3 py-2 text-xs disabled:cursor-wait disabled:opacity-60"
                        disabled={isBusy}
                        onClick={() => void confirmTranscript(profile)}
                        type="button"
                      >
                        確認文字稿
                      </button>
                      <button
                        className="secondary-button w-full px-3 py-2 text-xs disabled:cursor-wait disabled:opacity-60"
                        disabled={isBusy}
                        onClick={() => void refreshStatus(profile)}
                        type="button"
                      >
                        更新處理狀態
                      </button>
                    </div>
                  )}

                  {((designUnavailable && profile.kind === 'Base') || (profile.status === 'Failed' && profile.mode === 'Clone')) && (
                    <form
                      className="space-y-2 rounded-lg border border-amber-200 bg-white p-2"
                      onSubmit={(event) => void replaceDesignedWithClone(event, profile)}
                    >
                      <p className="leading-5 text-amber-800">
                        {profile.status === 'Failed' && profile.mode === 'Clone'
                          ? '這組克隆聲線處理失敗；重新上傳授權錄音後會先建立新的 3wa 任務，成功時才以交易取代這筆失敗聲線（原稽核資料照舊保留）。'
                          : '上傳後會先建立 3wa 任務；成功時才以交易取代這筆文字設計聲線。'}
                      </p>
                      <input accept="audio/wav" className="auth-input w-full text-xs" name="referenceAudio" required type="file" />
                      <textarea
                        className="auth-input min-h-16 w-full"
                        maxLength={2000}
                        name="expectedTranscript"
                        placeholder="逐字輸入錄音中實際說出的內容"
                        required
                      />
                      {consentReceiptFields(`replace:${profile.id}`)}
                      <button
                        className="secondary-button w-full px-3 py-2 text-xs disabled:cursor-wait disabled:opacity-60"
                        disabled={isBusy}
                        type="submit"
                      >
                        以授權錄音安全取代
                      </button>
                    </form>
                  )}

                  {profile.mode === 'Design' && (
                    <button
                      className="w-full px-3 py-2 text-xs text-rose-600 disabled:cursor-wait disabled:opacity-60"
                      disabled={isBusy}
                      onClick={() => void remove(profile)}
                      type="button"
                    >
                      刪除這組聲線
                    </button>
                  )}
                </div>
              )}
            </div>
          )
        })}
      </div>
      <p aria-live="polite" className="mt-3 min-h-5 text-xs text-stone-500">{message}</p>
    </div>
  )
}
