import {
  useCallback,
  useEffect,
  useRef,
  useState,
} from 'react'

import { localize, useLocale } from '../i18n'

type AudioPlayerProps = {
  src: string
  title?: string
  storageKey?: string
  hasPrevious?: boolean
  hasNext?: boolean
  onPrevious?: () => void
  onNext?: () => void
  className?: string
}

const SPEED_OPTIONS = [0.75, 1.0, 1.25, 1.5, 1.75, 2.0]

function formatTime(seconds: number): string {
  if (isNaN(seconds) || !isFinite(seconds) || seconds < 0) return '00:00'
  const mins = Math.floor(seconds / 60)
  const secs = Math.floor(seconds % 60)
  const hours = Math.floor(mins / 60)
  const remMins = mins % 60
  if (hours > 0) {
    return `${hours}:${String(remMins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`
  }
  return `${String(remMins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`
}

export function AudioPlayer({
  src,
  title,
  storageKey,
  hasPrevious = false,
  hasNext = false,
  onPrevious,
  onNext,
  className = '',
}: AudioPlayerProps) {
  const { locale } = useLocale()
  const audioRef = useRef<HTMLAudioElement | null>(null)

  const [isPlaying, setIsPlaying] = useState(false)
  const [currentTime, setCurrentTime] = useState(0)
  const [duration, setDuration] = useState(0)
  const [playbackRate, setPlaybackRate] = useState(1.0)
  const [volume, setVolume] = useState(1.0)
  const [isMuted, setIsMuted] = useState(false)
  const [savedResumeTime, setSavedResumeTime] = useState<number | null>(null)
  const [isSeeking, setIsSeeking] = useState(false)
  const [seekValue, setSeekValue] = useState(0)

  const effectiveStorageKey = storageKey ? `storyvoice.progress.${storageKey}` : null

  // Restore saved playback position if available
  useEffect(() => {
    if (!effectiveStorageKey || typeof window === 'undefined') return
    try {
      const raw = window.localStorage.getItem(effectiveStorageKey)
      if (raw) {
        const parsed = JSON.parse(raw) as { time: number; duration: number; savedAt: number }
        if (parsed.time > 3 && (!parsed.duration || parsed.time < parsed.duration - 5)) {
          setSavedResumeTime(parsed.time)
        }
      }
    } catch {
      // Storage unavailable
    }
  }, [effectiveStorageKey])

  // Save playback position periodically
  const persistProgress = useCallback((time: number, dur: number) => {
    if (!effectiveStorageKey || typeof window === 'undefined') return
    try {
      if (time > 2 && dur > 0 && time < dur - 2) {
        window.localStorage.setItem(
          effectiveStorageKey,
          JSON.stringify({ time: Math.floor(time), duration: Math.floor(dur), savedAt: Date.now() }),
        )
      } else if (time >= dur - 2 && dur > 0) {
        window.localStorage.removeItem(effectiveStorageKey)
      }
    } catch {
      // Storage unavailable
    }
  }, [effectiveStorageKey])

  const togglePlay = () => {
    const audio = audioRef.current
    if (!audio) return
    if (audio.paused) {
      void audio.play()
    } else {
      audio.pause()
    }
  }

  const handleResume = () => {
    const audio = audioRef.current
    if (!audio || savedResumeTime === null) return
    audio.currentTime = savedResumeTime
    setCurrentTime(savedResumeTime)
    setSavedResumeTime(null)
    void audio.play()
  }

  const changeSpeed = (speed: number) => {
    setPlaybackRate(speed)
    if (audioRef.current) {
      audioRef.current.playbackRate = speed
    }
  }

  const skipSeconds = (delta: number) => {
    const audio = audioRef.current
    if (!audio) return
    const target = Math.max(0, Math.min(duration || 0, audio.currentTime + delta))
    audio.currentTime = target
    setCurrentTime(target)
  }

  const handleSeekChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const val = Number(e.target.value)
    setSeekValue(val)
    if (!isSeeking) setIsSeeking(true)
  }

  const handleSeekEnd = () => {
    if (audioRef.current) {
      audioRef.current.currentTime = seekValue
      setCurrentTime(seekValue)
    }
    setIsSeeking(false)
  }

  const handleVolumeChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const val = Number(e.target.value)
    setVolume(val)
    setIsMuted(val === 0)
    if (audioRef.current) {
      audioRef.current.volume = val
      audioRef.current.muted = val === 0
    }
  }

  const toggleMute = () => {
    if (audioRef.current) {
      const nextMuted = !isMuted
      setIsMuted(nextMuted)
      audioRef.current.muted = nextMuted
    }
  }

  return (
    <div
      aria-label={localize(locale, '有聲書播放器', 'Audiobook Player')}
      className={`rounded-2xl border border-stone-200 bg-stone-900 p-4 text-stone-100 shadow-md ${className}`}
      role="region"
    >
      <audio
        className="sr-only"
        controls
        onDurationChange={(e) => setDuration(e.currentTarget.duration)}
        onEnded={() => {
          setIsPlaying(false)
          if (effectiveStorageKey) {
            try { window.localStorage.removeItem(effectiveStorageKey) } catch { /* ignore */ }
          }
          if (hasNext && onNext) onNext()
        }}
        onLoadedMetadata={(e) => {
          setDuration(e.currentTarget.duration)
          e.currentTarget.playbackRate = playbackRate
        }}
        onPause={() => setIsPlaying(false)}
        onPlay={() => setIsPlaying(true)}
        onTimeUpdate={(e) => {
          if (!isSeeking) {
            const cur = e.currentTarget.currentTime
            setCurrentTime(cur)
            persistProgress(cur, e.currentTarget.duration)
          }
        }}
        preload="metadata"
        ref={audioRef}
        src={src}
      >
        {localize(locale, '你的瀏覽器不支援音訊播放。', 'Your browser does not support audio playback.')}
      </audio>

      {/* Header / Title & Resume Notice */}
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-stone-800 pb-3">
        <div className="min-w-0 flex-1">
          {title && <p className="truncate text-sm font-medium text-stone-200">{title}</p>}
          <span className="text-xs text-stone-400">
            {localize(locale, '語音播放中', 'Playback Active')}
          </span>
        </div>

        {savedResumeTime !== null && (
          <button
            className="flex items-center gap-1.5 rounded-full border border-amber-500/40 bg-amber-500/10 px-3 py-1 text-xs text-amber-300 transition hover:bg-amber-500/20"
            onClick={handleResume}
            title={localize(locale, '點擊接續上次播放進度', 'Click to resume from last saved position')}
            type="button"
          >
            <span>↺</span>
            <span>
              {localize(
                locale,
                `從上次 ${formatTime(savedResumeTime)} 續播`,
                `Resume from ${formatTime(savedResumeTime)}`,
              )}
            </span>
          </button>
        )}
      </div>

      {/* Progress & Time Slider */}
      <div className="mt-3 space-y-1">
        <div className="flex items-center gap-3">
          <span className="w-12 text-right font-mono text-xs text-stone-400">
            {formatTime(isSeeking ? seekValue : currentTime)}
          </span>
          <input
            aria-label={localize(locale, '播放進度', 'Playback progress')}
            aria-valuemax={Math.floor(duration)}
            aria-valuemin={0}
            aria-valuenow={Math.floor(isSeeking ? seekValue : currentTime)}
            className="h-2 flex-1 cursor-pointer appearance-none rounded-full bg-stone-700 accent-amber-500 hover:bg-stone-600 focus:outline-none"
            max={duration || 100}
            min={0}
            onChange={handleSeekChange}
            onMouseUp={handleSeekEnd}
            onTouchEnd={handleSeekEnd}
            step={0.1}
            type="range"
            value={isSeeking ? seekValue : currentTime}
          />
          <span className="w-12 text-left font-mono text-xs text-stone-400">
            {formatTime(duration)}
          </span>
        </div>
      </div>

      {/* Controls: Prev/Next, Play/Pause, Rewind/Forward, Speed, Volume */}
      <div className="mt-4 flex flex-wrap items-center justify-between gap-3 pt-1">
        {/* Main playback buttons */}
        <div className="flex items-center gap-2">
          {hasPrevious && onPrevious && (
            <button
              aria-label={localize(locale, '上一章', 'Previous chapter')}
              className="rounded-full p-2 text-stone-300 hover:bg-stone-800 hover:text-white"
              onClick={onPrevious}
              type="button"
            >
              ⏮
            </button>
          )}

          <button
            aria-label={localize(locale, '倒轉 10 秒', 'Rewind 10 seconds')}
            className="rounded-full p-2 text-xs text-stone-300 hover:bg-stone-800 hover:text-white"
            onClick={() => skipSeconds(-10)}
            type="button"
          >
            -10s
          </button>

          <button
            aria-label={isPlaying ? localize(locale, '暫停', 'Pause') : localize(locale, '播放', 'Play')}
            className="flex h-10 w-10 items-center justify-center rounded-full bg-amber-500 font-bold text-stone-950 transition hover:bg-amber-400 active:scale-95"
            onClick={togglePlay}
            type="button"
          >
            {isPlaying ? '⏸' : '▶'}
          </button>

          <button
            aria-label={localize(locale, '快轉 10 秒', 'Forward 10 seconds')}
            className="rounded-full p-2 text-xs text-stone-300 hover:bg-stone-800 hover:text-white"
            onClick={() => skipSeconds(10)}
            type="button"
          >
            +10s
          </button>

          {hasNext && onNext && (
            <button
              aria-label={localize(locale, '下一章', 'Next chapter')}
              className="rounded-full p-2 text-stone-300 hover:bg-stone-800 hover:text-white"
              onClick={onNext}
              type="button"
            >
              ⏭
            </button>
          )}
        </div>

        {/* Speed Controls */}
        <div className="flex items-center gap-1">
          <span className="mr-1 text-xs text-stone-400">
            {localize(locale, '倍速', 'Speed')}:
          </span>
          <div className="inline-flex rounded-lg bg-stone-800 p-0.5" role="group">
            {SPEED_OPTIONS.map((speed) => (
              <button
                aria-pressed={playbackRate === speed}
                className={`rounded px-2 py-1 text-xs font-mono font-medium transition ${
                  playbackRate === speed
                    ? 'bg-amber-500 font-bold text-stone-950'
                    : 'text-stone-300 hover:bg-stone-700 hover:text-white'
                }`}
                key={speed}
                onClick={() => changeSpeed(speed)}
                type="button"
              >
                {speed}x
              </button>
            ))}
          </div>
        </div>

        {/* Volume Control */}
        <div className="flex items-center gap-2">
          <button
            aria-label={isMuted ? localize(locale, '取消靜音', 'Unmute') : localize(locale, '靜音', 'Mute')}
            className="text-stone-300 hover:text-white"
            onClick={toggleMute}
            type="button"
          >
            {isMuted || volume === 0 ? '🔇' : '🔊'}
          </button>
          <input
            aria-label={localize(locale, '音量', 'Volume')}
            className="h-1.5 w-16 cursor-pointer appearance-none rounded-full bg-stone-700 accent-amber-500"
            max={1}
            min={0}
            onChange={handleVolumeChange}
            step={0.05}
            type="range"
            value={isMuted ? 0 : volume}
          />
        </div>
      </div>
    </div>
  )
}
