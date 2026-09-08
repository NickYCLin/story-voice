import { useEffect, useId, useRef, useState } from 'react'
import { fetchJson } from '../api'

type AttemptUsage = {
  id: string
  startedAt: string
  finishedAt: string | null
  outcome: string
  provider: string | null
  inputCharacters: number | null
  completedChunks: number | null
  totalChunks: number | null
  elapsedMs: number | null
  synthesisElapsedMs: number | null
  audioBytes: number | null
}
type Usage = { jobId: string; totalAttempts: number; attempts: AttemptUsage[] }
const outcomes: Record<string, string> = {
  Running: '處理中', Completed: '已完成', Failed: '失敗', TimedOut: '逾時', Cancelled: '已取消',
  WorkerStopped: '服務停止', LeaseLost: '工作已由其他程序接手', Unknown: '結果未確認',
}
const engines: Record<string, string> = { edge: 'Edge', bluemagpie: 'BlueMagpie', voai: 'VoAI', '3wa-voxcpm2': '3wa' }
const number = (value: number | null) => value == null ? '未記錄' : value.toLocaleString('zh-TW')
const seconds = (value: number | null) => value == null ? '未記錄' : `${(value / 1000).toLocaleString('zh-TW', { maximumFractionDigits: 2 })} 秒`

export function NarrationUsagePanel({ jobId }: { jobId: string }) {
  return <UsageDetails key={jobId} jobId={jobId} />
}

function UsageDetails({ jobId }: { jobId: string }) {
  const regionId = useId()
  const [open, setOpen] = useState(false)
  const [usage, setUsage] = useState<Usage | null>(null)
  const [status, setStatus] = useState<'idle' | 'loading' | 'error'>('idle')
  const pending = useRef<AbortController | null>(null)
  useEffect(() => () => pending.current?.abort(), [])

  async function refresh() {
    pending.current?.abort()
    const controller = new AbortController()
    pending.current = controller
    setStatus('loading')
    try {
      const result = await fetchJson<Usage>(`/api/narrations/${jobId}/usage`, {
        signal: AbortSignal.any([controller.signal, AbortSignal.timeout(10_000)]),
      })
      if (controller.signal.aborted) return
      setUsage(result)
      setStatus('idle')
    } catch {
      if (!controller.signal.aborted) setStatus('error')
    }
  }

  return (
    <div className="mt-3 min-w-0">
      <button aria-controls={regionId} aria-expanded={open} className="secondary-button px-3 py-2 text-xs" onClick={() => {
        setOpen(!open)
        if (!open) void refresh()
        else { pending.current?.abort(); setStatus('idle') }
      }} type="button">{open ? '收起配音用量' : '查看配音用量'}</button>
      {open && (
        <section aria-label="配音用量" className="mt-3 rounded-xl border border-stone-200 bg-stone-50 p-3 text-xs text-stone-600" id={regionId}>
          <p className="leading-5">字數是每次配音接收的文字，可能包含重試或重用音訊的部分。這裡尚未換算金額，也不代表供應商的計費用量。</p>
          {status === 'loading' && <p className="mt-2" role="status">讀取用量中…</p>}
          {status === 'error' && <p className="mt-2 text-rose-700" role="alert">用量暫時無法讀取，請再試一次。</p>}
          {usage && (
            <>
              <p className="mt-3">已記錄 {number(usage.totalAttempts)} 次配音嘗試{usage.totalAttempts > usage.attempts.length ? `，顯示最近 ${usage.attempts.length} 次` : ''}。</p>
              {usage.attempts.length === 0 && <p className="mt-2">目前沒有紀錄；較早的工作不會補算用量。</p>}
              <ol className="mt-3 space-y-3">
                {usage.attempts.map(attempt => (
                  <li className="rounded-lg border border-stone-200 bg-white p-3" key={attempt.id}>
                    <div className="flex flex-wrap justify-between gap-2">
                      <strong>{outcomes[attempt.outcome] ?? outcomes.Unknown}</strong>
                      <time dateTime={attempt.startedAt}>{new Date(attempt.startedAt).toLocaleString('zh-TW')}</time>
                    </div>
                    <dl className="mt-3 grid grid-cols-1 gap-2 sm:grid-cols-2">
                      <div><dt className="inline">引擎：</dt><dd className="inline">{engines[attempt.provider ?? ''] ?? '未記錄'}</dd></div>
                      <div><dt className="inline">字數：</dt><dd className="inline">{number(attempt.inputCharacters)}</dd></div>
                      <div><dt className="inline">已處理片段：</dt><dd className="inline">{attempt.completedChunks == null || attempt.totalChunks == null ? '未記錄' : `${number(attempt.completedChunks)} / ${number(attempt.totalChunks)}`}</dd></div>
                      <div><dt className="inline">總耗時：</dt><dd className="inline">{seconds(attempt.elapsedMs)}</dd></div>
                      <div><dt className="inline">配音流程耗時：</dt><dd className="inline">{seconds(attempt.synthesisElapsedMs)}</dd></div>
                      <div><dt className="inline">成品大小：</dt><dd className="inline">{attempt.audioBytes == null ? '未記錄' : `${number(attempt.audioBytes)} bytes`}</dd></div>
                    </dl>
                  </li>
                ))}
              </ol>
            </>
          )}
          <button className="secondary-button mt-3 px-3 py-2 text-xs disabled:opacity-60" disabled={status === 'loading'} onClick={() => void refresh()} type="button">更新用量</button>
        </section>
      )}
    </div>
  )
}
