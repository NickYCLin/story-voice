import { useEffect, useRef, useState } from 'react'
import { safeSampleUrl, type PublicVoiceCard } from '../publicVoiceCatalog'

export function VoicePreviewButton({ voice }: { voice: PublicVoiceCard }) {
  const sampleUrl = safeSampleUrl(voice)
  return <VoicePreviewSession key={sampleUrl ?? voice.alias} alias={voice.alias} sampleUrl={sampleUrl} />
}

function VoicePreviewSession({ alias, sampleUrl }: { alias: string; sampleUrl: string | null }) {
  const audioRef = useRef<HTMLAudioElement>(null)
  const playRequest = useRef<object | null>(null)
  const [playback, setPlayback] = useState<'idle' | 'waiting' | 'playing' | 'error'>('idle')
  const descriptionId = `voice-demo-${alias}`
  const active = playback === 'playing' || playback === 'waiting'

  useEffect(() => {
    const audio = audioRef.current
    return () => {
      playRequest.current = null
      audio?.pause()
      if (audio) audio.currentTime = 0
    }
  }, [sampleUrl])

  if (!sampleUrl) {
    return (
      <div>
        <button className="public-catalog-button public-catalog-button-muted" disabled type="button">
          固定示範尚未開放
        </button>
        <p className="mt-2 text-xs leading-5 text-stone-500">此聲線尚未通過公開試聽授權檢查。</p>
      </div>
    )
  }

  async function togglePlayback() {
    const audio = audioRef.current
    if (!audio) return

    if (!audio.paused) {
      playRequest.current = null
      audio.pause()
      return
    }

    if (playRequest.current) return
    const request = {}
    playRequest.current = request
    setPlayback('idle')
    try {
      if (audio.error) audio.load()
      await audio.play()
    } catch (error) {
      if (playRequest.current !== request) return
      if (error instanceof DOMException && error.name === 'AbortError') {
        setPlayback(audio.paused ? 'idle' : 'playing')
        return
      }
      setPlayback('error')
    } finally {
      if (playRequest.current === request) playRequest.current = null
    }
  }

  return (
    <div>
      <button
        aria-describedby={descriptionId}
        aria-pressed={active}
        className="public-catalog-button public-catalog-button-preview public-focus"
        onClick={() => void togglePlayback()}
        type="button"
      >
        <span aria-hidden="true">{active ? 'Ⅱ' : '▶'}</span>
        {active ? '暫停固定示範' : '播放固定示範'}
      </button>
      <audio
        onEnded={() => { playRequest.current = null; setPlayback('idle') }}
        onError={() => { playRequest.current = null; setPlayback('error') }}
        onPause={(event) => {
          if (event.currentTarget.paused) { playRequest.current = null; setPlayback('idle') }
        }}
        onPlay={(event) => setPlayback(event.currentTarget.paused ? 'idle' : 'playing')}
        onPlaying={(event) => setPlayback(event.currentTarget.paused ? 'idle' : 'playing')}
        onWaiting={(event) => {
          if (!event.currentTarget.paused && !event.currentTarget.ended) setPlayback('waiting')
        }}
        preload="none"
        ref={audioRef}
        src={sampleUrl}
      />
      <p className="mt-2 text-xs leading-5 text-stone-500" id={descriptionId}>
        固定公開示範，不會送出或合成你輸入的文字。
      </p>
      {playback === 'waiting' && <p className="mt-1 text-xs text-stone-500" role="status">示範音檔載入中…</p>}
      {playback === 'error' && <p className="mt-1 text-xs text-rose-700" role="alert">示範音檔暫時無法播放，請稍後再試。</p>}
    </div>
  )
}
