import { useCallback, useEffect, useState } from 'react'

import { apiUrl } from './api'

type LibraryBookStatus = {
  bookId: string
  title: string
  sourceType: string
  officialReaderAvailable: boolean
  officialTtsAvailable: boolean | null
  authorizedTextAvailable: boolean
  readingNoteCount: number
  storyVoiceNarrationStatus: string | null
  storyVoiceNarrationMatchesAuthorizedText: boolean
  state: 'blocked' | 'ready' | 'processing' | 'audio_ready' | 'failed'
  blockedReason: string | null
}

type Props = {
  refreshKey: string
}

const narrationLabel = (status: LibraryBookStatus) => {
  if (!status.storyVoiceNarrationStatus) return '尚未建立 StoryVoice 音訊'
  const label = status.storyVoiceNarrationMatchesAuthorizedText
    ? 'StoryVoice 音訊'
    : '既有 StoryVoice 音訊（非目前合法正文）'
  switch (status.storyVoiceNarrationStatus) {
    case 'Completed': return `${label}：已完成`
    case 'Running': return `${label}：產製中`
    case 'Queued': return `${label}：排隊中`
    case 'Failed': return `${label}：失敗`
    case 'Cancelled': return `${label}：已取消`
    default: return `${label}：狀態未知`
  }
}

const officialTtsLabel = (value: boolean | null) => {
  if (value === true) return '官方 TTS：有'
  if (value === false) return '官方 TTS：無'
  return '官方 TTS：未知'
}

export default function LibraryStatusMatrix({ refreshKey }: Props) {
  const [items, setItems] = useState<LibraryBookStatus[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true)
    setError('')
    try {
      const response = await fetch(apiUrl('/api/library/status-matrix/'), {
        credentials: 'same-origin',
        signal,
      })
      if (!response.ok) throw new Error(`status_${response.status}`)
      setItems(await response.json() as LibraryBookStatus[])
    } catch (requestError) {
      if ((requestError as Error).name !== 'AbortError') {
        setError('狀態矩陣暫時讀取失敗，請重新整理。')
      }
    } finally {
      if (!signal?.aborted) setLoading(false)
    }
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal)
    return () => controller.abort()
  }, [load, refreshKey])

  return (
    <details className="status-matrix">
      <summary>
        <span>全部書籍處理狀態矩陣</span>
        <span className="status-matrix-count">{loading && items.length === 0 ? '讀取中…' : `${items.length} 本`}</span>
      </summary>
      <div className="status-matrix-toolbar">
        <p>
          「官方 TTS」只代表來源閱讀器宣告的能力；「StoryVoice 音訊」只會由你明確提供的合法 EPUB／TXT 產生。
        </p>
        <button type="button" className="secondary-button" onClick={() => void load()} disabled={loading}>
          {loading ? '更新中…' : '重新整理狀態'}
        </button>
      </div>
      {error && <p className="px-1 text-xs text-rose-600" role="alert">{error}</p>}
      {loading && items.length === 0 && (
        <p className="library-state" role="status">正在讀取全部書籍的處理狀態…</p>
      )}
      {!loading && !error && items.length === 0 && (
        <p className="library-state">目前沒有可顯示的書籍。</p>
      )}
      <div className="status-matrix-grid">
        {items.map((item) => (
          <article className={`status-matrix-card state-${item.state}`} key={item.bookId}>
            <div className="status-matrix-heading">
              <h3>{item.title}</h3>
              <span className="status-pill">metadata 已保存</span>
            </div>
            <div className="status-matrix-facts">
              <span>{item.officialReaderAvailable ? '官方閱讀器：有' : '官方閱讀器：無'}</span>
              <span>{officialTtsLabel(item.officialTtsAvailable)}</span>
              <span>{item.authorizedTextAvailable ? '可處理正文：已就緒' : '可處理正文：未提供'}</span>
              <span>你的筆記：{item.readingNoteCount} 則</span>
              <span>{narrationLabel(item)}</span>
            </div>
            {item.blockedReason === 'authorized_text_required' && (
              <p className="status-matrix-blocked">等待正文：這筆書籍資料沒有可處理內容；請匯入合法、無 DRM 的 EPUB／TXT，新匯入的書會直接可用。</p>
            )}
            {item.blockedReason === 'narration_failed' && (
              <p className="status-matrix-blocked">Blocked：最近一次 StoryVoice 音訊產製失敗，可在書籍詳情重新建立。</p>
            )}
          </article>
        ))}
      </div>
    </details>
  )
}
