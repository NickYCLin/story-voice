import { useEffect, useState } from 'react'
import { apiUrl, responseProblem } from '../api'
import { AudioPlayer } from './AudioPlayer'

type Track = {
  jobId: string; mode: string; visibility: string; status: string; castRevisionId: string | null
  sourceHash: string; artifactVersion: string; audioBytes: number | null; createdAt: string
  available: boolean; blockedReason: string | null
}
type TrackState = { bookId: string; revision: number; selectedJobId: string | null; tracks: Track[] }

export function PrivateNarrationTracks({ bookId, csrfToken }: { bookId: string; csrfToken: string }) {
  const [state, setState] = useState<TrackState | null>(null)
  const [candidateId, setCandidateId] = useState('')
  const [message, setMessage] = useState('')
  const [saving, setSaving] = useState(false)
  useEffect(() => {
    const controller = new AbortController()
    setState(null)
    setCandidateId('')
    setMessage('')
    void fetch(apiUrl(`/api/books/${bookId}/narrations/tracks`), { signal: controller.signal, credentials: 'same-origin' })
      .then(async response => {
        if (!response.ok) throw new Error(await responseProblem(response, '音軌版本讀取失敗。'))
        const loaded = await response.json() as TrackState
        if (loaded.bookId !== bookId || !Array.isArray(loaded.tracks)) throw new Error('音軌版本資料不正確。')
        if (!controller.signal.aborted) { setState(loaded); setCandidateId(loaded.selectedJobId ?? '') }
      }).catch(error => { if (!controller.signal.aborted) setMessage(error instanceof Error ? error.message : '音軌版本讀取失敗。') })
    return () => controller.abort()
  }, [bookId])
  const candidate = state?.tracks.find(track => track.jobId === candidateId)
  async function selectTrack() {
    if (!state || !candidate?.available || saving) return
    setSaving(true)
    setMessage('')
    try {
      const response = await fetch(apiUrl(`/api/books/${bookId}/narrations/tracks/selection`), {
        method: 'PUT', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
        body: JSON.stringify({ jobId: candidate.jobId, expectedRevision: state.revision, expectedArtifactVersion: candidate.artifactVersion }),
      })
      if (response.status === 409) {
        setState(await response.json() as TrackState)
        setMessage('音軌或選擇已變更，請核對最新資料後再選擇。')
        return
      }
      if (!response.ok) throw new Error(await responseProblem(response, '音軌選擇未儲存。'))
      setState(await response.json() as TrackState)
      setMessage('已儲存私人播放版本，其他音軌仍保留。')
    } catch (error) { setMessage(error instanceof Error ? error.message : '音軌選擇未儲存。') }
    finally { setSaving(false) }
  }
  return <section aria-label="私人音軌版本" className="mt-4 rounded-xl border border-stone-200 bg-white p-4">
    <h5 className="font-semibold text-stone-800">私人音軌版本</h5>
    <p className="mt-2 text-xs leading-6 text-stone-500">可重用既有完成音檔，包含待啟用與歷史版本。此選擇只決定這本書的私人播放版本；全系列正式配音仍由系列頁啟用。</p>
    {state && state.tracks.length > 0 && <>
      <label className="mt-3 block text-sm">檢視與試聽版本
        <select className="mt-1 block w-full rounded-lg border border-stone-300 p-2" value={candidateId} disabled={saving} onChange={event => setCandidateId(event.target.value)}>
          <option value="">請選擇音軌</option>
          {state.tracks.map(track => <option key={track.jobId} value={track.jobId} disabled={!track.available}>
            {track.mode === 'MultiCharacter' ? '多角色' : '單人'} · {new Date(track.createdAt).toLocaleDateString()} · {track.visibility === 'Staged' ? '待啟用' : track.visibility === 'Historical' ? '歷史' : '已發布'}{!track.available ? '（音檔不可用）' : ''}
          </option>)}
        </select>
      </label>
      {candidate && <div className="mt-3 space-y-2 text-xs text-stone-500">
        <p className="break-all">音軌：{candidate.jobId} · 來源版本：{candidate.sourceHash}</p>
        <p>檔案大小：{candidate.audioBytes?.toLocaleString() ?? '—'} bytes。已核對檔案存在與大小，未驗證逐字發音。</p>
        {candidate.available && <AudioPlayer key={`${bookId}-${candidate.jobId}`} src={apiUrl(`/api/books/${bookId}/narrations/tracks/${candidate.jobId}/audio`)} storageKey={`private-track-${candidate.jobId}`} title="私人音軌試聽" />}
        <button className="secondary-button" type="button" disabled={saving || !candidate.available || state.selectedJobId === candidate.jobId} onClick={() => void selectTrack()}>
          {state.selectedJobId === candidate.jobId ? '目前私人播放版本' : saving ? '正在儲存…' : '設為這本書的私人播放版本'}
        </button>
      </div>}
    </>}
    {state?.tracks.length === 0 && <p className="mt-3 text-xs text-stone-500">目前沒有既有音軌。</p>}
    <p className="mt-2 text-xs text-stone-600" role="status">{message}</p>
  </section>
}
