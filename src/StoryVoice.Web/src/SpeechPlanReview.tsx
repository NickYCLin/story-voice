import { useMemo, useState, type FormEvent } from 'react'

import { fetchBlob, fetchJson } from './api'
import type { BookDetails, Chapter } from './types'

export type SeriesCharacterChoice = {
  id: string
  canonicalName: string
  voice: string
  voiceProvider: string
  characterProfileId: string | null
}

export type SpeechPlanSegment = {
  id: string
  sortOrder: number
  sourceKind: 'ChapterTitle' | 'Body'
  kind: 'Narrator' | 'Dialogue' | 'InnerMonologue'
  startOffset: number
  length: number
  characterId: string | null
  characterName: string | null
  confidence: number
  decisionSource: string
  reviewStatus: 'Confirmed' | 'Suggested' | 'NeedsReview' | 'Rejected'
}

export type SpeechPlanDraft = {
  id: string
  seriesId: string
  bookId: string
  chapterId: string
  planVersion: number
  status: 'Draft' | 'ReadyToConfirm' | 'NeedsReview' | 'Stale'
  confirmedRevisionId: string | null
  segments: SpeechPlanSegment[]
  createdAt: string
  updatedAt: string
}

export type SpeechPlanReviewEntry = {
  book: BookDetails
  chapter: Chapter
  draft: SpeechPlanDraft | null
}

export type ReviewVoiceOption = {
  provider: string
  voice: string
  displayName: string
  locale: string
}

export type AddReviewCharacterRequest = {
  canonicalName: string
  role: 'Main' | 'Supporting' | 'Minor'
  voiceProvider: string
  voice: string
}

type Props = {
  seriesId: string
  entries: SpeechPlanReviewEntry[]
  characters: SeriesCharacterChoice[]
  narratorProvider: string
  characterVoiceOptions: ReviewVoiceOption[]
  csrfToken: string
  onDraftUpdated: (draft: SpeechPlanDraft) => void
  onAddCharacter: (request: AddReviewCharacterRequest) => Promise<SeriesCharacterChoice | null>
  onRebuildCreated: (batch: {
    id: string
    status: 'Building' | 'ReadyToActivate' | 'Activated' | 'Failed'
  }) => void
}

const BLUE_MAGPIE_PROVIDER = 'bluemagpie'
const CLONE_PROVIDER = '3wa-voxcpm2'

const DRAFT_STATUS_LABELS: Record<SpeechPlanDraft['status'], string> = {
  Draft: '草稿',
  NeedsReview: '待審核',
  ReadyToConfirm: '可鎖定',
  Stale: '已過期',
}

const DECISION_SOURCE_LABELS: Record<string, string> = {
  Rule: '規則判定',
  LocalModel: '本機模型建議',
  User: '人工確認',
}

const SEGMENT_KIND_LABELS = {
  Narrator: '旁白',
  Dialogue: '對白',
  InnerMonologue: '內心／默讀',
} as const

function segmentText(chapter: Chapter, segment: SpeechPlanSegment) {
  if (segment.sourceKind === 'ChapterTitle') {
    return chapter.title.slice(segment.startOffset, segment.startOffset + segment.length)
  }

  return chapter.originalText.slice(segment.startOffset, segment.startOffset + segment.length)
}

/** null 表示可試聽；否則回傳停用原因。只有本機供應商能在批次工作外合成。 */
function previewDisabledReason(
  segment: SpeechPlanSegment,
  characters: SeriesCharacterChoice[],
  narratorProvider: string,
) {
  const provider = segment.characterId
    ? characters.find((character) => character.id === segment.characterId)?.voiceProvider ?? ''
    : narratorProvider
  if (provider === BLUE_MAGPIE_PROVIDER) return null
  if (provider === CLONE_PROVIDER) {
    if (!segment.characterId) return '旁白使用 Edge 備援聲線，只在整批合成工作中執行。'
    const character = characters.find((candidate) => candidate.id === segment.characterId)
    return character?.characterProfileId ? null : '角色尚未連結角色庫克隆聲線，無法本機試聽。'
  }
  return '此聲線的供應商只在整批合成工作中執行，暫不支援單句試聽。'
}

