import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const playerSource = readFileSync(new URL('../src/components/AudioPlayer.tsx', import.meta.url), 'utf8')
const narrationPanelSource = readFileSync(new URL('../src/NarrationPanel.tsx', import.meta.url), 'utf8')

test('AudioPlayer component provides explicit playback speed options', () => {
  assert.ok(playerSource.includes('SPEED_OPTIONS = [0.75, 1.0, 1.25, 1.5, 1.75, 2.0]'))
  assert.ok(playerSource.includes('playbackRate === speed'))
  assert.ok(playerSource.includes('changeSpeed'))
  assert.ok(playerSource.includes('audioRef.current.playbackRate = speed'))
})

test('AudioPlayer persists and resumes listening progress safely', () => {
  assert.ok(playerSource.includes('storyvoice.progress.'))
  assert.ok(playerSource.includes('localStorage.getItem'))
  assert.ok(playerSource.includes('localStorage.setItem'))
  assert.ok(playerSource.includes('handleResume'))
  assert.ok(playerSource.includes('persistProgress'))
})

test('AudioPlayer includes seek, skip-10s, volume and mute accessibility controls', () => {
  assert.ok(playerSource.includes('skipSeconds(-10)'))
  assert.ok(playerSource.includes('skipSeconds(10)'))
  assert.ok(playerSource.includes('handleSeekChange'))
  assert.ok(playerSource.includes('handleVolumeChange'))
  assert.ok(playerSource.includes('toggleMute'))
  assert.ok(playerSource.includes('role="region"'))
  assert.ok(playerSource.includes('type="range"'))
})

test('AudioPlayer integrates with NarrationPanel and uses bilingual i18n', () => {
  assert.ok(narrationPanelSource.includes('<AudioPlayer'))
  assert.ok(narrationPanelSource.includes('storageKey={`narration-${job.id}`}') || narrationPanelSource.includes('storageKey='))
  assert.ok(playerSource.includes('useLocale()'))
  assert.ok(playerSource.includes('localize('))
})

test('AudioPlayer renders a chapter list with click-to-seek navigation from the timeline', () => {
  assert.ok(playerSource.includes('timeline.chapters.map'))
  assert.ok(playerSource.includes('seekToMs(chapter.startMs)'))
  assert.ok(playerSource.includes("localize(locale, '章節列表', 'Chapter list')"))
  assert.ok(playerSource.includes('goToPreviousChapter'))
  assert.ok(playerSource.includes('goToNextChapter'))
  assert.ok(playerSource.includes("aria-current={index === currentChapterIndex ? 'true' : undefined}"))
})

test('AudioPlayer highlights the current sentence and speaking character during playback', () => {
  assert.ok(playerSource.includes('findTimelineIndex'))
  assert.ok(playerSource.includes('currentTurn'))
  assert.ok(playerSource.includes('speakerLabel(currentTurn)'))
  assert.ok(playerSource.includes('currentTurn?.text'))
  assert.ok(playerSource.includes("localize(locale, '旁白', 'Narrator')"))
  assert.ok(playerSource.includes("localize(locale, '內心獨白', 'Inner monologue')"))
})

test('NarrationPanel fetches the playback timeline for completed jobs without breaking playback on 404', () => {
  assert.ok(narrationPanelSource.includes('CompletedNarrationPlayer'))
  assert.ok(narrationPanelSource.includes('/timeline'))
  assert.ok(narrationPanelSource.includes('if (!response.ok) return'))
  assert.ok(narrationPanelSource.includes('timeline={timeline}'))
})
