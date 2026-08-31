import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'

import { apiUrl, fetchJson, responseProblem } from '../api'
import { useAuthedOutletContext } from '../authOutletContext'
import { CharacterVoiceProfilesPanel } from '../CharacterVoiceProfilesPanel'
import { ConfirmDialog } from '../components/ConfirmDialog'

type CharacterProfile = {
  id: string
  canonicalName: string
  hasAvatar: boolean
  age: string | null
  gender: string | null
  birthday: string | null
  personality: string | null
  catchphrase: string | null
  background: string | null
  speakingStyle: string | null
  isActive: boolean
  createdAt: string
  updatedAt: string
}

type VoiceProfileSummary = {
  id: string
  kind: 'Base' | 'Scene'
  sceneCode: string | null
  mode: 'Design' | 'Clone'
  status: 'Pending' | 'AwaitingTranscriptConfirmation' | 'Ready' | 'Failed'
  referenceAudioDurationSeconds: number | null
  createdAt: string
  updatedAt: string
}

type LocalClonePreviewAvailability = {
  available: boolean
  label: string | null
}

type LocalCloneRequestContext = {
  characterProfileId: string
  abortController: AbortController
}

type LoadState = 'idle' | 'loading' | 'ready' | 'error'
type Tab = 'basic' | 'ai' | 'voices' | 'preview' | 'tasks'

const emptyForm = {
  canonicalName: '',
  age: '',
  gender: '',
  birthday: '',
  personality: '',
  catchphrase: '',
  background: '',
  speakingStyle: '',
}

const GENDER_OPTIONS = ['女', '男', '不指定 / 其他']
const LOCAL_CLONE_PREVIEW_TEXT = '這是角色私人聲線試音，僅用於確認聲音，不屬於有聲書正文。'

const SLOT_LABELS: Record<string, string> = {
  Base: '基礎聲線',
  neutral: '平常',
  nervous: '緊張',
  happy: '開心',
  angry: '生氣',
  sad: '難過',
}

const STATUS_LABEL: Record<VoiceProfileSummary['status'], string> = {
  Pending: '處理中',
  AwaitingTranscriptConfirmation: '待確認文字稿',
  Ready: '已完成',
  Failed: '失敗',
}

function slotLabel(profile: VoiceProfileSummary) {
  return SLOT_LABELS[profile.sceneCode ?? 'Base'] ?? profile.sceneCode ?? '基礎聲線'
}

function formatDuration(totalSeconds: number) {
  const seconds = Math.round(totalSeconds)
  const minutes = Math.floor(seconds / 60)
  const remainder = seconds % 60
  return `${minutes}:${remainder.toString().padStart(2, '0')}`
}