function dialogueStats(draft: SpeechPlanDraft) {
  const dialogue = draft.segments.filter((segment) => segment.kind === 'Dialogue')
  return {
    total: dialogue.length,
    confirmedWithCharacter: dialogue.filter((segment) => segment.reviewStatus === 'Confirmed' && segment.characterId).length,
    narratorFallback: dialogue.filter((segment) => segment.reviewStatus === 'Confirmed' && !segment.characterId).length,
    pending: dialogue.filter((segment) => segment.reviewStatus !== 'Confirmed').length,
  }
}

export function SpeechPlanReview({
  seriesId,
  entries,
  characters,
  narratorProvider,
  characterVoiceOptions,
  csrfToken,
  onDraftUpdated,
  onAddCharacter,
  onRebuildCreated,
}: Props) {
  const [message, setMessage] = useState('')
  const [busyChapterId, setBusyChapterId] = useState<string | null>(null)
  const [busySegmentId, setBusySegmentId] = useState<string | null>(null)
  const [playingSegmentId, setPlayingSegmentId] = useState<string | null>(null)
  const [expandedChapterId, setExpandedChapterId] = useState<string | null>(null)
  const [assignSelections, setAssignSelections] = useState<Record<string, string>>({})
  const [correctionSegmentId, setCorrectionSegmentId] = useState<string | null>(null)
  const [correctionKind, setCorrectionKind] = useState<SpeechPlanSegment['kind']>('Dialogue')
  const [correctionCharacterId, setCorrectionCharacterId] = useState('')
  const [newCharacterName, setNewCharacterName] = useState('')
  const [newCharacterRole, setNewCharacterRole] = useState<AddReviewCharacterRequest['role']>('Supporting')
  const [newCharacterVoiceKey, setNewCharacterVoiceKey] = useState('')
  const [rightsAttested, setRightsAttested] = useState(false)
  const [stageState, setStageState] = useState<'idle' | 'loading'>('idle')
  const [bulkDraftState, setBulkDraftState] = useState<'idle' | 'loading'>('idle')

  const confirmedGapCount = useMemo(
    () => entries.filter((entry) => entry.draft?.confirmedRevisionId === null || entry.draft === null).length,
    [entries],
  )
  const missingDraftCount = useMemo(() => entries.filter((entry) => entry.draft === null).length, [entries])
  const overallStats = useMemo(() => {
    const drafts = entries.flatMap((entry) => (entry.draft ? [entry.draft] : []))
    const perDraft = drafts.map(dialogueStats)
    return {
      lockedChapters: entries.filter((entry) => entry.draft && entry.draft.confirmedRevisionId !== null).length,
      totalChapters: entries.length,
      confirmedDialogue: perDraft.reduce((sum, stats) => sum + stats.confirmedWithCharacter, 0),
      narratorFallback: perDraft.reduce((sum, stats) => sum + stats.narratorFallback, 0),
      pendingDialogue: perDraft.reduce((sum, stats) => sum + stats.pending, 0),
    }
  }, [entries])

  function assignSelectionFor(segment: SpeechPlanSegment) {
    return assignSelections[segment.id] ?? segment.characterId ?? ''
  }

  async function previewSegmentAudio(entry: SpeechPlanReviewEntry, segment: SpeechPlanSegment) {
    if (!entry.draft) return
    const text = segmentText(entry.chapter, segment)
    if (!text.trim()) return
    setBusySegmentId(segment.id)
    setMessage(`正在以這段實際會使用的聲線產生「${text.slice(0, 15)}…」試聽…`)
    try {
      const blob = await fetchBlob(
        `/api/series/${seriesId}/speech-plan-drafts/${entry.draft.id}/segments/${segment.id}/preview`,
        { method: 'POST', csrfToken },
      )
      const url = URL.createObjectURL(blob)
      const audio = new Audio(url)
      setPlayingSegmentId(segment.id)
      audio.onended = () => {
        setPlayingSegmentId(null)
        URL.revokeObjectURL(url)
      }
      audio.onerror = () => {
        setPlayingSegmentId(null)
        URL.revokeObjectURL(url)
      }
      await audio.play()
      setMessage('正在播放該句語音（過長片段只試聽開頭）。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '單段語音試聽失敗。')
    } finally {
      setBusySegmentId(null)
    }
  }

  async function buildDraft(entry: SpeechPlanReviewEntry) {
    setBusyChapterId(entry.chapter.id)
    setMessage('正在依目前角色表產生劇本草稿…')
    try {
      const draft = await fetchJson<SpeechPlanDraft>(
        `/api/series/${seriesId}/books/${entry.book.id}/chapters/${entry.chapter.id}/speech-plan`,
        { method: 'POST', csrfToken, body: {} },
      )
      onDraftUpdated(draft)
      setExpandedChapterId(entry.chapter.id)
      setMessage('草稿已更新；低信心角色需要逐段確認。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '無法建立劇本草稿。')
    } finally {
      setBusyChapterId(null)
    }
  }

  async function buildMissingDrafts() {
    const missing = entries.filter((entry) => entry.draft === null)
    if (missing.length === 0) return
    setBulkDraftState('loading')
    let done = 0
    let failed = 0
    for (const entry of missing) {
      setMessage(`正在產生草稿 ${done + failed + 1}/${missing.length}：${entry.chapter.title}`)
      try {
        const draft = await fetchJson<SpeechPlanDraft>(
          `/api/series/${seriesId}/books/${entry.book.id}/chapters/${entry.chapter.id}/speech-plan`,
          { method: 'POST', csrfToken, body: {} },
        )
        onDraftUpdated(draft)
        done += 1
      } catch {
        failed += 1
      }
    }
    setMessage(failed === 0
      ? `已為 ${done} 章產生草稿；接著逐章確認低信心對白。`
      : `已產生 ${done} 章草稿，${failed} 章失敗；請稍後對失敗章節單獨重試。`)
    setBulkDraftState('idle')
  }

  async function confirmSegment(entry: SpeechPlanReviewEntry, segment: SpeechPlanSegment, characterId: string | null) {
    if (!entry.draft) return
    setBusySegmentId(segment.id)
    try {
      const draft = await fetchJson<SpeechPlanDraft>(
        `/api/series/${seriesId}/speech-plan-drafts/${entry.draft.id}/segments/${segment.id}/confirm`,
        { method: 'PUT', csrfToken, body: { characterId } },
      )
      onDraftUpdated(draft)
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '無法確認這段角色。')
    } finally {
      setBusySegmentId(null)
    }
  }

  async function confirmAllSuggested(entry: SpeechPlanReviewEntry, characterId: string | null) {
    if (!entry.draft) return
    setBusyChapterId(entry.chapter.id)
    try {
      const draft = await fetchJson<SpeechPlanDraft>(
        `/api/series/${seriesId}/speech-plan-drafts/${entry.draft.id}/segments/confirm-suggested`,
        { method: 'POST', csrfToken, body: { characterId } },
      )
      onDraftUpdated(draft)
      setMessage(characterId ? '已批次確認這個角色的所有建議段。' : '已批次確認所有帶角色建議的對白段；無建議角色的段落仍需人工指派。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '批次確認建議失敗。')
    } finally {
      setBusyChapterId(null)
    }
  }

  async function reassignSegment(entry: SpeechPlanReviewEntry, segment: SpeechPlanSegment) {
    if (!entry.draft) return
    if (correctionKind === 'InnerMonologue' && !correctionCharacterId) {
      setMessage('內心／默讀片段必須指定角色。')
      return
    }
    setBusySegmentId(segment.id)
    try {
      const draft = await fetchJson<SpeechPlanDraft>(
        `/api/series/${seriesId}/speech-plan-drafts/${entry.draft.id}/segments/${segment.id}/reassign`,
        {
          method: 'PUT',
          csrfToken,
          body: {
            kind: correctionKind,
            characterId: correctionKind === 'Narrator' ? null : correctionCharacterId || null,
          },
        },
      )
      onDraftUpdated(draft)
      setCorrectionSegmentId(null)
      setMessage('已依你的指定修改這段的朗讀方式。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '無法修改這段指派。')
    } finally {
      setBusySegmentId(null)
    }
  }

  async function confirmPlan(entry: SpeechPlanReviewEntry) {
    if (!entry.draft) return
    setBusyChapterId(entry.chapter.id)
    try {
      const revision = await fetchJson<{ id: string }>(
        `/api/series/${seriesId}/speech-plan-drafts/${entry.draft.id}/confirm`,
        { method: 'POST', csrfToken, body: {} },
      )
      onDraftUpdated({ ...entry.draft, confirmedRevisionId: revision.id })
      setMessage('這一章已鎖定目前劇本 revision。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '這一章還有未審核的分段。')
    } finally {
      setBusyChapterId(null)
    }
  }

  async function addCharacterInline(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const name = newCharacterName.trim()
    const voice = characterVoiceOptions.find(
      (option) => `${option.provider}\n${option.voice}` === newCharacterVoiceKey,
    )
    if (!name || !voice) return
    setBusyChapterId('__new-character__')
    try {
      const created = await onAddCharacter({
        canonicalName: name,
        role: newCharacterRole,
        voiceProvider: voice.provider,
        voice: voice.voice,
      })
      if (created) {
        setNewCharacterName('')
        setMessage(`已新增角色「${created.canonicalName}」，現在可以直接指派給對白段。`)
      }
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '審核中新增角色失敗。')
    } finally {
      setBusyChapterId(null)
    }
  }

  async function createStagedRebuild() {
    setStageState('loading')
    setMessage('正在建立全系列 staged 多角色工作…')
    try {
      const batch = await fetchJson<{ id: string; status: 'Building' | 'ReadyToActivate' | 'Activated' | 'Failed' }>(
        `/api/series/${seriesId}/narration-rebuilds`,
        { method: 'POST', csrfToken, body: { rightsAttested } },
      )
      onRebuildCreated(batch)
      setMessage('整批工作已排入；所有冊次完成前不會切換目前音訊。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '無法建立 staged 多角色工作。')
    } finally {
      setStageState('idle')
    }
  }

  return (
    <section className="mt-8 rounded-3xl border border-amber-100 bg-amber-50/60 p-5 sm:p-7" aria-label="跨冊劇本審核">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <p className="text-xs font-semibold uppercase tracking-[.22em] text-amber-700">Speech plan review</p>
          <h2 className="mt-1 font-serif text-2xl text-stone-900">逐章角色審核</h2>
          <p className="mt-2 max-w-3xl text-sm leading-6 text-stone-500">只有目前章節正文的 owner 頁面會顯示這些切片；staged batch API 不回傳正文。</p>
          <p className="mt-2 text-xs text-stone-500">
            已鎖定 {overallStats.lockedChapters}/{overallStats.totalChapters} 章 ·
            對白已確認 {overallStats.confirmedDialogue} 段（另有 {overallStats.narratorFallback} 段確認為旁白 fallback）·
            待審核 {overallStats.pendingDialogue} 段
          </p>
        </div>
        <div className="flex flex-col items-end gap-2">
          <span className="rounded-full border border-amber-200 bg-amber-100 px-3 py-1 text-xs text-amber-800">尚缺 {confirmedGapCount} 章確認</span>
          {missingDraftCount > 0 && (
            <button
              className="secondary-button px-3 py-1.5 text-xs disabled:cursor-wait disabled:opacity-60"
              disabled={bulkDraftState === 'loading'}
              onClick={() => void buildMissingDrafts()}
              type="button"
            >
              {bulkDraftState === 'loading' ? '批次產生草稿中…' : `為缺草稿的 ${missingDraftCount} 章產生草稿`}
            </button>
          )}
        </div>
      </div>

      <form className="mt-5 flex flex-wrap items-end gap-3 rounded-2xl border border-stone-200 bg-white p-4" onSubmit={addCharacterInline}>
        <p className="w-full text-xs text-stone-500">審核時遇到名冊上沒有的角色，可以直接在這裡補建；聲線建立後即固定，跨冊沿用。</p>
        <label className="text-xs text-stone-500">
          新角色名稱
          <input className="auth-input mt-1" maxLength={100} onChange={(event) => setNewCharacterName(event.target.value)} value={newCharacterName} />
        </label>
        <label className="text-xs text-stone-500">
          角色層級
          <select className="auth-input mt-1" onChange={(event) => setNewCharacterRole(event.target.value as AddReviewCharacterRequest['role'])} value={newCharacterRole}>
            <option value="Main">主角</option>
            <option value="Supporting">配角</option>
            <option value="Minor">次要角色</option>
          </select>
        </label>
        <label className="text-xs text-stone-500">
          固定聲線
          <select className="auth-input mt-1 min-w-52" onChange={(event) => setNewCharacterVoiceKey(event.target.value)} value={newCharacterVoiceKey}>
            <option value="">選擇聲線</option>
            {characterVoiceOptions.map((option) => (
              <option key={`${option.provider}\n${option.voice}`} value={`${option.provider}\n${option.voice}`}>
                {option.displayName}（{option.locale}）
              </option>
            ))}
          </select>
        </label>
        <button
          className="secondary-button px-3 py-2 text-xs disabled:cursor-not-allowed disabled:opacity-50"
          disabled={busyChapterId === '__new-character__' || !newCharacterName.trim() || !newCharacterVoiceKey}
          type="submit"
        >
          新增角色
        </button>
      </form>

      <div className="mt-5 space-y-4">
        {entries.map((entry) => {
          const draft = entry.draft
          const needsReview = draft?.segments.filter((segment) => segment.kind === 'Dialogue' && segment.reviewStatus !== 'Confirmed') ?? []
          const isConfirmed = draft?.confirmedRevisionId !== null && draft !== null
          const isExpanded = expandedChapterId === entry.chapter.id
          const stats = draft ? dialogueStats(draft) : null
          const suggestedWithCharacter = draft?.segments.filter(
            (segment) => segment.kind === 'Dialogue' && segment.reviewStatus === 'Suggested' && segment.characterId,
          ) ?? []
          const suggestedByCharacter = new Map<string, { name: string; count: number }>()
          for (const segment of suggestedWithCharacter) {
            const existing = suggestedByCharacter.get(segment.characterId ?? '')
            suggestedByCharacter.set(segment.characterId ?? '', {
              name: segment.characterName ?? '未命名角色',
              count: (existing?.count ?? 0) + 1,
            })
          }
          return (
            <article className="rounded-2xl border border-stone-200 bg-white p-4" key={entry.chapter.id}>
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div>
                  <p className="text-xs text-stone-500">{entry.book.title}</p>
                  <h3 className="mt-1 font-medium text-stone-800">{entry.chapter.title}</h3>
                  {stats && (
                    <p className="mt-1 text-xs text-stone-500">
                      對白 {stats.total} 段：{stats.confirmedWithCharacter} 已確認角色、{stats.narratorFallback} 旁白 fallback、{stats.pending} 待審核
                    </p>
                  )}
                </div>
                <div className="flex flex-wrap items-center gap-2">
                  <span className={`rounded-full px-2.5 py-1 text-xs ${isConfirmed ? 'bg-emerald-50 text-emerald-700' : 'bg-stone-100 text-stone-500'}`}>
                    {isConfirmed ? '目前 revision 已確認' : draft ? `草稿：${DRAFT_STATUS_LABELS[draft.status] ?? draft.status}` : '尚未產生草稿'}
                  </span>
                  <button className="secondary-button px-3 py-1.5 text-xs" disabled={busyChapterId === entry.chapter.id} onClick={() => void buildDraft(entry)} type="button">
                    {draft ? '重新產生草稿' : '產生草稿'}
                  </button>
                  {draft && (
                    <button
                      className="rounded-full border border-stone-200 bg-white px-3 py-1.5 text-xs text-stone-600 transition hover:bg-stone-100"
                      onClick={() => setExpandedChapterId(isExpanded ? null : entry.chapter.id)}
                      type="button"
                    >
                      {isExpanded ? '收合分段 ▲' : `展開分段（${draft.segments.length}）▼`}
                    </button>
                  )}
                </div>
              </div>

              {draft?.status === 'Stale' && (
                <p className="mt-3 rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-xs leading-5 text-amber-800">章節正文或章名已變更，這份草稿已過期；請重新產生草稿後再審核。</p>
              )}

              {draft && isExpanded && (
                <div className="mt-4 space-y-2">
                  {suggestedWithCharacter.length > 0 && (
                    <div className="flex flex-wrap items-center gap-2 rounded-xl border border-sky-200 bg-sky-50 p-3">
                      <button
                        className="secondary-button px-3 py-1.5 text-xs"
                        disabled={busyChapterId === entry.chapter.id}
                        onClick={() => void confirmAllSuggested(entry, null)}
                        type="button"
                      >
                        接受全部建議（{suggestedWithCharacter.length} 段）
                      </button>
                      {[...suggestedByCharacter.entries()].map(([characterId, info]) => (
                        <button
                          className="rounded-full border border-sky-200 bg-white px-3 py-1.5 text-xs text-sky-800 transition hover:bg-sky-100"
                          disabled={busyChapterId === entry.chapter.id}
                          key={characterId}
                          onClick={() => void confirmAllSuggested(entry, characterId)}
                          type="button"
                        >
                          全部確認為 {info.name}（{info.count}）
                        </button>
                      ))}
                    </div>
                  )}

                  {draft.segments.map((segment) => {
                    const previewDisabled = previewDisabledReason(segment, characters, narratorProvider)
                    const correctable = segment.sourceKind === 'Body'
                    const isCorrecting = correctionSegmentId === segment.id
                    return (
                      <div className="rounded-xl border border-stone-200 bg-stone-50/70 p-3" key={segment.id}>
                        <div className="flex flex-wrap items-start justify-between gap-3">
                          <p className="min-w-0 flex-1 text-sm leading-6 text-stone-700">{segmentText(entry.chapter, segment)}</p>
                          <div className="flex shrink-0 items-center gap-2">
                            <button
                              className="rounded-full border border-stone-200 bg-white px-2.5 py-1 text-xs text-stone-600 transition hover:bg-stone-100 disabled:opacity-50"
                              disabled={busySegmentId === segment.id || previewDisabled !== null}
                              onClick={() => void previewSegmentAudio(entry, segment)}
                              title={previewDisabled ?? '用這段實際會使用的聲線單獨試聽'}
                              type="button"
                            >
                              {playingSegmentId === segment.id ? '播放中 🔊' : '試聽此句 ▶'}
                            </button>
                            {correctable && (
                              <button
                                className="rounded-full border border-stone-200 bg-white px-2.5 py-1 text-xs text-stone-600 transition hover:bg-stone-100"
                                onClick={() => {
                                  if (isCorrecting) {
                                    setCorrectionSegmentId(null)
                                    return
                                  }
                                  setCorrectionSegmentId(segment.id)
                                  setCorrectionKind(segment.kind)
                                  setCorrectionCharacterId(segment.characterId ?? '')
                                }}
                                title="修改這段的朗讀方式或說話者（含已自動確認的判定）"
                                type="button"
                              >
                                {isCorrecting ? '取消修改' : '修改指派'}
                              </button>
                            )}
                            <span className={`text-xs ${segment.reviewStatus === 'Confirmed' ? 'text-emerald-700' : 'text-amber-700'}`}>
                              {segment.kind === 'Dialogue' && `${segment.characterName ?? '無法判定'} · `}
                              {segment.kind === 'InnerMonologue' && `內心／默讀：${segment.characterName ?? '無法判定'} · `}
                              {segment.reviewStatus === 'Confirmed'
                                ? `已確認（${DECISION_SOURCE_LABELS[segment.decisionSource] ?? segment.decisionSource}）`
                                : segment.reviewStatus === 'Rejected'
                                  ? `已拒絕，請重新指派 · 信心 ${segment.confidence}%`
                                  : `待審核（${DECISION_SOURCE_LABELS[segment.decisionSource] ?? segment.decisionSource}）· 信心 ${segment.confidence}%`}
                            </span>
                          </div>
                        </div>
                        {segment.kind === 'Dialogue' && segment.reviewStatus !== 'Confirmed' && (
                          <div className="mt-3 flex flex-wrap items-end gap-2">
                            <label className="text-xs text-stone-500">
                              指派角色
                              <select
                                className="auth-input mt-1 min-w-44"
                                onChange={(event) => setAssignSelections((current) => ({ ...current, [segment.id]: event.target.value }))}
                                value={assignSelectionFor(segment)}
                              >
                                <option value="">無法判定，保留旁白</option>
                                {characters.map((character) => <option key={character.id} value={character.id}>{character.canonicalName}</option>)}
                              </select>
                            </label>
                            <button
                              className="secondary-button px-3 py-2 text-xs"
                              disabled={busySegmentId === segment.id}
                              onClick={() => void confirmSegment(entry, segment, assignSelectionFor(segment) || null)}
                              type="button"
                            >
                              確認這段角色
                            </button>
                          </div>
                        )}
                        {correctable && isCorrecting && (
                          <div className="mt-3 flex flex-wrap items-end gap-2 rounded-lg border border-amber-200 bg-amber-50/70 p-2">
                            <label className="text-xs text-stone-500">
                              朗讀方式
                              <select
                                className="auth-input mt-1"
                                onChange={(event) => setCorrectionKind(event.target.value as SpeechPlanSegment['kind'])}
                                value={correctionKind}
                              >
                                <option value="Narrator">{SEGMENT_KIND_LABELS.Narrator}</option>
                                <option value="Dialogue">{SEGMENT_KIND_LABELS.Dialogue}</option>
                                <option value="InnerMonologue">{SEGMENT_KIND_LABELS.InnerMonologue}</option>
                              </select>
                            </label>
                            {correctionKind !== 'Narrator' && (
                              <label className="text-xs text-stone-500">
                                說話者
                                <select
                                  className="auth-input mt-1 min-w-44"
                                  onChange={(event) => setCorrectionCharacterId(event.target.value)}
                                  value={correctionCharacterId}
                                >
                                  <option value="">{correctionKind === 'Dialogue' ? '無法判定，保留旁白' : '選擇角色'}</option>
                                  {characters.map((character) => <option key={character.id} value={character.id}>{character.canonicalName}</option>)}
                                </select>
                              </label>
                            )}
                            <button
                              className="secondary-button px-3 py-2 text-xs"
                              disabled={busySegmentId === segment.id}
                              onClick={() => void reassignSegment(entry, segment)}
                              type="button"
                            >
                              套用修改
                            </button>
                          </div>
                        )}
                      </div>
                    )
                  })}
                  <div className="flex flex-wrap items-center justify-between gap-3 pt-2">
                    <p className="text-xs text-stone-500">{needsReview.length === 0 ? '所有對白段已確認。' : `仍有 ${needsReview.length} 段低信心／待審核對白。`}</p>
                    <button
                      className="secondary-button px-3 py-2 text-xs disabled:cursor-not-allowed disabled:opacity-45"
                      disabled={draft.status !== 'ReadyToConfirm' || busyChapterId === entry.chapter.id || isConfirmed}
                      onClick={() => void confirmPlan(entry)}
                      type="button"
                    >
                      鎖定這章劇本 revision
                    </button>
                  </div>
                </div>
              )}
            </article>
          )
        })}
      </div>

      <div className="mt-6 border-t border-stone-200 pt-5">
        <label className="flex max-w-4xl items-start gap-3 text-sm leading-6 text-stone-600">
          <input checked={rightsAttested} className="mt-1 h-4 w-4 accent-amber-600" onChange={(event) => setRightsAttested(event.target.checked)} type="checkbox" />
          <span>我確認這個系列所有正文都可合法交給系列目前設定的語音服務處理；服務可能是私人本機自架或外部供應商。這會建立整批私有 staged 音訊，直到所有冊次完成才可手動啟用。</span>
        </label>
        <button
          className="secondary-button mt-3 disabled:cursor-not-allowed disabled:opacity-45"
          disabled={confirmedGapCount > 0 || !rightsAttested || stageState === 'loading'}
          onClick={() => void createStagedRebuild()}
          type="button"
        >
          {stageState === 'loading' ? '正在建立 staged 工作…' : '建立整批多角色音訊'}
        </button>
      </div>
      <p aria-live="polite" className="mt-3 min-h-5 text-xs text-stone-500">{message}</p>
    </section>
  )
}
