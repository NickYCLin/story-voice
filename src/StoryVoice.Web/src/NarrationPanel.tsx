import { useCallback, useEffect, useMemo, useState, type CSSProperties } from 'react'
import { Link } from 'react-router-dom'

import { apiUrl, responseProblem } from './api'

type NarrationBook = {
  id: string
  contentBookId: string | null
  authorizedTextAvailable: boolean
}

type NarrationJob = {
  id: string
  bookId: string
  contentBookId: string
  sourceHash: string
  voice: string
  rate: string
  status: 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Cancelled'
  progressPercent: number
  attempts: number
  cancellationRequested: boolean
  errorCode: string | null
  audioBytes: number | null
  rightsAttestedAt: string
  createdAt: string
  updatedAt: string
  completedAt: string | null
}

type Props = {
  book: NarrationBook
  csrfToken: string
}

const statusLabels: Record<NarrationJob['status'], string> = {
  Queued: '排隊中',
  Running: '正在產生語音',
  Completed: '語音已完成',
  Failed: '產製失敗',
  Cancelled: '已取消',
}

const WAVE_HEIGHTS = [.4, .7, .35, .9, .55, .8, .3, .65, .95, .45, .75, .5, .85, .38, .6, .7]

function VoiceWave() {
  return (
    <div aria-hidden="true" className="voice-wave !h-8">
      {WAVE_HEIGHTS.map((height, index) => (
        <span key={index} style={{ '--wave': `${height * 100}%`, '--delay': `${(index % 5) * .15}s` } as CSSProperties} />
      ))}
    </div>
  )
}

function mergeFreshJobs(current: NarrationJob[], incomingJobs: NarrationJob[], bookId: string) {
  const currentForBook = current.filter((job) => job.bookId === bookId)
  const currentById = new Map(currentForBook.map((job) => [job.id, job]))
  const incomingIds = new Set(incomingJobs.map((job) => job.id))
  const merged = incomingJobs.map((incoming) => {
    const existing = currentById.get(incoming.id)
    if (!existing || Date.parse(incoming.updatedAt) >= Date.parse(existing.updatedAt)) return incoming
    return existing
  })
  return [...merged, ...currentForBook.filter((job) => !incomingIds.has(job.id))]
}

