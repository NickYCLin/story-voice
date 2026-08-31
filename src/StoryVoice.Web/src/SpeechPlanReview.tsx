import { useMemo, useState } from 'react'

import { apiUrl, fetchJson } from './api'
import type { BookDetails, Chapter } from './types'

export type SeriesCharacterChoice = {
  id: string
  canonicalName: string
  voice: string
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
  status: 'ReadyToConfirm' | 'NeedsReview' | 'Stale'
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

type Props = {
  seriesId: string
  entries: SpeechPlanReviewEntry[]
  characters: SeriesCharacterChoice[]
  csrfToken: string
  onDraftUpdated: (draft: SpeechPlanDraft) => void
  onRebuildCreated: (batch: {
    id: string
    status: 'Building' | 'ReadyToActivate' | 'Activated' | 'Failed'
  }) => void
}

function segmentText(chapter: Chapter, segment: SpeechPlanSegment) {
  if (segment.sourceKind === 'ChapterTitle') {
    return chapter.title.slice(segment.startOffset, segment.startOffset + segment.length)
  }

  return chapter.originalText.slice(segment.startOffset, segment.startOffset + segment.length)
}

export function SpeechPlanReview({
  seriesId,
  entries,
  characters,
  csrfToken,
  onDraftUpdated,
  onRebuildCreated,
}: Props) {
  const [message, setMessage] = useState('')
  const [busyChapterId, setBusyChapterId] = useState<string | null>(null)
  const [busySegmentId, setBusySegmentId] = useState<string | null>(null)
  const [playingSegmentId, setPlayingSegmentId] = useState<string | null>(null)
  const [rightsAttested, setRightsAttested] = useState(false)
  const [stageState, setStageState] = useState<'idle' | 'loading'>('idle')

  const confirmedGapCount = useMemo(
    () => entries.filter((entry) => entry.draft?.confirmedRevisionId === null || entry.draft === null).length,
    [entries],
  )

  async function previewSegmentAudio(entry: SpeechPlanReviewEntry, segment: SpeechPlanSegment) {
    const text = segmentText(entry.chapter, segment)
    if (!text.trim()) return
    setBusySegmentId(segment.id)
    setMessage(`正在產生「${text.slice(0, 15)}…」語音試聽…`)
    try {
      const char = characters.find((c) => c.id === segment.characterId)
      const voice = char?.voice || 'zh-TW-HsiaoChenNeural'
      const response = await fetch(apiUrl('/api/developer/playground/synthesize'), {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
        body: JSON.stringify({ voice, text }),
      })
      if (!response.ok) {
        throw new Error('單段語音試聽合成失敗。')
      }
      const blob = await response.blob()
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
      setMessage('正在播放該句語音。')
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
      setMessage('草稿已更新；低信心角色需要逐段確認。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '無法建立劇本草稿。')
    } finally {
      setBusyChapterId(null)
    }
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
        </div>
        <span className="rounded-full border border-amber-200 bg-amber-100 px-3 py-1 text-xs text-amber-800">尚缺 {confirmedGapCount} 章確認</span>
      </div>

      <div className="mt-5 space-y-4">
        {entries.map((entry) => {
          const draft = entry.draft
          const needsReview = draft?.segments.filter((segment) => segment.kind === 'Dialogue' && segment.reviewStatus !== 'Confirmed') ?? []
          const isConfirmed = draft?.confirmedRevisionId !== null && draft !== null
          return (
            <article className="rounded-2xl border border-stone-200 bg-white p-4" key={entry.chapter.id}>
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div>
                  <p className="text-xs text-stone-500">{entry.book.title}</p>
                  <h3 className="mt-1 font-medium text-stone-800">{entry.chapter.title}</h3>
                </div>
                <div className="flex flex-wrap gap-2">
                  <span className={`rounded-full px-2.5 py-1 text-xs ${isConfirmed ? 'bg-emerald-50 text-emerald-700' : 'bg-stone-100 text-stone-500'}`}>
                    {isConfirmed ? '目前 revision 已確認' : draft ? `草稿：${draft.status}` : '尚未產生草稿'}
                  </span>
                  <button className="secondary-button px-3 py-1.5 text-xs" disabled={busyChapterId === entry.chapter.id} onClick={() => void buildDraft(entry)} type="button">
                    {draft ? '重新產生草稿' : '產生草稿'}
                  </button>
                </div>
              </div>

              {draft && (
                <div className="mt-4 space-y-2">
                  {draft.segments.map((segment) => (
                    <div className="rounded-xl border border-stone-200 bg-stone-50/70 p-3" key={segment.id}>
                      <div className="flex flex-wrap items-start justify-between gap-3">
                        <p className="min-w-0 flex-1 text-sm leading-6 text-stone-700">{segmentText(entry.chapter, segment)}</p>
                        <div className="flex shrink-0 items-center gap-2">
                          <button
                            className="rounded-full border border-stone-200 bg-white px-2.5 py-1 text-xs text-stone-600 transition hover:bg-stone-100 disabled:opacity-50"
                            disabled={busySegmentId === segment.id}
                            onClick={() => void previewSegmentAudio(entry, segment)}
                            title="單獨試聽此句語音合成效果"
                            type="button"
                          >
                            {playingSegmentId === segment.id ? '播放中 🔊' : '試聽此句 ▶'}
                          </button>
                          <span className={`text-xs ${segment.reviewStatus === 'Confirmed' ? 'text-emerald-700' : 'text-amber-700'}`}>
                            {segment.kind === 'Dialogue' && `${segment.characterName ?? '無法判定'} · `}
                            {segment.kind === 'InnerMonologue' && `內心／默讀：${segment.characterName ?? '無法判定'} · `}
                            {segment.reviewStatus === 'Confirmed'
                              ? '已確認'
                              : segment.reviewStatus === 'Rejected'
                                ? `已拒絕，請重新指派 · 信心 ${segment.confidence}%`
                                : `待審核 · 信心 ${segment.confidence}%`}
                          </span>
                        </div>
                      </div>
                      {segment.kind === 'Dialogue' && segment.reviewStatus !== 'Confirmed' && (
                        <div className="mt-3 flex flex-wrap items-end gap-2">
                          <label className="text-xs text-stone-500">
                            指派角色
                            <select className="auth-input mt-1 min-w-44" defaultValue={segment.characterId ?? ''} id={`speaker-${segment.id}`}>
                              <option value="">無法判定，保留旁白</option>
                              {characters.map((character) => <option key={character.id} value={character.id}>{character.canonicalName}</option>)}
                            </select>
                          </label>
                          <button
                            className="secondary-button px-3 py-2 text-xs"
                            disabled={busySegmentId === segment.id}
                            onClick={() => {
                              const selected = document.getElementById(`speaker-${segment.id}`) as HTMLSelectElement | null
                              void confirmSegment(entry, segment, selected?.value || null)
                            }}
                            type="button"
                          >
                            確認這段角色
                          </button>
                        </div>
                      )}
                    </div>
                  ))}
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
