import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react'

import { localize, useLocale } from '../i18n'
import type { ListeningProgress } from '../useListeningProgress'

export type NarrationTimelineChapter = {
  chapterId: string
  sortOrder: number
  title: string
  startMs: number
}

export type NarrationTimelineTurn = {
  index: number
  startMs: number
  durationMs: number
  chapterSortOrder: number
  kind: 'narrator' | 'dialogue' | 'innerMonologue' | 'mixed'
  characterId: string | null
  characterName: string | null
  text: string | null
}

export type NarrationTimeline = {
  jobId: string
  textAvailable: boolean
  chapters: NarrationTimelineChapter[]
  turns: NarrationTimelineTurn[]
}

type AudioPlayerProps = {
  src: string
  title?: string
  storageKey?: string
  hasPrevious?: boolean
  hasNext?: boolean
  onPrevious?: () => void
  onNext?: () => void
  timeline?: NarrationTimeline | null
  className?: string
  serverProgress?: ListeningProgress
  onSaveProgress?: (positionMs: number, durationMs: number) => void
}

/** Last turn whose startMs is at or before the playhead; -1 before the first turn starts. */
function findTimelineIndex(startsMs: number[], positionMs: number): number {
  let low = 0
  let high = startsMs.length - 1
  let found = -1
  while (low <= high) {
    const mid = (low + high) >> 1
    if (startsMs[mid] <= positionMs) {
      found = mid
      low = mid + 1
    } else {
      high = mid - 1
    }
  }
  return found
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

export function AudioPlayer(props: AudioPlayerProps) {
  return <AudioPlayerSession key={JSON.stringify([props.src, props.storageKey])} {...props} />
}

function AudioPlayerSession({
  src,
  title,
  storageKey,
  hasPrevious = false,
  hasNext = false,
  onPrevious,
  onNext,
  timeline = null,
  className = '',
  serverProgress,
  onSaveProgress,
}: AudioPlayerProps) {
  const { locale } = useLocale()
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const [showChapterList, setShowChapterList] = useState(false)

  const [isPlaying, setIsPlaying] = useState(false)
  const [currentTime, setCurrentTime] = useState(0)
  const [duration, setDuration] = useState(0)
  const [playbackRate, setPlaybackRate] = useState(1.0)
  const [volume, setVolume] = useState(1.0)
  const [isMuted, setIsMuted] = useState(false)
  const [savedResumeTime, setSavedResumeTime] = useState<number | null>(null)
  const [playbackError, setPlaybackError] = useState(false)
  const lastPersistedAt = useRef(0)
  const progressChanged = useRef(false)
  const saveToServer = useRef(onSaveProgress)
  useEffect(() => { saveToServer.current = onSaveProgress }, [onSaveProgress])

  const effectiveStorageKey = storageKey ? `storyvoice.progress.${storageKey}` : null

  const positionMs = currentTime * 1000
  const turnStarts = useMemo(
    () => timeline?.turns.map((turn) => turn.startMs) ?? [],
    [timeline],
  )
  const chapterStarts = useMemo(
    () => timeline?.chapters.map((chapter) => chapter.startMs) ?? [],
    [timeline],
  )
  const currentTurn = useMemo(() => {
    if (!timeline || timeline.turns.length === 0) return null
    const index = findTimelineIndex(turnStarts, positionMs)
    return index >= 0 ? timeline.turns[index] : null
  }, [timeline, turnStarts, positionMs])
  const currentChapterIndex = useMemo(() => {
    if (!timeline || timeline.chapters.length === 0) return -1
    return Math.max(0, findTimelineIndex(chapterStarts, positionMs))
  }, [timeline, chapterStarts, positionMs])
  const currentChapter = currentChapterIndex >= 0 ? timeline?.chapters[currentChapterIndex] ?? null : null
  const hasChapterNav = (timeline?.chapters.length ?? 0) > 1

  const goToPreviousChapter = () => {
    if (!timeline || currentChapterIndex < 0) return
    const chapterStartMs = timeline.chapters[currentChapterIndex].startMs
    // More than 3 seconds into the chapter restarts it; otherwise jump one chapter back.
    if (positionMs - chapterStartMs > 3_000 || currentChapterIndex === 0) {
      seekToMs(chapterStartMs)
    } else {
      seekToMs(timeline.chapters[currentChapterIndex - 1].startMs)
    }
  }

  const goToNextChapter = () => {
    if (!timeline || currentChapterIndex >= timeline.chapters.length - 1) return
    seekToMs(timeline.chapters[currentChapterIndex + 1].startMs)
  }

  const speakerLabel = (turn: NarrationTimelineTurn): string => {
    if (turn.kind === 'narrator') return localize(locale, '旁白', 'Narrator')
    if (turn.kind === 'mixed') return localize(locale, '旁白與對白', 'Narration & dialogue')
    const name = turn.characterName
      ?? localize(locale, '角色', 'Character')
    return turn.kind === 'innerMonologue'
      ? `${name} · ${localize(locale, '內心獨白', 'Inner monologue')}`
      : name
  }

  // Restore saved playback position if available
  useEffect(() => {
    if (!effectiveStorageKey || typeof window === 'undefined') return
    try {
      const raw = window.localStorage.getItem(effectiveStorageKey)
      if (raw) {
        const parsed = JSON.parse(raw) as { time: number; duration: number; savedAt: number }
        if (Number.isFinite(parsed.time) && parsed.time > 3
          && (!parsed.duration || (Number.isFinite(parsed.duration) && parsed.time < parsed.duration - 5))) {
          setSavedResumeTime(parsed.time)
        }
      }
    } catch {
      // Storage unavailable
    }
  }, [effectiveStorageKey])

  useEffect(() => {
    // A late read may offer a resume position, but never move a player already in use.
    // A server record at the beginning/end also overrides an old local resume marker.
    if (!serverProgress?.version || progressChanged.current) return
    const time = serverProgress.positionMs / 1000
    const dur = serverProgress.durationMs / 1000
    const actualDuration = audioRef.current?.duration
    const withinAudio = !Number.isFinite(actualDuration) || time < actualDuration! - 5
    setSavedResumeTime(time > 3 && time < dur - 5 && withinAudio ? time : null)
  }, [serverProgress])

  // Save playback position periodically
  const persistProgress = useCallback((time: number, dur: number) => {
    if (!effectiveStorageKey || typeof window === 'undefined') return
    if (!progressChanged.current) return
    if (!Number.isFinite(time) || !Number.isFinite(dur) || dur <= 0) return
    saveToServer.current?.(Math.round(Math.max(0, Math.min(time, dur)) * 1000), Math.round(dur * 1000))
    try {
      if (time > 2 && dur > 0 && time < dur - 2) {
        window.localStorage.setItem(
          effectiveStorageKey,
          JSON.stringify({ time: Math.floor(time), duration: Math.floor(dur), savedAt: Date.now() }),
        )
      } else {
        window.localStorage.removeItem(effectiveStorageKey)
      }
    } catch {
      // Storage unavailable
    }
  }, [effectiveStorageKey])

  useEffect(() => {
    const audio = audioRef.current
    if (!audio) return
    const save = () => persistProgress(audio.currentTime, audio.duration)
    const saveWhenHidden = () => { if (document.visibilityState === 'hidden') save() }
    window.addEventListener('pagehide', save)
    document.addEventListener('visibilitychange', saveWhenHidden)
    return () => {
      save()
      audio.pause()
      window.removeEventListener('pagehide', save)
      document.removeEventListener('visibilitychange', saveWhenHidden)
    }
  }, [persistProgress])

  const seekToMs = (targetMs: number) => {
    const audio = audioRef.current
    if (!audio || !Number.isFinite(targetMs) || duration <= 0) return
    const target = Math.max(0, Math.min(duration, targetMs / 1000))
    progressChanged.current = true
    audio.currentTime = target
    setCurrentTime(target)
    setSavedResumeTime(null)
    persistProgress(target, duration)
  }

  const playAudio = async () => {
    const audio = audioRef.current
    if (!audio) return
    setPlaybackError(false)
    try {
      if (audio.error) audio.load()
      await audio.play()
    } catch (error) {
      if (audioRef.current !== audio || (error instanceof DOMException && error.name === 'AbortError')) return
      setIsPlaying(false)
      setPlaybackError(true)
    }
  }

  const togglePlay = () => {
    const audio = audioRef.current
    if (!audio) return
    if (audio.paused) {
      setSavedResumeTime(null)
      void playAudio()
    } else {
      audio.pause()
    }
  }

  const handleResume = () => {
    const audio = audioRef.current
    if (!audio || savedResumeTime === null) return
    seekToMs(savedResumeTime * 1000)
    void playAudio()
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
    seekToMs((audio.currentTime + delta) * 1000)
  }

  const handleSeekChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    seekToMs(Number(e.currentTarget.value) * 1000)
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
      const nextMuted = !(isMuted || volume === 0)
      setIsMuted(nextMuted)
      audioRef.current.muted = nextMuted
      if (!nextMuted && volume === 0) {
        setVolume(1)
        audioRef.current.volume = 1
      }
    }
  }

  return (
    <div
      aria-label={localize(locale, '有聲書播放器', 'Audiobook Player')}
      className={`audio-player min-w-0 rounded-2xl border border-stone-700 bg-stone-900 p-4 text-stone-100 shadow-md sm:p-5 ${className}`}
      role="region"
    >
      <audio
        className="sr-only"
        tabIndex={-1}
        onDurationChange={(e) => setDuration(Number.isFinite(e.currentTarget.duration) ? Math.max(0, e.currentTarget.duration) : 0)}
        onEnded={(e) => {
          setIsPlaying(false)
          setSavedResumeTime(null)
          progressChanged.current = true
          persistProgress(e.currentTarget.duration, e.currentTarget.duration)
          if (effectiveStorageKey) {
            try { window.localStorage.removeItem(effectiveStorageKey) } catch { /* ignore */ }
          }
          if (hasNext && onNext) onNext()
        }}
        onLoadedMetadata={(e) => {
          const nextDuration = Number.isFinite(e.currentTarget.duration) ? Math.max(0, e.currentTarget.duration) : 0
          setDuration(nextDuration)
          setSavedResumeTime((saved) => saved !== null && saved < nextDuration - 5 ? saved : null)
          e.currentTarget.playbackRate = playbackRate
        }}
        onError={() => { setIsPlaying(false); setPlaybackError(true) }}
        onPause={(e) => {
          setIsPlaying(false)
          persistProgress(e.currentTarget.currentTime, e.currentTarget.duration)
        }}
        onPlay={() => { progressChanged.current = true; setIsPlaying(true) }}
        onTimeUpdate={(e) => {
          const cur = e.currentTarget.currentTime
          setCurrentTime(cur)
          if (Date.now() - lastPersistedAt.current >= 5000) {
            lastPersistedAt.current = Date.now()
            persistProgress(cur, e.currentTarget.duration)
          }
        }}
        preload="metadata"
        ref={audioRef}
        src={src}
      >
        {localize(locale, '你的瀏覽器不支援音訊播放。', 'Your browser does not support audio playback.')}
      </audio>

      {playbackError && (
        <p className="mb-3 rounded-xl border border-rose-400/30 bg-rose-400/10 px-3 py-2 text-sm leading-6 text-rose-200" role="alert">
          {localize(locale, '無法播放這段音訊。請檢查連線後再按播放重試。', 'Unable to play this audio. Check your connection and press play to try again.')}
        </p>
      )}

      {/* Header / Title & Resume Notice */}
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-stone-800 pb-3">
        <div className="min-w-0 grow basis-40">
          {title && <p className="truncate text-sm font-medium text-stone-200">{title}</p>}
          <span className="text-xs text-stone-400">
            {playbackError
              ? localize(locale, '播放失敗', 'Playback failed')
              : isPlaying
                ? localize(locale, '正在播放', 'Playing')
                : localize(locale, '按播放開始聆聽', 'Press play to listen')}
          </span>
        </div>

        {savedResumeTime !== null && (
          <button
            className="flex items-center gap-1.5 rounded-full border border-amber-500/40 bg-amber-500/10 px-3 py-1 text-xs text-amber-300 transition hover:bg-amber-500/20"
            onClick={handleResume}
            disabled={duration <= 0}
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

      {/* Now Playing: current chapter, character and sentence synced to the playhead */}
      {timeline && (currentChapter || currentTurn) && (
        <div className="mt-3 rounded-xl bg-stone-800/60 p-3">
          <div className="flex flex-wrap items-center gap-2 text-xs">
            {currentChapter && (
              <span className="rounded-full bg-stone-700 px-2.5 py-0.5 text-stone-300">
                {currentChapter.title
                  || localize(locale, `第 ${currentChapter.sortOrder + 1} 章`, `Chapter ${currentChapter.sortOrder + 1}`)}
              </span>
            )}
            {currentTurn && (
              <span className="rounded-full border border-amber-500/40 bg-amber-500/10 px-2.5 py-0.5 font-medium text-amber-300">
                {speakerLabel(currentTurn)}
              </span>
            )}
          </div>
          {currentTurn?.text && (
            <p className="mt-2 max-h-24 overflow-y-auto text-sm leading-6 text-stone-200">
              {currentTurn.text}
            </p>
          )}
        </div>
      )}

      {/* Progress & Time Slider */}
      <div className="mt-3 space-y-1">
        <div className="flex items-center gap-3">
          <span className="w-12 text-right font-mono text-xs text-stone-400">
            {formatTime(currentTime)}
          </span>
          <input
            aria-label={localize(locale, '播放進度', 'Playback progress')}
            aria-valuemax={Math.floor(duration)}
            aria-valuemin={0}
            aria-valuenow={Math.floor(currentTime)}
            aria-valuetext={`${formatTime(currentTime)} / ${formatTime(duration)}`}
            className="h-2 min-w-0 flex-1 cursor-pointer appearance-none rounded-full bg-stone-700 accent-amber-500 hover:bg-stone-600 disabled:cursor-not-allowed disabled:opacity-40"
            disabled={duration <= 0}
            max={duration || 100}
            min={0}
            onChange={handleSeekChange}
            step={0.1}
            type="range"
            value={currentTime}
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
          {((hasPrevious && onPrevious) || hasChapterNav) && (
            <button
              aria-label={localize(locale, '上一章', 'Previous chapter')}
              className="rounded-full p-2 text-stone-300 hover:bg-stone-800 hover:text-white"
              onClick={onPrevious ?? goToPreviousChapter}
              disabled={!onPrevious && duration <= 0}
              type="button"
            >
              ⏮
            </button>
          )}

          <button
            aria-label={localize(locale, '倒轉 10 秒', 'Rewind 10 seconds')}
            className="rounded-full p-2 text-xs text-stone-300 hover:bg-stone-800 hover:text-white"
            onClick={() => skipSeconds(-10)}
            disabled={duration <= 0}
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
            disabled={duration <= 0}
            type="button"
          >
            +10s
          </button>

          {((hasNext && onNext) || hasChapterNav) && (
            <button
              aria-label={localize(locale, '下一章', 'Next chapter')}
              className="rounded-full p-2 text-stone-300 hover:bg-stone-800 hover:text-white disabled:cursor-not-allowed disabled:opacity-40"
              disabled={!onNext && (duration <= 0 || (!!timeline && currentChapterIndex >= timeline.chapters.length - 1))}
              onClick={onNext ?? goToNextChapter}
              type="button"
            >
              ⏭
            </button>
          )}
        </div>

        {/* Speed Controls */}
        <div className="flex items-center gap-1">
          <span className="mr-1 shrink-0 text-xs text-stone-400">
            {localize(locale, '倍速', 'Speed')}:
          </span>
          <div aria-label={localize(locale, '播放倍速', 'Playback speed')} className="inline-flex flex-wrap rounded-lg bg-stone-800 p-0.5" role="group">
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
            aria-label={isMuted || volume === 0 ? localize(locale, '取消靜音', 'Unmute') : localize(locale, '靜音', 'Mute')}
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

      {/* Chapter List navigation */}
      {timeline && timeline.chapters.length > 0 && (
        <div className="mt-3 border-t border-stone-800 pt-3">
          <button
            aria-expanded={showChapterList}
            className="flex items-center gap-1.5 rounded-full px-3 py-1 text-xs text-stone-300 transition hover:bg-stone-800 hover:text-white"
            onClick={() => setShowChapterList((current) => !current)}
            type="button"
          >
            <span>{showChapterList ? '▾' : '▸'}</span>
            <span>{localize(locale, '章節列表', 'Chapter list')}（{timeline.chapters.length}）</span>
          </button>
          {showChapterList && (
            <ol
              aria-label={localize(locale, '章節列表', 'Chapter list')}
              className="mt-2 max-h-48 space-y-1 overflow-y-auto pr-1"
            >
              {timeline.chapters.map((chapter, index) => (
                <li key={`${chapter.chapterId}-${index}`}>
                  <button
                    aria-current={index === currentChapterIndex ? 'true' : undefined}
                    className={`flex w-full items-center justify-between gap-3 rounded-lg px-3 py-1.5 text-left text-xs transition ${
                      index === currentChapterIndex
                        ? 'bg-amber-500/15 text-amber-300'
                        : 'text-stone-300 hover:bg-stone-800 hover:text-white'
                    }`}
                    onClick={() => seekToMs(chapter.startMs)}
                    disabled={duration <= 0}
                    type="button"
                  >
                    <span className="min-w-0 flex-1 truncate">
                      {chapter.title
                        || localize(locale, `第 ${chapter.sortOrder + 1} 章`, `Chapter ${chapter.sortOrder + 1}`)}
                    </span>
                    <span className="shrink-0 font-mono text-stone-500">
                      {formatTime(chapter.startMs / 1000)}
                    </span>
                  </button>
                </li>
              ))}
            </ol>
          )}
        </div>
      )}
    </div>
  )
}