export function NarrationPanel({ book, csrfToken }: Props) {
  const [jobs, setJobs] = useState<NarrationJob[]>([])
  const [loading, setLoading] = useState(true)
  const [message, setMessage] = useState('')
  const eligible = book.authorizedTextAvailable || book.contentBookId !== null
  const active = useMemo(
    () => jobs.some((job) => job.status === 'Queued' || job.status === 'Running'),
    [jobs],
  )

  const loadJobs = useCallback(async (signal?: AbortSignal) => {
    const response = await fetch(apiUrl(`/api/books/${book.id}/narrations/`), {
      credentials: 'same-origin',
      signal,
    })
    if (!response.ok) throw new Error(await responseProblem(response, '朗讀工作讀取失敗。'))
    const incomingJobs = await response.json() as NarrationJob[]
    setJobs((current) => mergeFreshJobs(current, incomingJobs, book.id))
  }, [book.id])

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setMessage('')
    loadJobs(controller.signal)
      .catch((error) => {
        if (error instanceof DOMException && error.name === 'AbortError') return
        setMessage(error instanceof Error ? error.message : '朗讀工作讀取失敗。')
      })
      .finally(() => setLoading(false))
    return () => controller.abort()
  }, [loadJobs])

  useEffect(() => {
    if (!active) return
    let stopped = false
    let timer = 0
    let requestController: AbortController | null = null
    const poll = async () => {
      requestController = new AbortController()
      await loadJobs(requestController.signal).catch(() => undefined)
      requestController = null
      if (!stopped) timer = window.setTimeout(poll, 2_000)
    }
    timer = window.setTimeout(poll, 2_000)
    return () => {
      stopped = true
      requestController?.abort()
      window.clearTimeout(timer)
    }
  }, [active, loadJobs])


  async function cancelNarration(jobId: string) {
    setMessage('正在取消朗讀工作…')
    try {
      const response = await fetch(apiUrl(`/api/narrations/${jobId}/cancel`), {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
        body: '{}',
      })
      if (!response.ok) throw new Error(await responseProblem(response, '朗讀工作取消失敗。'))
      const job = await response.json() as NarrationJob
      setJobs((current) => current.map((item) => item.id === job.id ? job : item))
      setMessage('取消要求已送出。')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : '朗讀工作取消失敗。')
    }
  }

  return (
    <section aria-label="AI 朗讀與有聲書" className="mt-5 rounded-2xl border border-amber-100 bg-amber-50/60 p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <p className="text-xs font-semibold uppercase tracking-[0.22em] text-amber-700">合法正文神經語音</p>
          <h4 className="mt-1 font-serif text-lg text-stone-800">AI 朗讀與有聲書</h4>
          <p className="mt-2 max-w-3xl text-xs leading-6 text-stone-500">
            StoryVoice 只處理你主動匯入、且有權使用的無 DRM EPUB／TXT 正文。建立系列配音時，文字會交給該系列目前設定的語音服務（可能是私人本機自架或外部供應商），音訊完成後保存於你的私人 StoryVoice 帳號。
          </p>
        </div>
        <span className="shrink-0 rounded-full border border-amber-200 bg-amber-100 px-3 py-1 text-xs text-amber-800">
          {eligible ? '正文已就緒' : '等待合法正文'}
        </span>
      </div>

      {!eligible && (
        <p className="mt-4 rounded-xl border border-stone-200 bg-white p-4 text-xs leading-6 text-stone-500">
          目前沒有可處理的正文；請先匯入你合法持有、無 DRM 的 EPUB 或 UTF-8 TXT。
        </p>
      )}

      {eligible && (
        <div className="mt-4 rounded-xl border border-stone-200 bg-white p-4">
          <p className="text-sm leading-6 text-stone-600">
            新的合法正文一律透過多角色系列配音建立：固定旁白與角色聲線、逐章審核、全系列 staged rebuild，再由你人工啟用。既有單人音訊仍可在下方讀取、播放與取消。
          </p>
          <Link className="secondary-button mt-3 inline-flex" to="/series">前往多角色系列配音</Link>
        </div>
      )}

      <div aria-live="polite" className="mt-3 min-h-5 text-xs text-stone-500">{message}</div>

      <div className="mt-4 space-y-3">
        {jobs.map((job) => (
          <article className="rounded-xl border border-stone-200 bg-white p-4" key={job.id}>
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div>
                <p className="font-semibold text-stone-800">{statusLabels[job.status]}</p>
                <p className="mt-1 text-xs text-stone-500">{job.voice} · 語速 {job.rate} · 嘗試 {job.attempts}/3</p>
              </div>
              <span className="rounded-full border border-stone-200 px-3 py-1 text-xs text-stone-600">{job.progressPercent}%</span>
            </div>
            {job.status === 'Running' && <VoiceWave />}
            {(job.status === 'Queued' || job.status === 'Running') && (
              <div className="mt-3">
                <div
                  aria-label="語音產製進度"
                  aria-valuemax={100}
                  aria-valuemin={0}
                  aria-valuenow={job.progressPercent}
                  className="h-2 overflow-hidden rounded-full bg-stone-100"
                  role="progressbar"
                >
                  <div
                    className="h-full rounded-full bg-amber-500 transition-[width] duration-300 motion-reduce:transition-none"
                    style={{ width: `${job.progressPercent}%` }}
                  />
                </div>
                <p className="mt-1 text-xs text-stone-500">
                  {job.status === 'Queued' ? '等待執行' : '分塊語音合成中'}
                </p>
              </div>
            )}
            {(job.status === 'Queued' || job.status === 'Running') && (
              <button
                className="secondary-button mt-3 px-4 py-2 text-xs"
                onClick={() => cancelNarration(job.id)}
                type="button"
              >
                取消工作
              </button>
            )}
            {job.status === 'Completed' && (
              <audio className="mt-4 w-full" controls preload="metadata" src={apiUrl(`/api/narrations/${job.id}/audio`)}>
                你的瀏覽器不支援音訊播放。
              </audio>
            )}
            {job.status === 'Failed' && (
              <p className="mt-3 text-sm text-rose-600">語音服務未能完成這次工作（{job.errorCode ?? 'provider_failed'}）。重新確認授權後可再次建立。</p>
            )}
          </article>
        ))}
        {!loading && jobs.length === 0 && eligible && (
          <p className="text-xs text-stone-600">這本書尚未建立 StoryVoice 音訊。</p>
        )}
      </div>
    </section>
  )
}
