import { useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent, KeyboardEvent } from 'react'
import { Link, useOutletContext, useSearchParams } from 'react-router-dom'

import type { AuthedOutletContext } from '../authOutletContext'
import {
  DeveloperVoicePlaygroundError,
  fetchDeveloperVoiceOverview,
  synthesizeDeveloperVoicePlayground,
} from '../developerVoiceConsole'
import { localize, useLocale, type SupportedLocale } from '../i18n'
import type {
  DeveloperVoiceConsoleOverview,
  DeveloperVoicePlaygroundAudio,
} from '../developerVoiceConsole'

type LoadState = 'loading' | 'ready' | 'error'
type GenerateState = 'idle' | 'generating' | 'success' | 'cancelled' | 'error'
type ExampleTab = 'curl' | 'javascript' | 'csharp' | 'python'

const OUTCOME_LABEL: Record<string, string> = {
  invalid_request: '請檢查文字、聲線與冪等鍵格式。',
  voice_not_available: '這個專案目前不能使用所選聲線。',
  idempotency_conflict: '這組冪等鍵已用於不同內容，請建立新要求。',
  rate_limited: '已達速率上限，請稍後再試。',
  synthesis_unavailable: '語音服務暫時無法使用，請稍後再試。',
}

const OUTCOME_LABEL_EN: Record<string, string> = {
  invalid_request: 'Check the text, voice, and idempotency-key format.',
  voice_not_available: 'The selected voice is not available to this project.',
  idempotency_conflict: 'This idempotency key is already bound to different input. Create a new request.',
  rate_limited: 'The rate limit was reached. Try again later.',
  synthesis_unavailable: 'Voice synthesis is temporarily unavailable. Try again later.',
}

const outcomeLabel = (code: string, locale: SupportedLocale) =>
  localize(locale, OUTCOME_LABEL[code], OUTCOME_LABEL_EN[code])

const formatBytes = (bytes: number, numberLocale: string) => bytes < 1024
  ? `${bytes.toLocaleString(numberLocale)} B`
  : `${(bytes / 1024).toLocaleString(numberLocale, { maximumFractionDigits: 1 })} KiB`

const createIdempotencyKey = () => `play_${crypto.randomUUID().replaceAll('-', '')}`

const EXAMPLE_TAB_LABEL: Record<ExampleTab, string> = {
  curl: 'curl',
  javascript: 'JavaScript server',
  csharp: 'C#',
  python: 'Python',
}

const EXAMPLE_TABS = Object.keys(EXAMPLE_TAB_LABEL) as ExampleTab[]

const exampleCode = (tab: ExampleTab, voice: string, locale: SupportedLocale) => {
  const selectedVoice = voice || 'your-authorized-voice'
  const sampleText = localize(locale, '請替換成要合成的文字', 'Replace with the text you want to synthesize')
  if (tab === 'curl') {
    return [
      'curl --request POST "https://your-storyvoice-host.example/api/external/v1/speech" \\',
      '  --header "Authorization: Bearer $STORYVOICE_VOICE_TOKEN" \\',
      '  --header "Content-Type: application/json" \\',
      '  --header "Idempotency-Key: $IDEMPOTENCY_KEY" \\',
      `  --data '{"voice":"${selectedVoice}","text":"${sampleText}"}'`,
    ].join('\n')
  }
  if (tab === 'javascript') {
    return [
      "import { writeFile } from 'node:fs/promises'",
      "const response = await fetch('https://your-storyvoice-host.example/api/external/v1/speech', {",
      "  method: 'POST',",
      "  headers: { Authorization: `Bearer ${process.env.STORYVOICE_VOICE_TOKEN}`,",
      "    'Content-Type': 'application/json', 'Idempotency-Key': crypto.randomUUID() },",
      `  body: JSON.stringify({ voice: '${selectedVoice}', text: '${sampleText}' }),`,
      '})',
      "if (!response.ok) throw new Error(`StoryVoice ${response.status}`)",
      "await writeFile('speech.wav', Buffer.from(await response.arrayBuffer()))",
    ].join('\n')
  }
  if (tab === 'csharp') {
    return [
      'using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);',
      'request.Headers.Authorization = new("Bearer", token);',
      'request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));',
      `request.Content = JsonContent.Create(new { voice = "${selectedVoice}", text = "${sampleText}" });`,
      'using var response = await client.SendAsync(request);',
      'response.EnsureSuccessStatusCode();',
      'await File.WriteAllBytesAsync("speech.wav", await response.Content.ReadAsByteArrayAsync());',
    ].join('\n')
  }
  return [
    "response = requests.post(endpoint, headers={",
    "    'Authorization': f'Bearer {os.environ[\"STORYVOICE_VOICE_TOKEN\"]}',",
    "    'Idempotency-Key': str(uuid.uuid4()),",
    `}, json={'voice': '${selectedVoice}', 'text': '${sampleText}'}, timeout=60)`,
    'response.raise_for_status()',
    "Path('speech.wav').write_bytes(response.content)",
  ].join('\n')
}

