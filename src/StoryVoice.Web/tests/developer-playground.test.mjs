import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8')
const page = readFileSync(new URL('../src/pages/DeveloperPlaygroundPage.tsx', import.meta.url), 'utf8')
const shared = readFileSync(new URL('../src/developerVoiceConsole.ts', import.meta.url), 'utf8')
const consolePage = readFileSync(new URL('../src/pages/DeveloperConsolePage.tsx', import.meta.url), 'utf8')
const projectPage = readFileSync(new URL('../src/pages/DeveloperProjectPage.tsx', import.meta.url), 'utf8')
const endpoint = readFileSync(new URL('../../StoryVoice.Api/DeveloperConsoleEndpoints.cs', import.meta.url), 'utf8')
const service = readFileSync(new URL('../../StoryVoice.Infrastructure/ExternalVoices/DeveloperVoicePlaygroundService.cs', import.meta.url), 'utf8')

test('developer playground is routed and linked from owner project surfaces', () => {
  assert.ok(app.includes('<Route element={<DeveloperPlaygroundPage />} path="developer/playground" />'))
  assert.match(consolePage, /to="\/developer\/playground"/)
  assert.match(consolePage, /\/developer\/playground\?project=/)
  assert.match(projectPage, /\/developer\/playground\?project=/)
})

test('playground sends only same-origin session data with CSRF and no external bearer', () => {
  assert.match(shared, /\/api\/developer\/external-voice\/playground/)
  assert.match(shared, /credentials: 'same-origin'/)
  assert.match(shared, /'X-CSRF-TOKEN': csrfToken/)
  assert.doesNotMatch(shared, /Authorization/)
  assert.doesNotMatch(page, /localStorage|sessionStorage/)
  assert.match(page, /瀏覽器不會取得、保存或傳送 external bearer/)
  assert.match(endpoint, /RequireAuthorization\(StoryVoicePolicies\.UserSession\)/)
  assert.match(endpoint, /AddEndpointFilter<AntiforgeryEndpointFilter>/)
  assert.match(service, /IExternalVoiceRequestRateLimiter rateLimiter/)
  assert.match(service, /rateLimiter\.TryAcquire/)
  assert.match(service, /ExternalVoiceSynthesisFailureKind\.RateLimited/)
})

test('playground exposes generation, cancellation, playback, download and safe metadata', () => {
  assert.match(page, /產生語音/)
  assert.match(page, /取消/)
  assert.match(page, /<audio/)
  assert.match(page, /下載 WAV/)
  assert.match(page, /用相同冪等鍵重送/)
  assert.match(page, /Request ID/)
  assert.match(page, /Idempotency key/)
  assert.match(page, /UTF-8 bytes/)
  assert.match(page, /401、404、409、429、503/)
  assert.match(page, /Playground 與同一 consumer 的 external API 共用這份額度/)
  assert.match(shared, /X-StoryVoice-Request-Id/)
  assert.match(shared, /X-StoryVoice-Latency-Ms/)
  assert.match(shared, /X-StoryVoice-Audio-Duration-Ms/)
  assert.match(page, /URL\.createObjectURL/)
  assert.match(page, /URL\.revokeObjectURL/)
})

test('playground result cleanup cannot abort a replacement request', () => {
  const audioCleanupStart = page.indexOf('if (!audioUrl) return')
  const audioCleanupEnd = page.indexOf('const selectedProject', audioCleanupStart)
  const audioCleanup = page.slice(audioCleanupStart, audioCleanupEnd)

  assert.notEqual(audioCleanupStart, -1)
  assert.ok(audioCleanupEnd > audioCleanupStart)
  assert.match(audioCleanup, /URL\.revokeObjectURL\(audioUrl\)/)
  assert.doesNotMatch(audioCleanup, /\.abort\(\)/)
  assert.match(page, /useEffect\(\(\) => \(\) => \{[\s\S]*controllerRef\.current\?\.abort\(\)[\s\S]*\}, \[\]\)/)
})

test('playground ignores stale responses and freezes request inputs while generating', () => {
  assert.match(page, /requestSequenceRef\.current !== requestSequence/)
  assert.match(page, /controller\.signal\.aborted/)
  assert.match(page, /function invalidatePendingGeneration\(\)[\s\S]*controllerRef\.current\?\.abort\(\)/)
  assert.ok((page.match(/disabled=\{generateState === 'generating'\}/g) ?? []).length >= 3)
})

test('project query 切換會立即停用舊 overview 與 synthesis 並顯示 transition loading', () => {
  assert.match(page, /const routeTransitioning = loadedRequestedProject !== requestedProject/)
  assert.match(page, /const activeOverview = routeTransitioning \? null : overview/)
  assert.match(page, /useEffect\(\(\) => \{[\s\S]*setLoadState\('loading'\)[\s\S]*setOverview\(null\)/)
  assert.match(page, /requestSequenceRef\.current \+= 1[\s\S]*controllerRef\.current\?\.abort\(\)/)
  assert.match(page, /setProjectId\(''\)[\s\S]*setVoice\(''\)/)
  assert.match(page, /if \(routeTransitioning \|\| loadState !== 'ready' \|\| !activeOverview/)
  assert.match(page, /if \(routeTransitioning \|\| loadState === 'loading'\)/)
  assert.doesNotMatch(page, /\{overview && overview\.projects\.length > 0/)
})

test('playground examples use the same token variable as downloaded env files', () => {
  assert.match(page, /STORYVOICE_VOICE_TOKEN/)
  assert.doesNotMatch(page, /STORYVOICE_TOKEN/)
})

test('playground example tabs expose complete ARIA relationships and keyboard navigation', () => {
  assert.match(page, /role="tablist"/)
  assert.match(page, /aria-controls=\{`playground-example-panel-\$\{tab\}`\}/)
  assert.match(page, /aria-selected=\{exampleTab === tab\}/)
  assert.match(page, /tabIndex=\{exampleTab === tab \? 0 : -1\}/)
  assert.match(page, /role="tabpanel"/)
  assert.match(page, /aria-labelledby=\{`playground-example-tab-\$\{tab\}`\}/)
  assert.match(page, /event\.key === 'ArrowRight'/)
  assert.match(page, /event\.key === 'ArrowLeft'/)
  assert.match(page, /event\.key === 'Home'/)
  assert.match(page, /event\.key === 'End'/)
})
