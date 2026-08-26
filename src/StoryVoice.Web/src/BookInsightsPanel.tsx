import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'

import { apiUrl, responseProblem } from './api'
import type { BookDetails } from './types'

type LoadState = 'idle' | 'loading' | 'ready' | 'error'

type LocalLlmCharacterCandidate = {
  name: string
  confidence: 'high' | 'medium'
  dialogueEvidenceCount: number
  evidenceChapterNumbers: number[]
  aliases?: string[]
}

type LocalLlmCharacterAnalysis = {
  bookId: string
  contentBookId: string
  generator: 'local-ollama'
  model: string
  promptVersion: string
  sourceHash: string
  generatedAt: string
  candidates: LocalLlmCharacterCandidate[]
}

type ReadingNote = {
  id: string
  bookId: string
  chapterId: string | null
  body: string
  createdAt: string
  updatedAt: string
}

type StorySeriesSummary = {
  id: string
  name: string
  narratorProvider: string
  bookCount: number
  characterCount: number
}

type SeriesVoiceOption = {
  provider: string
  voice: string
  displayName: string
  locale: string
}

type CandidateDraft = {
  selected: boolean
  canonicalName: string
  aliases: string
  role: 'Main' | 'Supporting' | 'Minor'
  voiceKey: string
}

const voiceKey = (voice: SeriesVoiceOption) => `${voice.provider}\n${voice.voice}`
const splitAliases = (value: string) => value
  .split(/[、,，\n]/)
  .map((alias) => alias.normalize('NFKC').trim())
  .filter((alias, index, aliases) => alias !== '' && aliases.indexOf(alias) === index)

type BookInsightsPanelProps = {
  book: BookDetails
  csrfToken: string
}