export function CharacterLibraryPage() {
  const { csrfToken } = useAuthedOutletContext()
  const [characters, setCharacters] = useState<CharacterProfile[]>([])
  const [listState, setListState] = useState<LoadState>('loading')
  const [selectedId, setSelectedId] = useState('')
  const [newCharacterName, setNewCharacterName] = useState('')
  const [creating, setCreating] = useState(false)
  const [form, setForm] = useState(emptyForm)
  const [saveState, setSaveState] = useState<LoadState>('idle')
  const [message, setMessage] = useState('')
  const [pendingDelete, setPendingDelete] = useState(false)
  const [avatarBusy, setAvatarBusy] = useState(false)
  const [avatarVersion, setAvatarVersion] = useState(0)
  const [activeTab, setActiveTab] = useState<Tab>('basic')
  const [voiceProfiles, setVoiceProfiles] = useState<VoiceProfileSummary[]>([])
  const [previewProfileId, setPreviewProfileId] = useState('')
  const [previewText, setPreviewText] = useState('你好，這是我的聲音示範。')
  const [previewBusy, setPreviewBusy] = useState(false)
  const [localCloneAvailability, setLocalCloneAvailability] = useState<LocalClonePreviewAvailability | null>(null)
  const [localCloneAvailabilityState, setLocalCloneAvailabilityState] = useState<LoadState>('idle')
  const [localClonePreviewBusy, setLocalClonePreviewBusy] = useState(false)
  const [localClonePreviewUrl, setLocalClonePreviewUrl] = useState<string | null>(null)
  const localCloneAvailabilityRequest = useRef<LocalCloneRequestContext | null>(null)
  const localClonePreviewRequest = useRef<LocalCloneRequestContext | null>(null)
  const selectedIdRef = useRef(selectedId)
  const [statusToggleBusy, setStatusToggleBusy] = useState(false)
  const [idCopied, setIdCopied] = useState(false)
  const [aiBusy, setAiBusy] = useState(false)

  const triggerAiAssist = useCallback(async (field: 'all' | 'personality' | 'background' | 'speakingStyle' | 'catchphrase') => {
    if (!form.canonicalName.trim()) {
      setMessage('請先填寫角色名稱再進行 AI 輔助生成。')
      return
    }
    setAiBusy(true)
    setMessage('AI 正在為角色構思設定…')
    try {
      const response = await fetch(apiUrl('/api/character-profiles/ai-assist'), {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
        body: JSON.stringify({
          canonicalName: form.canonicalName,
          gender: form.gender || null,
          age: form.age || null,
          existingPersonality: form.personality || null,
          existingBackground: form.background || null,
          existingCatchphrase: form.catchphrase || null,
          existingSpeakingStyle: form.speakingStyle || null,
          fieldToGenerate: field,
        }),
      })
      if (!response.ok) throw new Error(await responseProblem(response, 'AI 輔助生成失敗。'))
      const result = await response.json() as {
        personality: string | null
        background: string | null
        speakingStyle: string | null
        catchphrase: string | null
      }
      setForm((current) => ({
        ...current,
        personality: result.personality ?? current.personality,
        background: result.background ?? current.background,
        speakingStyle: result.speakingStyle ?? current.speakingStyle,
        catchphrase: result.catchphrase ?? current.catchphrase,
      }))
      setMessage('AI 角色設定已生成並填入表單，確認無誤後請點擊「儲存角色資料」。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'AI 輔助生成失敗。')
    } finally {
      setAiBusy(false)
    }
  }, [csrfToken, form])

  const loadCharacters = useCallback(async () => {
    setListState('loading')
    try {
      const items = await fetchJson<CharacterProfile[]>('/api/character-profiles')
      setCharacters(items)
      setListState('ready')
      setSelectedId((current) => current || items[0]?.id || '')
    } catch {
      setListState('error')
    }
  }, [])

  useEffect(() => {
    void loadCharacters()
  }, [loadCharacters])

  const selected = characters.find((character) => character.id === selectedId) ?? null
  const selectedIsActive = selected?.isActive ?? false
  selectedIdRef.current = selectedId

  useEffect(() => {
    setForm(selected
      ? {
        canonicalName: selected.canonicalName,
        age: selected.age ?? '',
        gender: selected.gender ?? '',
        birthday: selected.birthday ?? '',
        personality: selected.personality ?? '',
        catchphrase: selected.catchphrase ?? '',
        background: selected.background ?? '',
        speakingStyle: selected.speakingStyle ?? '',
      }
      : emptyForm)
  }, [selected])

  const loadVoiceProfiles = useCallback(async () => {
    if (!selectedId) {
      setVoiceProfiles([])
      return
    }
    try {
      const profiles = await fetchJson<VoiceProfileSummary[]>(`/api/character-profiles/${selectedId}/voice-profiles`)
      setVoiceProfiles(profiles)
      setPreviewProfileId((current) => (profiles.some((profile) => profile.id === current) ? current : profiles.find((profile) => profile.status === 'Ready')?.id ?? ''))
    } catch {
      setVoiceProfiles([])
    }
  }, [selectedId])

  useEffect(() => {
    void loadVoiceProfiles()
  }, [loadVoiceProfiles])

  useEffect(() => {
    localCloneAvailabilityRequest.current?.abortController.abort()
    localCloneAvailabilityRequest.current = null
    localClonePreviewRequest.current?.abortController.abort()
    localClonePreviewRequest.current = null
    setLocalClonePreviewBusy(false)
    setLocalClonePreviewUrl(null)
    setLocalCloneAvailability(null)
    if (!selectedId) {
      setLocalCloneAvailabilityState('idle')
      return
    }

    if (!selectedIsActive) {
      setLocalCloneAvailability({ available: false, label: null })
      setLocalCloneAvailabilityState('ready')
      return
    }

    const request: LocalCloneRequestContext = {
      characterProfileId: selectedId,
      abortController: new AbortController(),
    }
    localCloneAvailabilityRequest.current = request
    setLocalCloneAvailabilityState('loading')
    void fetchJson<LocalClonePreviewAvailability>(
      `/api/character-profiles/${selectedId}/local-clone-preview`,
      { signal: request.abortController.signal },
    ).then((availability) => {
      if (localCloneAvailabilityRequest.current !== request
        || selectedIdRef.current !== request.characterProfileId) return
      setLocalCloneAvailability(availability)
      setLocalCloneAvailabilityState('ready')
    }).catch(() => {
      if (request.abortController.signal.aborted
        || localCloneAvailabilityRequest.current !== request) return
      setLocalCloneAvailabilityState('error')
    })

    return () => {
      request.abortController.abort()
      if (localCloneAvailabilityRequest.current === request) {
        localCloneAvailabilityRequest.current = null
      }
    }
  }, [selectedId, selectedIsActive])

  useEffect(() => () => {
    if (localClonePreviewUrl) URL.revokeObjectURL(localClonePreviewUrl)
  }, [localClonePreviewUrl])

  useEffect(() => () => {
    const availabilityRequest = localCloneAvailabilityRequest.current
    const previewRequest = localClonePreviewRequest.current
    localCloneAvailabilityRequest.current = null
    localClonePreviewRequest.current = null
    availabilityRequest?.abortController.abort()
    previewRequest?.abortController.abort()
  }, [])

  function resetForm() {
    setForm(selected
      ? {
        canonicalName: selected.canonicalName,
        age: selected.age ?? '',
        gender: selected.gender ?? '',
        birthday: selected.birthday ?? '',
        personality: selected.personality ?? '',
        catchphrase: selected.catchphrase ?? '',
        background: selected.background ?? '',
        speakingStyle: selected.speakingStyle ?? '',
      }
      : emptyForm)
    setMessage('已重設為儲存過的資料。')
  }

  async function createCharacter(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!newCharacterName.trim()) return
    setCreating(true)
    try {
      const created = await fetchJson<CharacterProfile>('/api/character-profiles', {
        method: 'POST',
        csrfToken,
        body: { ...emptyForm, canonicalName: newCharacterName.trim() },
      })
      setNewCharacterName('')
      await loadCharacters()
      setSelectedId(created.id)
      setActiveTab('basic')
      setMessage(`已建立角色「${created.canonicalName}」，可以在下方補齊資料與聲線。`)
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '建立角色失敗。')
    } finally {
      setCreating(false)
    }
  }

  async function saveCharacter(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!selected || !form.canonicalName.trim()) return
    setSaveState('loading')
    try {
      const updated = await fetchJson<CharacterProfile>(`/api/character-profiles/${selected.id}`, {
        method: 'PUT',
        csrfToken,
        body: {
          canonicalName: form.canonicalName.trim(),
          age: form.age.trim() || null,
          gender: form.gender.trim() || null,
          birthday: form.birthday.trim() || null,
          personality: form.personality.trim() || null,
          catchphrase: form.catchphrase.trim() || null,
          background: form.background.trim() || null,
          speakingStyle: form.speakingStyle.trim() || null,
        },
      })
      setCharacters((current) => current.map((character) => (character.id === updated.id ? updated : character)))
      setSaveState('ready')
      setMessage('角色資料已儲存。')
    } catch (error) {
      setSaveState('error')
      setMessage(error instanceof Error ? error.message : '儲存角色失敗。')
    }
  }

  async function toggleActive() {
    if (!selected) return
    setStatusToggleBusy(true)
    try {
      const updated = await fetchJson<CharacterProfile>(
        `/api/character-profiles/${selected.id}/${selected.isActive ? 'deactivate' : 'activate'}`,
        { method: 'POST', csrfToken, body: {} },
      )
      setCharacters((current) => current.map((character) => (character.id === updated.id ? updated : character)))
      setMessage(updated.isActive ? '角色已設為啟用中。' : '角色已停用；停用不影響已經連結的系列配音。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '切換啟用狀態失敗。')
    } finally {
      setStatusToggleBusy(false)
    }
  }

  async function uploadAvatar(event: FormEvent<HTMLInputElement>) {
    const file = event.currentTarget.files?.[0]
    event.currentTarget.value = ''
    if (!selected || !file) return
    if (file.size > 5 * 1024 * 1024) {
      setMessage('頭像檔案不可超過 5 MiB。')
      return
    }

    setAvatarBusy(true)
    try {
      const formData = new FormData()
      formData.set('avatar', file)
      const response = await fetch(apiUrl(`/api/character-profiles/${selected.id}/avatar`), {
        method: 'POST',
        body: formData,
        credentials: 'same-origin',
        headers: { 'X-CSRF-TOKEN': csrfToken },
      })
      if (!response.ok) throw new Error(await responseProblem(response, '上傳頭像失敗'))
      await loadCharacters()
      setAvatarVersion((current) => current + 1)
      setMessage('頭像已更新。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '上傳頭像失敗。')
    } finally {
      setAvatarBusy(false)
    }
  }

  async function deleteCharacter() {
    if (!selected) return
    setPendingDelete(false)
    try {
      await fetchJson(`/api/character-profiles/${selected.id}`, { method: 'DELETE', csrfToken })
      setSelectedId('')
      await loadCharacters()
      setMessage('角色已刪除。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '刪除角色失敗——如果這個角色還在某個系列裡使用中，要先從系列移除。')
    }
  }

  async function copyId() {
    if (!selected) return
    try {
      await navigator.clipboard.writeText(selected.id)
      setIdCopied(true)
      window.setTimeout(() => setIdCopied(false), 2_000)
    } catch {
      setMessage('複製失敗，請手動選取角色 ID。')
    }
  }

  async function playPreview() {
    if (!selected || !previewProfileId || !previewText.trim()) return
    setPreviewBusy(true)
    try {
      const response = await fetch(apiUrl(`/api/character-profiles/${selected.id}/voice-profiles/${previewProfileId}/preview`), {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
        body: JSON.stringify({ text: previewText.trim() }),
      })
      if (!response.ok) throw new Error(await responseProblem(response, '試講失敗'))
      const blob = await response.blob()
      const url = URL.createObjectURL(blob)
      const audio = new Audio(url)
      let released = false
      const releaseUrl = () => {
        if (released) return
        released = true
        URL.revokeObjectURL(url)
      }
      audio.addEventListener('ended', releaseUrl, { once: true })
      audio.addEventListener('error', releaseUrl, { once: true })
      try {
        await audio.play()
      } catch (error) {
        releaseUrl()
        throw error
      }
      setMessage('正在播放試講。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '試講失敗。')
    } finally {
      setPreviewBusy(false)
    }
  }

  async function generateLocalClonePreview() {
    if (!selected || !localCloneAvailability?.available || localClonePreviewRequest.current) return
    const request: LocalCloneRequestContext = {
      characterProfileId: selected.id,
      abortController: new AbortController(),
    }
    localClonePreviewRequest.current = request
    setLocalClonePreviewBusy(true)
    try {
      const response = await fetch(apiUrl(`/api/character-profiles/${selected.id}/local-clone-preview`), {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
        body: JSON.stringify({ text: LOCAL_CLONE_PREVIEW_TEXT }),
        signal: request.abortController.signal,
      })
      if (!response.ok) throw new Error(await responseProblem(response, '本機私人試音失敗'))
      if (response.headers.get('Content-Type')?.split(';', 1)[0].trim().toLowerCase() !== 'audio/wav') {
        throw new Error('本機私人試音回傳格式無法驗證。')
      }

      const blob = await response.blob()
      if (blob.size === 0) throw new Error('本機私人試音沒有回傳音訊。')
      if (localClonePreviewRequest.current !== request
        || selectedIdRef.current !== request.characterProfileId) return
      setLocalClonePreviewUrl(URL.createObjectURL(blob))
      setMessage('私人試音已產生；只供播放或下載，不會切換正式聲線。')
    } catch (error) {
      if (request.abortController.signal.aborted) return
      setLocalClonePreviewUrl(null)
      setMessage(error instanceof Error ? error.message : '本機私人試音失敗。')
    } finally {
      if (localClonePreviewRequest.current === request) {
        localClonePreviewRequest.current = null
        setLocalClonePreviewBusy(false)
      }
    }
  }

  const baseProfile = voiceProfiles.find((profile) => profile.kind === 'Base')
  const baseProfileReady = baseProfile?.status === 'Ready' && baseProfile.mode === 'Clone'
  const sceneProfilesReady = voiceProfiles.filter((profile) => profile.kind === 'Scene' && profile.status === 'Ready' && profile.mode === 'Clone').length
  const totalSampleSeconds = voiceProfiles.reduce((total, profile) => total + (profile.referenceAudioDurationSeconds ?? 0), 0)
  const inFlightProfile = voiceProfiles.find((profile) => profile.status === 'Pending' || profile.status === 'AwaitingTranscriptConfirmation')
  const readyProfiles = voiceProfiles.filter((profile) => profile.status === 'Ready' && profile.mode === 'Clone')

  const tabs: Array<{ key: Tab; label: string }> = [
    { key: 'basic', label: '基本資料' },
    { key: 'ai', label: 'AI 輔助生成' },
    { key: 'voices', label: '聲線管理' },
    { key: 'preview', label: '試講' },
    { key: 'tasks', label: '任務紀錄' },
  ]

  return (
    <main className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
      <section className="rounded-3xl border border-stone-200 bg-white p-5 sm:p-7">
        <p className="text-xs font-semibold uppercase tracking-[.22em] text-amber-700">Character library</p>
        <h1 className="mt-2 font-serif text-3xl text-stone-900">角色管理</h1>
        <p className="mt-3 max-w-3xl text-sm leading-6 text-stone-500">
          在這裡建立角色的基本資料與自訂聲線，建好之後就能在「多角色系列配音」裡直接選用，同一個角色可以跨系列重複使用。
        </p>
      </section>

      <section className="mt-6 grid grid-cols-1 gap-6 lg:grid-cols-[20rem_minmax(0,1fr)]">
        <aside className="rounded-3xl border border-stone-200 bg-white p-4">
          <h2 className="font-serif text-xl text-stone-900">我的角色</h2>

          <form className="mt-3 flex gap-2" onSubmit={createCharacter}>
            <input
              className="auth-input flex-1"
              maxLength={200}
              onChange={(event) => setNewCharacterName(event.target.value)}
              placeholder="新角色名稱"
              value={newCharacterName}
            />
            <button className="secondary-button px-3 disabled:cursor-wait disabled:opacity-60" disabled={creating || !newCharacterName.trim()} type="submit">
              新增
            </button>
          </form>

          {listState === 'loading' && <p className="mt-4 text-sm text-stone-500">正在讀取角色…</p>}
          {listState === 'error' && <p className="mt-4 text-sm text-rose-700">角色讀取失敗，請重新整理頁面。</p>}
          <div className="mt-3 space-y-2">
            {characters.map((character) => (
              <button
                className={`flex w-full items-center gap-3 rounded-xl border p-3 text-left text-sm transition ${character.id === selectedId ? 'border-amber-300 bg-amber-50 text-amber-800' : 'border-stone-200 bg-stone-50 text-stone-500 hover:text-stone-800'} ${character.isActive ? '' : 'opacity-60'}`}
                key={character.id}
                onClick={() => { setSelectedId(character.id); setActiveTab('basic') }}
                type="button"
              >
                {character.hasAvatar ? (
                  <img
                    alt=""
                    className="h-9 w-9 rounded-full object-cover"
                    src={apiUrl(`/api/character-profiles/${character.id}/avatar?v=${character.id === selectedId ? avatarVersion : 0}`)}
                  />
                ) : (
                  <span className="grid h-9 w-9 shrink-0 place-items-center rounded-full border border-stone-200 bg-white font-serif text-sm text-stone-400">
                    {character.canonicalName.slice(0, 1)}
                  </span>
                )}
                <span className="min-w-0 flex-1 truncate">{character.canonicalName}</span>
                {!character.isActive && <span className="ml-auto shrink-0 text-[10px] text-stone-400">已停用</span>}
              </button>
            ))}
            {listState === 'ready' && characters.length === 0 && (
              <p className="px-1 text-sm leading-6 text-stone-500">還沒有角色；用上面的表單建立第一個。</p>
            )}
          </div>
        </aside>

        <section>
          {!selected && <div className="library-state">選擇或建立一個角色，開始設定基本資料與自訂聲線。</div>}
          {selected && (
            <div className="space-y-6">
              <div className="rounded-3xl border border-stone-200 bg-white p-5 sm:p-7">
                <div className="flex flex-wrap items-center gap-4">
                  {selected.hasAvatar ? (
                    <img
                      alt=""
                      className="h-16 w-16 rounded-full object-cover"
                      src={apiUrl(`/api/character-profiles/${selected.id}/avatar?v=${avatarVersion}`)}
                    />
                  ) : (
                    <span className="grid h-16 w-16 place-items-center rounded-full border border-stone-200 bg-stone-50 font-serif text-2xl text-stone-400">
                      {selected.canonicalName.slice(0, 1)}
                    </span>
                  )}
                  <div>
                    <div className="flex flex-wrap items-center gap-2">
                      <h2 className="font-serif text-2xl text-stone-900">{selected.canonicalName}</h2>
                      <span className={`rounded-full border px-3 py-0.5 text-xs ${selected.isActive ? 'border-emerald-200 bg-emerald-50 text-emerald-700' : 'border-stone-200 bg-stone-100 text-stone-500'}`}>
                        {selected.isActive ? '啟用中' : '已停用'}
                      </span>
                    </div>
                    <div className="mt-1 flex flex-wrap items-center gap-3 text-xs text-stone-400">
                      <button className="underline" onClick={() => void copyId()} type="button">
                        {idCopied ? '角色 ID 已複製' : `角色 ID：${selected.id.slice(0, 8)}…`}
                      </button>
                      <span>建立於 {new Date(selected.createdAt).toLocaleDateString('zh-TW')}</span>
                      <span>最後更新 {new Date(selected.updatedAt).toLocaleDateString('zh-TW')}</span>
                    </div>
                    <label className="mt-1 inline-block text-xs text-amber-700">
                      <span className="cursor-pointer underline">{avatarBusy ? '上傳中…' : '上傳／更換頭像'}</span>
                      <input accept="image/jpeg,image/png,image/webp" className="hidden" onChange={(event) => void uploadAvatar(event)} type="file" />
                    </label>
                  </div>
                  <div className="ml-auto flex items-center gap-3">
                    <button
                      className="secondary-button px-3 py-2 text-xs disabled:cursor-wait disabled:opacity-60"
                      disabled={statusToggleBusy}
                      onClick={() => void toggleActive()}
                      type="button"
                    >
                      {selected.isActive ? '停用角色' : '啟用角色'}
                    </button>
                    <button className="text-xs text-rose-600" onClick={() => setPendingDelete(true)} type="button">刪除這個角色</button>
                  </div>
                </div>

                <div className="mt-5 grid grid-cols-1 gap-3 border-t border-stone-200 pt-5 sm:grid-cols-4">
                  <div className="rounded-xl border border-stone-200 bg-stone-50 p-3">
                    <p className="text-[11px] text-stone-500">基礎聲線狀態</p>
                    <p className={`mt-1 text-sm font-medium ${baseProfileReady ? 'text-emerald-700' : 'text-stone-600'}`}>
                      {baseProfile?.mode === 'Design' ? '不可用（文字設計）' : baseProfile ? STATUS_LABEL[baseProfile.status] : '尚未建立'}
                    </p>
                  </div>
                  <div className="rounded-xl border border-stone-200 bg-stone-50 p-3">
                    <p className="text-[11px] text-stone-500">情境聲線數量</p>
                    <p className="mt-1 text-sm font-medium text-stone-800">{sceneProfilesReady} / 4 已建立</p>
                  </div>
                  <div className="rounded-xl border border-stone-200 bg-stone-50 p-3">
                    <p className="text-[11px] text-stone-500">樣本語料時數</p>
                    <p className="mt-1 text-sm font-medium text-stone-800">{formatDuration(totalSampleSeconds)}</p>
                  </div>
                  <div className="rounded-xl border border-stone-200 bg-stone-50 p-3">
                    <p className="text-[11px] text-stone-500">最近任務狀態</p>
                    <p className="mt-1 text-sm font-medium text-stone-800">
                      {inFlightProfile ? `${slotLabel(inFlightProfile)}：${STATUS_LABEL[inFlightProfile.status]}` : '無進行中任務'}
                    </p>
                  </div>
                </div>

                <div className="mt-5 flex flex-wrap gap-2 border-t border-stone-200 pt-4">
                  {tabs.map((tab) => (
                    <button
                      className={`rounded-full border px-4 py-2 text-sm transition ${activeTab === tab.key ? 'border-amber-300 bg-amber-50 text-amber-800' : 'border-stone-200 text-stone-500 hover:text-stone-800'}`}
                      key={tab.key}
                      onClick={() => setActiveTab(tab.key)}
                      type="button"
                    >
                      {tab.label}
                    </button>
                  ))}
                </div>

                {activeTab === 'basic' && (
                  <form className="mt-6 grid grid-cols-1 gap-4 border-t border-stone-200 pt-5 sm:grid-cols-2" onSubmit={saveCharacter}>
                    <label className="text-xs text-stone-500 sm:col-span-2">
                      角色名稱
                      <input
                        className="auth-input mt-2"
                        maxLength={200}
                        onChange={(event) => setForm((current) => ({ ...current, canonicalName: event.target.value }))}
                        required
                        value={form.canonicalName}
                      />
                    </label>
                    <label className="text-xs text-stone-500">
                      年齡
                      <input className="auth-input mt-2" maxLength={100} onChange={(event) => setForm((current) => ({ ...current, age: event.target.value }))} placeholder="虛構角色可留空或填文字" value={form.age} />
                    </label>
                    <label className="text-xs text-stone-500">
                      性別
                      <select className="auth-input mt-2" onChange={(event) => setForm((current) => ({ ...current, gender: event.target.value }))} value={form.gender}>
                        <option value="">未指定</option>
                        {GENDER_OPTIONS.map((option) => <option key={option} value={option}>{option}</option>)}
                      </select>
                    </label>
                    <label className="text-xs text-stone-500 sm:col-span-2">
                      生日
                      <input className="auth-input mt-2" onChange={(event) => setForm((current) => ({ ...current, birthday: event.target.value }))} type="date" value={form.birthday} />
                    </label>
                    <label className="text-xs text-stone-500 sm:col-span-2">
                      <div className="flex items-center justify-between">
                        <span>個性</span>
                        <button
                          className="rounded-full border border-amber-300 bg-amber-50 px-2.5 py-0.5 text-[10px] font-medium text-amber-800 transition hover:bg-amber-100 disabled:opacity-50"
                          disabled={aiBusy || !form.canonicalName.trim()}
                          onClick={() => void triggerAiAssist('personality')}
                          type="button"
                        >
                          ✦ AI 補完個性
                        </button>
                      </div>
                      <textarea className="auth-input mt-2 min-h-16 w-full" maxLength={2000} onChange={(event) => setForm((current) => ({ ...current, personality: event.target.value }))} value={form.personality} />
                    </label>
                    <label className="text-xs text-stone-500 sm:col-span-2">
                      <div className="flex items-center justify-between">
                        <span>口頭禪</span>
                        <button
                          className="rounded-full border border-amber-300 bg-amber-50 px-2.5 py-0.5 text-[10px] font-medium text-amber-800 transition hover:bg-amber-100 disabled:opacity-50"
                          disabled={aiBusy || !form.canonicalName.trim()}
                          onClick={() => void triggerAiAssist('catchphrase')}
                          type="button"
                        >
                          ✦ AI 構思口頭禪
                        </button>
                      </div>
                      <textarea className="auth-input mt-2 min-h-12 w-full" maxLength={2000} onChange={(event) => setForm((current) => ({ ...current, catchphrase: event.target.value }))} value={form.catchphrase} />
                    </label>
                    <label className="text-xs text-stone-500 sm:col-span-2">
                      <div className="flex items-center justify-between">
                        <span>人物背景</span>
                        <button
                          className="rounded-full border border-amber-300 bg-amber-50 px-2.5 py-0.5 text-[10px] font-medium text-amber-800 transition hover:bg-amber-100 disabled:opacity-50"
                          disabled={aiBusy || !form.canonicalName.trim()}
                          onClick={() => void triggerAiAssist('background')}
                          type="button"
                        >
                          ✦ AI 補完背景
                        </button>
                      </div>
                      <textarea className="auth-input mt-2 min-h-20 w-full" maxLength={4000} onChange={(event) => setForm((current) => ({ ...current, background: event.target.value }))} value={form.background} />
                    </label>
                    <label className="text-xs text-stone-500 sm:col-span-2">
                      <div className="flex items-center justify-between">
                        <span>說話風格</span>
                        <button
                          className="rounded-full border border-amber-300 bg-amber-50 px-2.5 py-0.5 text-[10px] font-medium text-amber-800 transition hover:bg-amber-100 disabled:opacity-50"
                          disabled={aiBusy || !form.canonicalName.trim()}
                          onClick={() => void triggerAiAssist('speakingStyle')}
                          type="button"
                        >
                          ✦ AI 補完風格
                        </button>
                      </div>
                      <textarea className="auth-input mt-2 min-h-16 w-full" maxLength={2000} onChange={(event) => setForm((current) => ({ ...current, speakingStyle: event.target.value }))} value={form.speakingStyle} />
                    </label>
                    <div className="flex gap-3 sm:col-span-2">
                      <button className="secondary-button flex-1 disabled:cursor-wait disabled:opacity-60" disabled={saveState === 'loading' || !form.canonicalName.trim()} type="submit">
                        儲存角色資料
                      </button>
                      <button className="rounded-full border border-stone-200 px-4 py-2 text-sm text-stone-600 transition hover:border-stone-300" onClick={resetForm} type="button">
                        重設
                      </button>
                    </div>
                  </form>
                )}

                {activeTab === 'ai' && (
                  <div className="mt-6 space-y-4 border-t border-stone-200 pt-5">
                    <div className="rounded-2xl border border-amber-200 bg-amber-50/70 p-5">
                      <div className="flex flex-wrap items-center justify-between gap-3">
                        <div>
                          <h3 className="font-serif text-lg text-stone-900">AI 角色設定輔助生成</h3>
                          <p className="mt-1 text-xs text-stone-600">
                            根據目前角色姓名「{form.canonicalName || '未命名'}」與基本條件，AI 會為你自動生成個性特徵、人物背景、說話習慣與口頭禪。
                          </p>
                        </div>
                        <button
                          className="primary-button text-xs disabled:cursor-wait disabled:opacity-60"
                          disabled={aiBusy || !form.canonicalName.trim()}
                          onClick={() => void triggerAiAssist('all')}
                          type="button"
                        >
                          {aiBusy ? '構思生成中…' : '✦ 一鍵生成完整人設'}
                        </button>
                      </div>
                    </div>

                    <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                      <div className="rounded-xl border border-stone-200 bg-white p-4">
                        <div className="flex items-center justify-between">
                          <span className="text-xs font-semibold text-stone-700">個性設定</span>
                          <button
                            className="text-[11px] text-amber-700 hover:underline disabled:opacity-50"
                            disabled={aiBusy || !form.canonicalName.trim()}
                            onClick={() => void triggerAiAssist('personality')}
                            type="button"
                          >
                            單獨重構 ↺
                          </button>
                        </div>
                        <p className="mt-2 text-xs leading-5 text-stone-600">
                          {form.personality || '（尚未生成，點擊上方按鈕生成）'}
                        </p>
                      </div>

                      <div className="rounded-xl border border-stone-200 bg-white p-4">
                        <div className="flex items-center justify-between">
                          <span className="text-xs font-semibold text-stone-700">口頭禪</span>
                          <button
                            className="text-[11px] text-amber-700 hover:underline disabled:opacity-50"
                            disabled={aiBusy || !form.canonicalName.trim()}
                            onClick={() => void triggerAiAssist('catchphrase')}
                            type="button"
                          >
                            單獨重構 ↺
                          </button>
                        </div>
                        <p className="mt-2 text-xs leading-5 text-stone-600">
                          {form.catchphrase || '（尚未生成，點擊上方按鈕生成）'}
                        </p>
                      </div>

                      <div className="rounded-xl border border-stone-200 bg-white p-4">
                        <div className="flex items-center justify-between">
                          <span className="text-xs font-semibold text-stone-700">人物背景</span>
                          <button
                            className="text-[11px] text-amber-700 hover:underline disabled:opacity-50"
                            disabled={aiBusy || !form.canonicalName.trim()}
                            onClick={() => void triggerAiAssist('background')}
                            type="button"
                          >
                            單獨重構 ↺
                          </button>
                        </div>
                        <p className="mt-2 text-xs leading-5 text-stone-600">
                          {form.background || '（尚未生成，點擊上方按鈕生成）'}
                        </p>
                      </div>

                      <div className="rounded-xl border border-stone-200 bg-white p-4">
                        <div className="flex items-center justify-between">
                          <span className="text-xs font-semibold text-stone-700">說話風格</span>
                          <button
                            className="text-[11px] text-amber-700 hover:underline disabled:opacity-50"
                            disabled={aiBusy || !form.canonicalName.trim()}
                            onClick={() => void triggerAiAssist('speakingStyle')}
                            type="button"
                          >
                            單獨重構 ↺
                          </button>
                        </div>
                        <p className="mt-2 text-xs leading-5 text-stone-600">
                          {form.speakingStyle || '（尚未生成，點擊上方按鈕生成）'}
                        </p>
                      </div>
                    </div>

                    <div className="flex justify-end gap-3 pt-2">
                      <button
                        className="secondary-button text-xs disabled:cursor-wait disabled:opacity-60"
                        disabled={saveState === 'loading' || !form.canonicalName.trim()}
                        onClick={saveCharacter}
                        type="button"
                      >
                        儲存已生成的設定至角色
                      </button>
                    </div>
                  </div>
                )}

                {activeTab === 'voices' && (
                  <div className="mt-6 border-t border-stone-200 pt-5">
                    <CharacterVoiceProfilesPanel characterName={selected.canonicalName} characterProfileId={selected.id} csrfToken={csrfToken} />
                  </div>
                )}

                {activeTab === 'preview' && (
                  <div className="mt-6 border-t border-stone-200 pt-5">
                    <section className="rounded-2xl border border-amber-200 bg-amber-50/60 p-4">
                      <div className="flex flex-wrap items-start justify-between gap-3">
                        <div>
                          <h3 className="font-serif text-lg text-stone-900">本機私人試音</h3>
                          <p className="mt-1 text-xs leading-5 text-stone-600">
                            只會使用已列入允許清單的本機素材，不會建立配音任務、存回角色設定或切換正式聲線。
                          </p>
                        </div>
                        {localCloneAvailability?.available && (
                          <span className="rounded-full border border-emerald-200 bg-white px-3 py-1 text-xs text-emerald-700">
                            {localCloneAvailability.label ?? '已核准私人試音'}
                          </span>
                        )}
                      </div>
                      <div className="mt-3 rounded-xl border border-amber-100 bg-white p-3">
                        <p className="text-[11px] font-medium text-stone-500">固定非正文測試句</p>
                        <p className="mt-1 text-sm leading-6 text-stone-800">{LOCAL_CLONE_PREVIEW_TEXT}</p>
                      </div>
                      {localCloneAvailabilityState === 'loading' && (
                        <p className="mt-3 text-sm text-stone-500">正在確認私人試音授權狀態…</p>
                      )}
                      {localCloneAvailabilityState === 'error' && (
                        <p className="mt-3 text-sm text-rose-700">無法驗證私人試音狀態，已停用試音按鈕。</p>
                      )}
                      {localCloneAvailabilityState === 'ready' && !localCloneAvailability?.available && (
                        <p className="mt-3 text-sm text-stone-500">這個角色目前沒有已核准的本機私人試音。</p>
                      )}
                      {localCloneAvailability?.available && (
                        <div className="mt-3 space-y-3">
                          <button
                            className="secondary-button disabled:cursor-wait disabled:opacity-60"
                            disabled={localClonePreviewBusy}
                            onClick={() => void generateLocalClonePreview()}
                            type="button"
                          >
                            {localClonePreviewBusy ? '產生中…' : '產生私人試音'}
                          </button>
                          {localClonePreviewUrl && (
                            <div className="space-y-2 rounded-xl border border-stone-200 bg-white p-3">
                              <audio className="w-full" controls preload="metadata" src={localClonePreviewUrl} />
                              <a
                                className="inline-flex rounded-full border border-stone-200 px-3 py-1.5 text-xs text-stone-700 hover:border-stone-300"
                                download="local-clone-private-preview.wav"
                                href={localClonePreviewUrl}
                              >
                                下載私人試音
                              </a>
                            </div>
                          )}
                        </div>
                      )}
                    </section>

                    <section className="mt-6 border-t border-stone-200 pt-5">
                      <h3 className="font-serif text-lg text-stone-900">正式角色聲線試講</h3>
                      <p className="mt-2 text-sm text-stone-500">選一組已完成的錄音克隆聲線，輸入任意文字試講，聲音會即時合成播放（不會存檔）。文字設計聲線因 3wa 尚未將 voice_prompt 傳給 VoxCPM2，目前不可試音。</p>
                      {readyProfiles.length === 0 && <p className="mt-3 text-sm text-stone-500">這個角色還沒有已完成的聲線，先到「聲線管理」建立一組。</p>}
                      {readyProfiles.length > 0 && (
                        <div className="mt-3 space-y-3">
                          <label className="block text-xs text-stone-500">
                            選擇聲線
                            <select className="auth-input mt-2" onChange={(event) => setPreviewProfileId(event.target.value)} value={previewProfileId}>
                              {readyProfiles.map((profile) => (
                                <option key={profile.id} value={profile.id}>{slotLabel(profile)}（{profile.mode === 'Design' ? '文字設計' : '錄音克隆'}）</option>
                              ))}
                            </select>
                          </label>
                          <label className="block text-xs text-stone-500">
                            試講文字
                            <textarea className="auth-input mt-2 min-h-20 w-full" maxLength={200} onChange={(event) => setPreviewText(event.target.value)} value={previewText} />
                          </label>
                          <button
                            className="secondary-button disabled:cursor-wait disabled:opacity-60"
                            disabled={previewBusy || !previewProfileId || !previewText.trim()}
                            onClick={() => void playPreview()}
                            type="button"
                          >
                            {previewBusy ? '合成中…' : '▶ 播放試講'}
                          </button>
                        </div>
                      )}
                    </section>
                  </div>
                )}

                {activeTab === 'tasks' && (
                  <div className="mt-6 border-t border-stone-200 pt-5">
                    <p className="text-sm text-stone-500">這個角色每一組聲線的建立紀錄（依最後更新時間排序）。</p>
                    {voiceProfiles.length === 0 && <p className="mt-3 text-sm text-stone-500">還沒有任何聲線建立紀錄。</p>}
                    {voiceProfiles.length > 0 && (
                      <div className="mt-3 overflow-x-auto">
                        <table className="w-full min-w-[36rem] border-collapse text-left text-sm">
                          <thead>
                            <tr className="border-b border-stone-200 text-xs text-stone-500">
                              <th className="py-2 pr-3 font-normal">情境</th>
                              <th className="py-2 pr-3 font-normal">來源</th>
                              <th className="py-2 pr-3 font-normal">狀態</th>
                              <th className="py-2 pr-3 font-normal">更新時間</th>
                            </tr>
                          </thead>
                          <tbody>
                            {voiceProfiles
                              .slice()
                              .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
                              .map((profile) => (
                                <tr className="border-b border-stone-100 text-stone-700" key={profile.id}>
                                  <td className="py-2 pr-3">{slotLabel(profile)}</td>
                                  <td className="py-2 pr-3">{profile.mode === 'Design' ? '文字設計' : '錄音克隆'}</td>
                                  <td className="py-2 pr-3">{STATUS_LABEL[profile.status]}</td>
                                  <td className="py-2 pr-3 text-xs text-stone-500">{new Date(profile.updatedAt).toLocaleString('zh-TW')}</td>
                                </tr>
                              ))}
                          </tbody>
                        </table>
                      </div>
                    )}
                  </div>
                )}
              </div>
            </div>
          )}
        </section>
      </section>

      <p aria-live="polite" className="mt-5 min-h-5 text-sm text-stone-500">{message}</p>
      <ConfirmDialog
        confirmLabel="刪除角色"
        description="如果這個角色還連結在某個系列的多角色配音裡，會先被拒絕，需要從系列移除後再刪除。"
        onCancel={() => setPendingDelete(false)}
        onConfirm={() => void deleteCharacter()}
        open={pendingDelete}
        title="確定刪除這個角色？"
      />
    </main>
  )
}
