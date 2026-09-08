import { useEffect, useRef, useState } from 'react'
import { safeSampleUrl, type PublicVoiceCard } from '../publicVoiceCatalog'

export function VoicePreviewButton({ voice }: { voice: PublicVoiceCard }) {
  const audioRef = useRef<HTMLAudioElement>(null)
  const [playback, setPlayback] = useState<'idle' | 'playing' | 'error'>('idle')
  const sampleUrl = safeSampleUrl(voice)
  const descriptionId = `voice-demo-${voice.alias}`

  useEffect(() => {
    const audio = audioRef.current
    return () => {
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
      audio.pause()
      return
    }

    setPlayback('idle')
    try {
      await audio.play()
    } catch {
      setPlayback('error')
    }
  }

  return (
    <div>
      <button
        aria-describedby={descriptionId}
        aria-pressed={playback === 'playing'}
        className="public-catalog-button public-catalog-button-preview public-focus"
        onClick={() => void togglePlayback()}
        type="button"
      >
        <span aria-hidden="true">{playback === 'playing' ? 'Ⅱ' : '▶'}</span>
        {playback === 'playing' ? '暫停固定示範' : '播放固定示範'}
      </button>
      <audio
        onEnded={() => setPlayback('idle')}
        onError={() => setPlayback('error')}
        onPause={() => setPlayback('idle')}
        onPlay={() => setPlayback('playing')}
        preload="none"
        ref={audioRef}
        src={sampleUrl}
      />
      <p className="mt-2 text-xs leading-5 text-stone-500" id={descriptionId}>
        固定公開示範，不會送出或合成你輸入的文字。
      </p>
      {playback === 'error' && <p className="mt-1 text-xs text-rose-700" role="alert">示範音檔暫時無法播放，請稍後再試。</p>}
    </div>
  )
}