export function BookInsightsPanel({ book, csrfToken }: BookInsightsPanelProps) {
  const [notes, setNotes] = useState<ReadingNote[]>([])
  const [notesState, setNotesState] = useState<LoadState>('loading')
  const [noteDraft, setNoteDraft] = useState('')
  const [noteMessage, setNoteMessage] = useState('')
  const [characterAnalysis, setCharacterAnalysis] = useState<LocalLlmCharacterAnalysis | null>(null)
  const [characterAnalysisState, setCharacterAnalysisState] = useState<LoadState>('loading')
  const [characterAnalysisMessage, setCharacterAnalysisMessage] = useState('')
  const [seriesOptions, setSeriesOptions] = useState<StorySeriesSummary[]>([])
  const [voiceOptions, setVoiceOptions] = useState<SeriesVoiceOption[]>([])
  const [selectedSeriesId, setSelectedSeriesId] = useState('')
  const [candidateDrafts, setCandidateDrafts] = useState<Record<string, CandidateDraft>>({})
  const [applyState, setApplyState] = useState<LoadState>('idle')
  const [applyMessage, setApplyMessage] = useState('')

  const canAnalyzeText = book.authorizedTextAvailable
  const selectedSeries = seriesOptions.find((series) => series.id === selectedSeriesId)
  const applicableVoiceOptions = useMemo(() => {
    if (!selectedSeries) return []
    return voiceOptions.filter((voice) => voice.provider === selectedSeries.narratorProvider
      || (selectedSeries.narratorProvider === '3wa-voxcpm2' && voice.provider === 'edge'))
      .filter((voice) => voice.provider !== '3wa-voxcpm2')
  }, [selectedSeries, voiceOptions])

  useEffect(() => {
    const controller = new AbortController()
    setNotes([])
    setNotesState('loading')
    setNoteDraft('')
    setNoteMessage('')
    setCharacterAnalysis(null)
    setCharacterAnalysisState('loading')
    setCharacterAnalysisMessage('')
    setCandidateDrafts({})
    setApplyState('idle')
    setApplyMessage('')

    fetch(apiUrl(`/api/books/${book.id}/character-analysis`), {
      credentials: 'same-origin',
      signal: controller.signal,
    }).then(async (response) => {
      if (response.status === 404 || response.status === 409) return null
      if (!response.ok) throw new Error(await responseProblem(response, '本機 LLM 角色分析讀取失敗。'))
      return response.json() as Promise<LocalLlmCharacterAnalysis>
    }).then((analysis) => {
      setCharacterAnalysis(analysis)
      setCharacterAnalysisState('ready')
    }).catch((error) => {
      if (error instanceof DOMException && error.name === 'AbortError') return
      setCharacterAnalysisState('error')
      setCharacterAnalysisMessage(error instanceof Error ? error.message : '本機 LLM 角色分析讀取失敗。')
    })

    fetch(apiUrl(`/api/books/${book.id}/notes`), {
      credentials: 'same-origin',
      signal: controller.signal,
    }).then(async (response) => {
      if (!response.ok) throw new Error(await responseProblem(response, '閱讀筆記讀取失敗。'))
      return response.json() as Promise<ReadingNote[]>
    }).then((items) => {
      setNotes(items)
      setNotesState('ready')
    }).catch((error) => {
      if (error instanceof DOMException && error.name === 'AbortError') return
      setNotesState('error')
      setNoteMessage(error instanceof Error ? error.message : '閱讀筆記讀取失敗。')
    })

    return () => controller.abort()
  }, [book.id])

  useEffect(() => {
    const controller = new AbortController()
    Promise.all([
      fetch(apiUrl('/api/series/'), { credentials: 'same-origin', signal: controller.signal }),
      fetch(apiUrl('/api/series/voice-options'), { credentials: 'same-origin', signal: controller.signal }),
    ]).then(async ([seriesResponse, voicesResponse]) => {
      if (!seriesResponse.ok || !voicesResponse.ok) throw new Error('series_setup_failed')
      const series = await seriesResponse.json() as StorySeriesSummary[]
      const voices = await voicesResponse.json() as SeriesVoiceOption[]
      setSeriesOptions(series)
      setVoiceOptions(voices)
      setSelectedSeriesId((current) => current || series[0]?.id || '')
    }).catch((error) => {
      if (error instanceof DOMException && error.name === 'AbortError') return
      setApplyState('error')
      setApplyMessage('系列與聲線清單讀取失敗，請重新整理。')
    })
    return () => controller.abort()
  }, [])

  useEffect(() => {
    if (!characterAnalysis) return
    setCandidateDrafts((current) => Object.fromEntries(characterAnalysis.candidates.map((candidate, index) => {
      const existing = current[candidate.name]
      const fallbackVoice = applicableVoiceOptions[index % Math.max(applicableVoiceOptions.length, 1)]
      return [candidate.name, {
        selected: existing?.selected ?? candidate.confidence === 'high',
        canonicalName: existing?.canonicalName ?? candidate.name,
        aliases: existing?.aliases ?? (candidate.aliases ?? []).join('、'),
        role: existing?.role ?? (candidate.confidence === 'high' ? 'Supporting' : 'Minor'),
        voiceKey: existing && applicableVoiceOptions.some((voice) => voiceKey(voice) === existing.voiceKey)
          ? existing.voiceKey
          : fallbackVoice ? voiceKey(fallbackVoice) : '',
      }]
    })))
  }, [applicableVoiceOptions, characterAnalysis])

  async function handleGenerateCharacterAnalysis() {
    setCharacterAnalysisState('loading')
    setCharacterAnalysisMessage('本機 LLM 正在逐章讀取完整正文並彙整候選；此期間會獨占本機模型，完成後立即卸載。')
    try {
      const response = await fetch(apiUrl(`/api/books/${book.id}/character-analysis`), {
        method: 'PUT',
        credentials: 'same-origin',
        headers: { 'X-CSRF-TOKEN': csrfToken },
      })
      if (!response.ok) throw new Error(await responseProblem(response, '本機 LLM 角色分析失敗。'))
      const analysis = await response.json() as LocalLlmCharacterAnalysis
      setCharacterAnalysis(analysis)
      setCandidateDrafts({})
      setCharacterAnalysisState('ready')
      setCharacterAnalysisMessage('本機 LLM 分析完成；請勾選、校正 canonical 名稱並合併 alias，再套用到系列。')
    } catch (error) {
      setCharacterAnalysisState('error')
      setCharacterAnalysisMessage(error instanceof Error ? error.message : '本機 LLM 角色分析失敗。')
    }
  }

  function updateCandidate(name: string, patch: Partial<CandidateDraft>) {
    setCandidateDrafts((current) => ({
      ...current,
      [name]: { ...current[name], ...patch },
    }))
  }

  async function handleApplyAnalyzedCharacters() {
    if (!characterAnalysis || !selectedSeriesId) return
    let selections: Array<{
      sourceName: string
      canonicalName: string
      aliases: string[]
      role: CandidateDraft['role']
      voiceProvider: string
      voice: string
      rate: string
      pitch: string
      volume: string
    }>
    try {
      selections = characterAnalysis.candidates
        .filter((candidate) => candidateDrafts[candidate.name]?.selected)
        .map((candidate) => {
          const draft = candidateDrafts[candidate.name]
          const voice = applicableVoiceOptions.find((option) => voiceKey(option) === draft.voiceKey)
          if (!draft.canonicalName.trim() || !voice) throw new Error(`請補齊「${candidate.name}」的 canonical 名稱與聲線。`)
          return {
            sourceName: candidate.name,
            canonicalName: draft.canonicalName.trim(),
            aliases: splitAliases(draft.aliases),
            role: draft.role,
            voiceProvider: voice.provider,
            voice: voice.voice,
            rate: '+0%',
            pitch: '+0Hz',
            volume: '+0%',
          }
        })
    } catch (error) {
      setApplyState('error')
      setApplyMessage(error instanceof Error ? error.message : '角色候選資料不完整。')
      return
    }
    if (selections.length === 0) {
      setApplyState('error')
      setApplyMessage('請至少勾選一位角色候選。')
      return
    }

    setApplyState('loading')
    setApplyMessage('正在合併 alias、建立系列角色表並加入這本書…')
    try {
      const response = await fetch(apiUrl(`/api/series/${selectedSeriesId}/analyzed-characters`), {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
        body: JSON.stringify({ bookId: book.id, characters: selections }),
      })
      if (!response.ok) throw new Error(await responseProblem(response, '角色候選套用失敗。'))
      setApplyState('ready')
      setApplyMessage(`已套用 ${selections.length} 筆候選；下一步到系列頁設定視角角色並產生逐章說話者草稿。`)
    } catch (error) {
      setApplyState('error')
      setApplyMessage(error instanceof Error ? error.message : '角色候選套用失敗。')
    }
  }

  async function handleAddNote(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const body = noteDraft.trim()
    if (!body) return
    setNotesState('loading')
    setNoteMessage('正在保存你的筆記…')
    try {
      const response = await fetch(apiUrl(`/api/books/${book.id}/notes`), {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
        body: JSON.stringify({ body, chapterId: null }),
      })
      if (!response.ok) throw new Error(await responseProblem(response, '筆記保存失敗。'))
      const note = await response.json() as ReadingNote
      setNotes((current) => [note, ...current])
      setNoteDraft('')
      setNotesState('ready')
      setNoteMessage('你的手動閱讀筆記已保存。')
    } catch (error) {
      setNotesState('error')
      setNoteMessage(error instanceof Error ? error.message : '筆記保存失敗。')
    }
  }

  async function handleDeleteNote(noteId: string) {
    if (!window.confirm('確定刪除這則閱讀筆記？')) return
    setNotesState('loading')
    try {
      const response = await fetch(apiUrl(`/api/books/${book.id}/notes/${noteId}`), {
        method: 'DELETE',
        credentials: 'same-origin',
        headers: { 'X-CSRF-TOKEN': csrfToken },
      })
      if (!response.ok) throw new Error(await responseProblem(response, '筆記刪除失敗。'))
      setNotes((current) => current.filter((note) => note.id !== noteId))
      setNotesState('ready')
      setNoteMessage('筆記已刪除。')
    } catch (error) {
      setNotesState('error')
      setNoteMessage(error instanceof Error ? error.message : '筆記刪除失敗。')
    }
  }

  return (
    <div className="mt-5 space-y-5">
      <section className="rounded-2xl border border-rose-100 bg-rose-50/70 p-4" aria-label="本機 LLM 角色分析">
        <div className="flex flex-col justify-between gap-3 sm:flex-row sm:items-start">
          <div>
            <h4 className="font-serif text-lg text-stone-800">本機 LLM 角色與 alias 分析 <span className="text-xs font-sans text-rose-700">勾選後套用系列</span></h4>
            <p className="mt-2 text-xs leading-6 text-stone-500">完整逐章分析後，先由你勾選候選、校正 canonical 名稱並合併 alias；套用時會自動加入系列冊次與建立缺少的系列角色，不會刪除既有角色。</p>
          </div>
          <button className="secondary-button shrink-0 disabled:cursor-not-allowed disabled:opacity-40" disabled={!canAnalyzeText || characterAnalysisState === 'loading'} onClick={handleGenerateCharacterAnalysis} type="button">{characterAnalysis ? '重新以本機 LLM 分析' : '以本機 LLM 分析'}</button>
        </div>
        {!canAnalyzeText && <p className="mt-3 text-xs text-amber-700">等待合法正文：請先匯入你有權處理的 EPUB 或 UTF-8 TXT。</p>}
        {characterAnalysis && (
          <>
            <p className="mt-3 text-[10px] tracking-wide text-stone-400">模型 {characterAnalysis.model} · {characterAnalysis.promptVersion} · {new Date(characterAnalysis.generatedAt).toLocaleString('zh-TW')}</p>
            {characterAnalysis.candidates.length > 0 && (
              <div className="mt-4 space-y-3">
                {characterAnalysis.candidates.map((candidate) => (
                  <article className="rounded-xl border border-rose-100 bg-white p-3" key={candidate.name}>
                    <div className="flex flex-wrap items-center gap-2 text-xs">
                      <label className="flex items-center gap-2 font-medium text-stone-800">
                        <input checked={candidateDrafts[candidate.name]?.selected ?? false} onChange={(event) => updateCandidate(candidate.name, { selected: event.target.checked })} type="checkbox" />
                        {candidate.name}
                      </label>
                      <span className={candidate.confidence === 'high' ? 'text-rose-700' : 'text-amber-700'}>{candidate.confidence === 'high' ? '高信心' : '中信心'}</span>
                      <span className="text-stone-400">{candidate.dialogueEvidenceCount} 句 · 第 {candidate.evidenceChapterNumbers.join('、')} 章</span>
                    </div>
                    <div className="mt-3 grid grid-cols-1 gap-2 lg:grid-cols-2">
                      <label className="text-[10px] text-stone-500">Canonical 名稱<input className="auth-input mt-1" maxLength={80} onChange={(event) => updateCandidate(candidate.name, { canonicalName: event.target.value })} value={candidateDrafts[candidate.name]?.canonicalName ?? candidate.name} /></label>
                      <label className="text-[10px] text-stone-500">Aliases（以、分隔）<input className="auth-input mt-1" maxLength={500} onChange={(event) => updateCandidate(candidate.name, { aliases: event.target.value })} value={candidateDrafts[candidate.name]?.aliases ?? (candidate.aliases ?? []).join('、')} /></label>
                      <label className="text-[10px] text-stone-500">角色層級<select className="auth-input mt-1" onChange={(event) => updateCandidate(candidate.name, { role: event.target.value as CandidateDraft['role'] })} value={candidateDrafts[candidate.name]?.role ?? 'Minor'}><option value="Main">主要角色</option><option value="Supporting">次要角色</option><option value="Minor">小角色</option></select></label>
                      <label className="text-[10px] text-stone-500">聲線<select className="auth-input mt-1" onChange={(event) => updateCandidate(candidate.name, { voiceKey: event.target.value })} value={candidateDrafts[candidate.name]?.voiceKey ?? ''}><option value="">選擇聲線</option>{applicableVoiceOptions.map((voice) => <option key={voiceKey(voice)} value={voiceKey(voice)}>{voice.displayName} · {voice.locale}</option>)}</select></label>
                    </div>
                  </article>
                ))}
                <div className="rounded-xl border border-stone-200 bg-stone-50 p-3">
                  <label className="text-xs text-stone-500">套用到系列<select className="auth-input mt-2" onChange={(event) => setSelectedSeriesId(event.target.value)} value={selectedSeriesId}><option value="">選擇系列</option>{seriesOptions.map((series) => <option key={series.id} value={series.id}>{series.name} · {series.characterCount} 位角色</option>)}</select></label>
                  {selectedSeries && <p className="mt-2 text-xs text-stone-500">角色聲線已限制為系列 provider：{selectedSeries.narratorProvider}，避免建立無法合成的混用 cast。</p>}
                  {seriesOptions.length === 0 && <p className="mt-2 text-xs text-amber-700">尚無系列，請先到「系列配音」建立系列與旁白聲線。</p>}
                  <div className="mt-3 flex flex-wrap items-center gap-3">
                    <button className="secondary-button disabled:opacity-40" disabled={!selectedSeriesId || applyState === 'loading' || Object.values(candidateDrafts).every((draft) => !draft.selected)} onClick={() => void handleApplyAnalyzedCharacters()} type="button">{applyState === 'loading' ? '套用中…' : '建立／合併系列角色表'}</button>
                    <Link className="text-xs font-medium text-rose-700 hover:text-rose-800" to={selectedSeriesId ? `/series?seriesId=${selectedSeriesId}` : '/series'}>前往系列配音 →</Link>
                  </div>
                  <p className={`mt-2 min-h-5 text-xs ${applyState === 'error' ? 'text-rose-600' : 'text-stone-500'}`} role="status">{applyMessage}</p>
                </div>
              </div>
            )}
            {characterAnalysis.candidates.length === 0 && <p className="mt-3 text-xs text-stone-400">本機模型沒有足夠證據列出命名說話者；未知比亂填好。</p>}
          </>
        )}
        {canAnalyzeText && characterAnalysisState === 'ready' && !characterAnalysis && <p className="mt-3 text-xs text-stone-400">尚未執行本機 LLM 分析；按上方按鈕才會使用本機模型。</p>}
        <p className={`mt-3 min-h-5 text-xs ${characterAnalysisState === 'error' ? 'text-rose-600' : 'text-stone-500'}`} role="status">{characterAnalysisState === 'loading' && !characterAnalysisMessage ? '正在讀取本機 LLM 分析…' : characterAnalysisMessage}</p>
      </section>

      <section className="rounded-2xl border border-violet-100 bg-violet-50/70 p-4" aria-label="我的閱讀筆記">
        <h4 className="font-serif text-lg text-stone-800">我的閱讀筆記</h4>
        <p className="mt-2 text-xs leading-6 text-stone-500">這裡只保存你親自輸入的帳號筆記；書目只有 metadata 也能記事，不代表 StoryVoice 擁有正文。</p>
        <form className="mt-3 space-y-3" onSubmit={handleAddNote}>
          <textarea aria-label="閱讀筆記內容" className="auth-input min-h-28 resize-y" maxLength={4000} onChange={(event) => setNoteDraft(event.target.value)} placeholder="寫下你的想法、待查資料或閱讀進度…" value={noteDraft} />
          <div className="flex justify-between gap-3 text-xs text-stone-400">
            <span>{noteDraft.length}／4000</span>
            <button className="secondary-button disabled:cursor-not-allowed disabled:opacity-40" disabled={!noteDraft.trim() || notesState === 'loading'} type="submit">保存筆記</button>
          </div>
        </form>
        {notes.length > 0 && (
          <ul className="mt-4 space-y-3">
            {notes.map((note) => (
              <li className="rounded-xl border border-stone-200 bg-stone-50 p-3" key={note.id}>
                <p className="whitespace-pre-wrap text-sm leading-7 text-stone-600">{note.body}</p>
                <div className="mt-3 flex items-center justify-between gap-3 text-[10px] text-stone-400">
                  <time dateTime={note.updatedAt}>{new Date(note.updatedAt).toLocaleString('zh-TW')}</time>
                  <button className="text-rose-500 transition hover:text-rose-700" onClick={() => handleDeleteNote(note.id)} type="button">刪除</button>
                </div>
              </li>
            ))}
          </ul>
        )}
        {notesState === 'ready' && notes.length === 0 && <p className="mt-4 text-xs text-stone-400">尚未建立閱讀筆記。</p>}
        <p className={`mt-3 min-h-5 text-xs ${notesState === 'error' ? 'text-rose-600' : 'text-stone-500'}`} role="status">{notesState === 'loading' && !noteMessage ? '正在讀取筆記…' : noteMessage}</p>
      </section>
    </div>
  )
}