export function DeveloperPlaygroundPage() {
  const { locale, numberLocale } = useLocale()
  const t = (zh: string, en: string) => localize(locale, zh, en)
  const { csrfToken } = useOutletContext<AuthedOutletContext>()
  const [searchParams] = useSearchParams()
  const requestedProject = searchParams.get('project') ?? ''
  const [loadState, setLoadState] = useState<LoadState>('loading')
  const [generateState, setGenerateState] = useState<GenerateState>('idle')
  const [overview, setOverview] = useState<DeveloperVoiceConsoleOverview | null>(null)
  const [projectId, setProjectId] = useState(requestedProject)
  const [voice, setVoice] = useState('')
  const [text, setText] = useState(() => t('歡迎使用 StoryVoice 聲線測試。', 'Welcome to the StoryVoice voice playground.'))
  const [idempotencyKey, setIdempotencyKey] = useState('')
  const [result, setResult] = useState<DeveloperVoicePlaygroundAudio | null>(null)
  const [audioUrl, setAudioUrl] = useState('')
  const [message, setMessage] = useState('')
  const [exampleTab, setExampleTab] = useState<ExampleTab>('curl')
  const controllerRef = useRef<AbortController | null>(null)
  const requestSequenceRef = useRef(0)
  const [loadedRequestedProject, setLoadedRequestedProject] = useState<string | null>(null)
  const routeTransitioning = loadedRequestedProject !== requestedProject

  useEffect(() => {
    setLoadState('loading')
    setOverview(null)
    requestSequenceRef.current += 1
    controllerRef.current?.abort()
    controllerRef.current = null
    setGenerateState('idle')
    setMessage('')
    setAudioUrl('')
    setResult(null)
    setIdempotencyKey('')
    setProjectId('')
    setVoice('')

    const controller = new AbortController()
    fetchDeveloperVoiceOverview(controller.signal)
      .then((nextOverview) => {
        if (controller.signal.aborted) return
        setOverview(nextOverview)
        const requested = nextOverview.projects.find((project) =>
          project.projectId === requestedProject || project.keyId === requestedProject)
        const firstAvailable = nextOverview.projects.find((project) =>
          project.status === 'active' || project.status === 'expiring-soon')
        const selected = requested ?? firstAvailable ?? nextOverview.projects[0]
        setProjectId(selected?.projectId || selected?.keyId || '')
        setVoice(selected?.voices.find((grant) => grant.status === 'active')?.voiceAlias ?? '')
        setLoadedRequestedProject(requestedProject)
        setLoadState('ready')
      })
      .catch((error: unknown) => {
        if (error instanceof DOMException && error.name === 'AbortError') return
        if (controller.signal.aborted) return
        setOverview(null)
        setLoadedRequestedProject(requestedProject)
        setLoadState('error')
      })
    return () => controller.abort()
  }, [requestedProject])

  useEffect(() => () => {
    requestSequenceRef.current += 1
    controllerRef.current?.abort()
    controllerRef.current = null
  }, [])

  useEffect(() => {
    if (!audioUrl) return
    return () => URL.revokeObjectURL(audioUrl)
  }, [audioUrl])

  useEffect(() => {
    setText((current) => current === '歡迎使用 StoryVoice 聲線測試。'
      || current === 'Welcome to the StoryVoice voice playground.'
      ? localize(locale, '歡迎使用 StoryVoice 聲線測試。', 'Welcome to the StoryVoice voice playground.')
      : current)
  }, [locale])

  const activeOverview = routeTransitioning ? null : overview
  const selectedProject = activeOverview?.projects.find((project) =>
    project.projectId === projectId || project.keyId === projectId)
  const availableVoices = selectedProject?.voices.filter((grant) => grant.status === 'active') ?? []
  const textCharacters = Array.from(text.normalize('NFKC').trim()).length
  const textBytes = useMemo(() => new TextEncoder().encode(text.normalize('NFKC').trim()).length, [text])
  const invalidText = !text.trim()
    || !activeOverview
    || textCharacters > activeOverview.maximumTextCharacters
    || textBytes > activeOverview.maximumTextUtf8Bytes
  const projectUnavailable = !activeOverview?.serviceEnabled
    || !selectedProject
    || !['active', 'expiring-soon'].includes(selectedProject.status)
    || availableVoices.length === 0

  function invalidatePendingGeneration() {
    requestSequenceRef.current += 1
    controllerRef.current?.abort()
    controllerRef.current = null
  }

  function clearResult() {
    setAudioUrl('')
    setResult(null)
    setIdempotencyKey('')
  }

  function selectProject(nextProjectId: string) {
    invalidatePendingGeneration()
    clearResult()
    setProjectId(nextProjectId)
    const nextProject = activeOverview?.projects.find((project) =>
      project.projectId === nextProjectId || project.keyId === nextProjectId)
    setVoice(nextProject?.voices.find((grant) => grant.status === 'active')?.voiceAlias ?? '')
    setGenerateState('idle')
    setMessage('')
  }

  function selectVoice(nextVoice: string) {
    invalidatePendingGeneration()
    clearResult()
    setVoice(nextVoice)
    setGenerateState('idle')
    setMessage('')
  }

  function updateText(nextText: string) {
    invalidatePendingGeneration()
    clearResult()
    setText(nextText)
    setGenerateState('idle')
    setMessage('')
  }

  async function generate(event?: FormEvent, reuseIdempotencyKey = false) {
    event?.preventDefault()
    if (routeTransitioning || loadState !== 'ready' || !activeOverview || invalidText || projectUnavailable || !voice) return

    const nextIdempotencyKey = reuseIdempotencyKey && idempotencyKey
      ? idempotencyKey
      : createIdempotencyKey()
    const requestSequence = requestSequenceRef.current + 1
    requestSequenceRef.current = requestSequence
    controllerRef.current?.abort()
    const controller = new AbortController()
    controllerRef.current = controller
    setIdempotencyKey(nextIdempotencyKey)
    setGenerateState('generating')
    setMessage('')
    setAudioUrl('')
    setResult(null)
    try {
      const nextResult = await synthesizeDeveloperVoicePlayground(
        projectId,
        voice,
        text,
        nextIdempotencyKey,
        csrfToken,
        controller.signal,
      )
      if (requestSequenceRef.current !== requestSequence || controller.signal.aborted) return
      const nextAudioUrl = URL.createObjectURL(nextResult.audio)
      setAudioUrl(nextAudioUrl)
      setResult(nextResult)
      setGenerateState('success')
      setMessage(t('語音已產生，可直接播放或下載 WAV。', 'Voice generated. You can play it now or download the WAV file.'))
    } catch (error) {
      if (requestSequenceRef.current !== requestSequence) return
      if (error instanceof DOMException && error.name === 'AbortError') {
        setGenerateState('cancelled')
        setMessage(t('已取消這次要求；如果合成已送進 GPU，後端仍可能完成安全收尾。', 'This request was cancelled. If synthesis had already reached the GPU, the server may still complete its safe cleanup.'))
        return
      }

      setGenerateState('error')
      if (error instanceof DeveloperVoicePlaygroundError) {
        const retry = error.retryAfterSeconds
          ? localize(locale, ` 建議 ${error.retryAfterSeconds} 秒後重試。`, ` Retry after ${error.retryAfterSeconds} seconds.`)
          : ''
        const request = error.requestId ? ` Request ID: ${error.requestId}.` : ''
        setMessage(`${outcomeLabel(error.code, locale) ?? t('語音產生失敗，請稍後再試。', 'Voice synthesis failed. Try again later.')}${retry}${request}`)
      } else {
        setMessage(t('語音產生失敗，請稍後再試。', 'Voice synthesis failed. Try again later.'))
      }
    } finally {
      if (controllerRef.current === controller) controllerRef.current = null
    }
  }

  function cancel() {
    controllerRef.current?.abort()
  }

  function handleExampleTabKeyDown(event: KeyboardEvent<HTMLButtonElement>, tab: ExampleTab) {
    const currentIndex = EXAMPLE_TABS.indexOf(tab)
    let nextIndex: number | null = null
    if (event.key === 'ArrowRight') nextIndex = (currentIndex + 1) % EXAMPLE_TABS.length
    if (event.key === 'ArrowLeft') nextIndex = (currentIndex - 1 + EXAMPLE_TABS.length) % EXAMPLE_TABS.length
    if (event.key === 'Home') nextIndex = 0
    if (event.key === 'End') nextIndex = EXAMPLE_TABS.length - 1
    if (nextIndex === null) return

    event.preventDefault()
    const nextTab = EXAMPLE_TABS[nextIndex]
    setExampleTab(nextTab)
    document.getElementById(`playground-example-tab-${nextTab}`)?.focus()
  }

  if (routeTransitioning || loadState === 'loading') {
    return <main className="library-state mx-auto my-12 max-w-7xl"><span role="status">{loadedRequestedProject === null
      ? t('正在準備 API Playground…', 'Preparing the API Playground…')
      : t('正在切換 API 專案…', 'Switching API projects…')}</span></main>
  }

  if (loadState === 'error') {
    return <main className="library-state mx-auto my-12 max-w-7xl border-rose-300 text-rose-700"><span role="alert">{t('Playground 資料讀取失敗，請重新整理頁面。', 'We could not load the Playground. Refresh the page to try again.')}</span></main>
  }

  return (
    <main className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
      <div className="flex flex-wrap items-end justify-between gap-5">
        <div>
          <p className="eyebrow">API Playground</p>
          <h1 className="mt-3 font-serif text-4xl tracking-tight sm:text-5xl">{t('用你的授權聲線試聽。', 'Try your authorized voices.')}</h1>
          <p className="mt-4 max-w-3xl text-sm leading-7 text-stone-600">
            {t('Playground 使用目前登入的 owner session，由同站後端代理合成。瀏覽器不會取得、保存或傳送 external bearer。', 'The Playground uses your signed-in owner session and a same-origin backend-for-frontend. Your browser never receives, stores, or sends an external bearer token.')}
          </p>
        </div>
        <Link className="font-semibold text-amber-800 underline" to="/developer">{t('返回開發者總覽', 'Back to developer overview')}</Link>
      </div>

      {activeOverview?.projects.length === 0 && (
        <div className="library-state mt-8 min-h-48">{t('目前沒有可供 Playground 使用的 API 專案。', 'No API project is currently available in the Playground.')}</div>
      )}

      {activeOverview && activeOverview.projects.length > 0 && (
        <form className="mt-8 grid gap-6 lg:grid-cols-[minmax(0,1.15fr)_minmax(20rem,.85fr)]" onSubmit={(event) => void generate(event)}>
          <section className="rounded-2xl border border-stone-200 bg-white/80 p-6">
            <div className="grid gap-4 sm:grid-cols-2">
              <label className="text-sm text-stone-600">
                {t('API 專案', 'API project')}
                <select className="auth-input mt-2" disabled={generateState === 'generating'} onChange={(event) => selectProject(event.target.value)} value={projectId}>
                  {activeOverview.projects.map((project) => (
                    <option key={project.keyId} value={project.projectId || project.keyId}>{project.displayName}</option>
                  ))}
                </select>
              </label>
              <label className="text-sm text-stone-600">
                {t('授權聲線', 'Authorized voice')}
                <select className="auth-input mt-2" disabled={generateState === 'generating'} onChange={(event) => selectVoice(event.target.value)} value={voice}>
                  {availableVoices.length === 0 && <option value="">{t('沒有可用聲線', 'No voice available')}</option>}
                  {availableVoices.map((grant) => <option key={grant.voiceAlias} value={grant.voiceAlias}>{grant.voiceAlias}</option>)}
                </select>
              </label>
            </div>

            <label className="mt-5 block text-sm text-stone-600">
              {t('試聽文字', 'Preview text')}
              <textarea
                className="auth-input mt-2 min-h-44 resize-y"
                disabled={generateState === 'generating'}
                onChange={(event) => updateText(event.target.value)}
                spellCheck="false"
                value={text}
              />
            </label>
            <div className="mt-2 flex flex-wrap justify-between gap-2 text-xs text-stone-400">
              <span className={textCharacters > activeOverview.maximumTextCharacters ? 'text-rose-700' : ''}>
                {textCharacters.toLocaleString(numberLocale)} / {activeOverview.maximumTextCharacters.toLocaleString(numberLocale)} {t('字', 'characters')}
              </span>
              <span className={textBytes > activeOverview.maximumTextUtf8Bytes ? 'text-rose-700' : ''}>
                {textBytes.toLocaleString(numberLocale)} / {activeOverview.maximumTextUtf8Bytes.toLocaleString(numberLocale)} UTF-8 bytes
              </span>
            </div>

            {!activeOverview.serviceEnabled && <p className="mt-4 text-sm text-amber-800">{t('語音 API 目前未啟用。', 'The voice API is currently disabled.')}</p>}
            {selectedProject && !['active', 'expiring-soon'].includes(selectedProject.status) && (
              <p className="mt-4 text-sm text-amber-800">{t('所選專案目前不在有效期間內。', 'The selected project is outside its active access window.')}</p>
            )}

            <div className="mt-6 flex flex-wrap gap-3">
              <button className="auth-submit" disabled={invalidText || projectUnavailable || !voice || generateState === 'generating'} type="submit">
                {generateState === 'generating' ? t('正在產生…', 'Generating…') : t('產生語音', 'Generate voice')}
              </button>
              {generateState === 'generating' && (
                <button className="rounded-full border border-stone-300 px-5 py-3 text-sm text-stone-700" onClick={cancel} type="button">{t('取消', 'Cancel')}</button>
              )}
              {result && generateState !== 'generating' && (
                <button className="rounded-full border border-amber-300 px-5 py-3 text-sm font-semibold text-amber-800" onClick={() => void generate(undefined, true)} type="button">
                  {t('用相同冪等鍵重送', 'Retry with the same idempotency key')}
                </button>
              )}
            </div>
          </section>

          <aside className="space-y-5">
            <section aria-live="polite" className="rounded-2xl border border-stone-200 bg-stone-900 p-6 text-stone-100">
              <p className="eyebrow text-amber-300">Result</p>
              <h2 className="mt-2 font-serif text-2xl">{t('合成結果', 'Synthesis result')}</h2>
              {generateState === 'idle' && <p className="mt-4 text-sm leading-6 text-stone-400">{t('送出後會在這裡顯示播放器、下載與安全的要求 metadata。', 'After submission, the player, download, and safe request metadata will appear here.')}</p>}
              {message && <p className={`mt-4 text-sm leading-6 ${generateState === 'error' ? 'text-rose-300' : 'text-stone-300'}`}>{message}</p>}
              {audioUrl && result && (
                <>
                  <audio className="mt-5 w-full" controls src={audioUrl}>{t('你的瀏覽器不支援音訊播放。', 'Your browser does not support audio playback.')}</audio>
                  <a className="mt-4 inline-flex font-semibold text-amber-200 underline" download={`storyvoice-${voice}-${result.requestId}.wav`} href={audioUrl}>{t('下載 WAV', 'Download WAV')}</a>
                  <dl className="mt-5 space-y-2 border-t border-stone-700 pt-5 text-xs text-stone-400">
                    <div><dt className="inline">{t('Request ID：', 'Request ID: ')}</dt><dd className="inline break-all text-stone-200">{result.requestId}</dd></div>
                    <div><dt className="inline">{t('Idempotency key：', 'Idempotency key: ')}</dt><dd className="inline break-all text-stone-200">{result.idempotencyKey}</dd></div>
                    <div><dt className="inline">{t('耗時：', 'Latency: ')}</dt><dd className="inline text-stone-200">{result.latencyMilliseconds.toLocaleString(numberLocale)} ms</dd></div>
                    <div><dt className="inline">{t('音訊：', 'Audio: ')}</dt><dd className="inline text-stone-200">{(result.audioDurationMilliseconds / 1000).toLocaleString(numberLocale, { maximumFractionDigits: 1 })} {t('秒', 'seconds')} · {formatBytes(result.responseBytes, numberLocale)}</dd></div>
                  </dl>
                </>
              )}
            </section>

            <section className="rounded-2xl border border-amber-200 bg-amber-50 p-5 text-sm leading-6 text-amber-950">
              {localize(locale, <>每分鐘上限 {activeOverview.requestsPerMinute.toLocaleString(numberLocale)} 次；Playground 與同一 consumer 的 external API 共用這份額度。401、404、409、429、503 都會顯示可理解的狀態；活動只記錄字數與輸出 metadata，不保存文字內容。</>, <>Limit: {activeOverview.requestsPerMinute.toLocaleString(numberLocale)} requests per minute. The Playground and external API share this allowance for the same consumer. Responses 401, 404, 409, 429, and 503 are shown as clear states. Activity records only character counts and output metadata, never the input text.</>)}
              <div className="mt-3 flex flex-wrap gap-4">
                <Link className="font-semibold underline" to={`/developer/usage?project=${encodeURIComponent(projectId)}`}>{t('查看用量', 'View usage')}</Link>
                <Link className="font-semibold underline" to="/developers/docs">{t('查看 API 文件', 'View API docs')}</Link>
              </div>
            </section>
          </aside>

          <section className="rounded-2xl border border-stone-200 bg-white/80 p-6 lg:col-span-2" aria-labelledby="playground-examples-heading">
            <p className="eyebrow">Server-side examples</p>
            <h2 className="mt-2 font-serif text-2xl text-stone-900" id="playground-examples-heading">{t('帶回後端串接', 'Integrate from your backend')}</h2>
            <p className="mt-2 text-sm leading-6 text-stone-500">{t('範例只使用環境變數或伺服器端 secret store；不要把 token 貼進瀏覽器程式碼。', 'These examples use environment variables or a server-side secret store. Never embed the token in browser code.')}</p>
            <div className="mt-4 flex flex-wrap gap-2" role="tablist" aria-label={t('API 範例語言', 'API example languages')}>
              {EXAMPLE_TABS.map((tab) => (
                <button
                  aria-controls={`playground-example-panel-${tab}`}
                  aria-selected={exampleTab === tab}
                  className={exampleTab === tab ? 'rounded-full bg-stone-900 px-4 py-2 text-xs text-white' : 'rounded-full border border-stone-300 px-4 py-2 text-xs text-stone-600'}
                  id={`playground-example-tab-${tab}`}
                  key={tab}
                  onClick={() => setExampleTab(tab)}
                  onKeyDown={(event) => handleExampleTabKeyDown(event, tab)}
                  role="tab"
                  tabIndex={exampleTab === tab ? 0 : -1}
                  type="button"
                >
                  {EXAMPLE_TAB_LABEL[tab]}
                </button>
              ))}
            </div>
            {EXAMPLE_TABS.map((tab) => (
              <pre
                aria-labelledby={`playground-example-tab-${tab}`}
                className="mt-4 overflow-x-auto rounded-2xl bg-stone-950 p-5 text-xs leading-6 text-stone-200"
                hidden={exampleTab !== tab}
                id={`playground-example-panel-${tab}`}
                key={tab}
                role="tabpanel"
                tabIndex={0}
              >
                <code>{exampleCode(tab, voice, locale)}</code>
              </pre>
            ))}
          </section>
        </form>
      )}
    </main>
  )
}
