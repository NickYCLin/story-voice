import { useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Link, useOutletContext, useSearchParams } from 'react-router-dom'

import type { AuthedOutletContext } from '../authOutletContext'
import {
  DeveloperVoicePlaygroundError,
  fetchDeveloperVoiceOverview,
  synthesizeDeveloperVoicePlayground,
} from '../developerVoiceConsole'
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

const formatBytes = (bytes: number) => bytes < 1024
  ? `${bytes} B`
  : `${(bytes / 1024).toFixed(1)} KiB`

const createIdempotencyKey = () => `play_${crypto.randomUUID().replaceAll('-', '')}`

const EXAMPLE_TAB_LABEL: Record<ExampleTab, string> = {
  curl: 'curl',
  javascript: 'JavaScript server',
  csharp: 'C#',
  python: 'Python',
}

const exampleCode = (tab: ExampleTab, voice: string) => {
  const selectedVoice = voice || 'your-authorized-voice'
  if (tab === 'curl') {
    return [
      'curl --request POST "https://your-storyvoice-host.example/api/external/v1/speech" \\',
      '  --header "Authorization: Bearer $STORYVOICE_TOKEN" \\',
      '  --header "Content-Type: application/json" \\',
      '  --header "Idempotency-Key: $IDEMPOTENCY_KEY" \\',
      `  --data '{"voice":"${selectedVoice}","text":"請替換成要合成的文字"}'`,
    ].join('\n')
  }
  if (tab === 'javascript') {
    return [
      "import { writeFile } from 'node:fs/promises'",
      "const response = await fetch('https://your-storyvoice-host.example/api/external/v1/speech', {",
      "  method: 'POST',",
      "  headers: { Authorization: `Bearer ${process.env.STORYVOICE_TOKEN}`,",
      "    'Content-Type': 'application/json', 'Idempotency-Key': crypto.randomUUID() },",
      `  body: JSON.stringify({ voice: '${selectedVoice}', text: '請替換成要合成的文字' }),`,
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
      `request.Content = JsonContent.Create(new { voice = "${selectedVoice}", text = "請替換成要合成的文字" });`,
      'using var response = await client.SendAsync(request);',
      'response.EnsureSuccessStatusCode();',
      'await File.WriteAllBytesAsync("speech.wav", await response.Content.ReadAsByteArrayAsync());',
    ].join('\n')
  }
  return [
    "response = requests.post(endpoint, headers={",
    "    'Authorization': f'Bearer {os.environ[\"STORYVOICE_TOKEN\"]}',",
    "    'Idempotency-Key': str(uuid.uuid4()),",
    `}, json={'voice': '${selectedVoice}', 'text': '請替換成要合成的文字'}, timeout=60)`,
    'response.raise_for_status()',
    "Path('speech.wav').write_bytes(response.content)",
  ].join('\n')
}

export function DeveloperPlaygroundPage() {
  const { csrfToken } = useOutletContext<AuthedOutletContext>()
  const [searchParams] = useSearchParams()
  const requestedProject = searchParams.get('project') ?? ''
  const [loadState, setLoadState] = useState<LoadState>('loading')
  const [generateState, setGenerateState] = useState<GenerateState>('idle')
  const [overview, setOverview] = useState<DeveloperVoiceConsoleOverview | null>(null)
  const [projectId, setProjectId] = useState(requestedProject)
  const [voice, setVoice] = useState('')
  const [text, setText] = useState('歡迎使用 StoryVoice 聲線測試。')
  const [idempotencyKey, setIdempotencyKey] = useState('')
  const [result, setResult] = useState<DeveloperVoicePlaygroundAudio | null>(null)
  const [audioUrl, setAudioUrl] = useState('')
  const [message, setMessage] = useState('')
  const [exampleTab, setExampleTab] = useState<ExampleTab>('curl')
  const controllerRef = useRef<AbortController | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    fetchDeveloperVoiceOverview(controller.signal)
      .then((nextOverview) => {
        setOverview(nextOverview)
        const requested = nextOverview.projects.find((project) =>
          project.projectId === requestedProject || project.keyId === requestedProject)
        const firstAvailable = nextOverview.projects.find((project) =>
          project.status === 'active' || project.status === 'expiring-soon')
        const selected = requested ?? firstAvailable ?? nextOverview.projects[0]
        setProjectId(selected?.projectId || selected?.keyId || '')
        setVoice(selected?.voices.find((grant) => grant.status === 'active')?.voiceAlias ?? '')
        setLoadState('ready')
      })
      .catch((error: unknown) => {
        if (error instanceof DOMException && error.name === 'AbortError') return
        setLoadState('error')
      })
    return () => controller.abort()
  }, [requestedProject])

  useEffect(() => () => {
    controllerRef.current?.abort()
    if (audioUrl) URL.revokeObjectURL(audioUrl)
  }, [audioUrl])

  const selectedProject = overview?.projects.find((project) =>
    project.projectId === projectId || project.keyId === projectId)
  const availableVoices = selectedProject?.voices.filter((grant) => grant.status === 'active') ?? []
  const textCharacters = Array.from(text.normalize('NFKC').trim()).length
  const textBytes = useMemo(() => new TextEncoder().encode(text.normalize('NFKC').trim()).length, [text])
  const invalidText = !text.trim()
    || !overview
    || textCharacters > overview.maximumTextCharacters
    || textBytes > overview.maximumTextUtf8Bytes
  const projectUnavailable = !overview?.serviceEnabled
    || !selectedProject
    || !['active', 'expiring-soon'].includes(selectedProject.status)
    || availableVoices.length === 0

  function clearResult() {
    if (audioUrl) URL.revokeObjectURL(audioUrl)
    setAudioUrl('')
    setResult(null)
    setIdempotencyKey('')
  }

  function selectProject(nextProjectId: string) {
    clearResult()
    setProjectId(nextProjectId)
    const nextProject = overview?.projects.find((project) =>
      project.projectId === nextProjectId || project.keyId === nextProjectId)
    setVoice(nextProject?.voices.find((grant) => grant.status === 'active')?.voiceAlias ?? '')
    setGenerateState('idle')
    setMessage('')
  }

  function selectVoice(nextVoice: string) {
    clearResult()
    setVoice(nextVoice)
    setGenerateState('idle')
    setMessage('')
  }

  function updateText(nextText: string) {
    clearResult()
    setText(nextText)
    setGenerateState('idle')
    setMessage('')
  }

  async function generate(event?: FormEvent, reuseIdempotencyKey = false) {
    event?.preventDefault()
    if (!overview || invalidText || projectUnavailable || !voice) return

    const nextIdempotencyKey = reuseIdempotencyKey && idempotencyKey
      ? idempotencyKey
      : createIdempotencyKey()
    setIdempotencyKey(nextIdempotencyKey)
    setGenerateState('generating')
    setMessage('')
    if (audioUrl) URL.revokeObjectURL(audioUrl)
    setAudioUrl('')
    setResult(null)
    controllerRef.current?.abort()
    const controller = new AbortController()
    controllerRef.current = controller
    try {
      const nextResult = await synthesizeDeveloperVoicePlayground(
        projectId,
        voice,
        text,
        nextIdempotencyKey,
        csrfToken,
        controller.signal,
      )
      if (audioUrl) URL.revokeObjectURL(audioUrl)
      const nextAudioUrl = URL.createObjectURL(nextResult.audio)
      setAudioUrl(nextAudioUrl)
      setResult(nextResult)
      setGenerateState('success')
      setMessage('語音已產生，可直接播放或下載 WAV。')
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') {
        setGenerateState('cancelled')
        setMessage('已取消這次要求；如果合成已送進 GPU，後端仍可能完成安全收尾。')
        return
      }

      setGenerateState('error')
      if (error instanceof DeveloperVoicePlaygroundError) {
        const retry = error.retryAfterSeconds ? ` 建議 ${error.retryAfterSeconds} 秒後重試。` : ''
        const request = error.requestId ? ` Request ID：${error.requestId}。` : ''
        setMessage(`${OUTCOME_LABEL[error.code] ?? error.message}${retry}${request}`)
      } else {
        setMessage('語音產生失敗，請稍後再試。')
      }
    } finally {
      if (controllerRef.current === controller) controllerRef.current = null
    }
  }

  function cancel() {
    controllerRef.current?.abort()
  }

  if (loadState === 'loading') {
    return <main className="library-state mx-auto my-12 max-w-7xl">正在準備 API Playground…</main>
  }

  if (loadState === 'error') {
    return <main className="library-state mx-auto my-12 max-w-7xl border-rose-300 text-rose-700">Playground 資料讀取失敗，請重新整理頁面。</main>
  }

  return (
    <main className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
      <div className="flex flex-wrap items-end justify-between gap-5">
        <div>
          <p className="eyebrow">API Playground</p>
          <h1 className="mt-3 font-serif text-4xl tracking-tight sm:text-5xl">用你的授權聲線試聽。</h1>
          <p className="mt-4 max-w-3xl text-sm leading-7 text-stone-600">
            Playground 使用目前登入的 owner session，由同站後端代理合成。瀏覽器不會取得、保存或傳送 external bearer。
          </p>
        </div>
        <Link className="font-semibold text-amber-800 underline" to="/developer">返回開發者總覽</Link>
      </div>

      {overview?.projects.length === 0 && (
        <div className="library-state mt-8 min-h-48">目前沒有可供 Playground 使用的 API 專案。</div>
      )}

      {overview && overview.projects.length > 0 && (
        <form className="mt-8 grid gap-6 lg:grid-cols-[minmax(0,1.15fr)_minmax(20rem,.85fr)]" onSubmit={(event) => void generate(event)}>
          <section className="rounded-2xl border border-stone-200 bg-white/80 p-6">
            <div className="grid gap-4 sm:grid-cols-2">
              <label className="text-sm text-stone-600">
                API 專案
                <select className="auth-input mt-2" onChange={(event) => selectProject(event.target.value)} value={projectId}>
                  {overview.projects.map((project) => (
                    <option key={project.keyId} value={project.projectId || project.keyId}>{project.displayName}</option>
                  ))}
                </select>
              </label>
              <label className="text-sm text-stone-600">
                授權聲線
                <select className="auth-input mt-2" onChange={(event) => selectVoice(event.target.value)} value={voice}>
                  {availableVoices.length === 0 && <option value="">沒有可用聲線</option>}
                  {availableVoices.map((grant) => <option key={grant.voiceAlias} value={grant.voiceAlias}>{grant.voiceAlias}</option>)}
                </select>
              </label>
            </div>

            <label className="mt-5 block text-sm text-stone-600">
              試聽文字
              <textarea
                className="auth-input mt-2 min-h-44 resize-y"
                onChange={(event) => updateText(event.target.value)}
                spellCheck="false"
                value={text}
              />
            </label>
            <div className="mt-2 flex flex-wrap justify-between gap-2 text-xs text-stone-400">
              <span className={textCharacters > overview.maximumTextCharacters ? 'text-rose-700' : ''}>
                {textCharacters} / {overview.maximumTextCharacters} 字
              </span>
              <span className={textBytes > overview.maximumTextUtf8Bytes ? 'text-rose-700' : ''}>
                {textBytes} / {overview.maximumTextUtf8Bytes} UTF-8 bytes
              </span>
            </div>

            {!overview.serviceEnabled && <p className="mt-4 text-sm text-amber-800">語音 API 目前未啟用。</p>}
            {selectedProject && !['active', 'expiring-soon'].includes(selectedProject.status) && (
              <p className="mt-4 text-sm text-amber-800">所選專案目前不在有效期間內。</p>
            )}

            <div className="mt-6 flex flex-wrap gap-3">
              <button className="auth-submit" disabled={invalidText || projectUnavailable || !voice || generateState === 'generating'} type="submit">
                {generateState === 'generating' ? '正在產生…' : '產生語音'}
              </button>
              {generateState === 'generating' && (
                <button className="rounded-full border border-stone-300 px-5 py-3 text-sm text-stone-700" onClick={cancel} type="button">取消</button>
              )}
              {result && generateState !== 'generating' && (
                <button className="rounded-full border border-amber-300 px-5 py-3 text-sm font-semibold text-amber-800" onClick={() => void generate(undefined, true)} type="button">
                  用相同冪等鍵重送
                </button>
              )}
            </div>
          </section>

          <aside className="space-y-5">
            <section aria-live="polite" className="rounded-2xl border border-stone-200 bg-stone-900 p-6 text-stone-100">
              <p className="eyebrow text-amber-300">Result</p>
              <h2 className="mt-2 font-serif text-2xl">合成結果</h2>
              {generateState === 'idle' && <p className="mt-4 text-sm leading-6 text-stone-400">送出後會在這裡顯示播放器、下載與安全的要求 metadata。</p>}
              {message && <p className={`mt-4 text-sm leading-6 ${generateState === 'error' ? 'text-rose-300' : 'text-stone-300'}`}>{message}</p>}
              {audioUrl && result && (
                <>
                  <audio className="mt-5 w-full" controls src={audioUrl}>你的瀏覽器不支援音訊播放。</audio>
                  <a className="mt-4 inline-flex font-semibold text-amber-200 underline" download={`storyvoice-${voice}-${result.requestId}.wav`} href={audioUrl}>下載 WAV</a>
                  <dl className="mt-5 space-y-2 border-t border-stone-700 pt-5 text-xs text-stone-400">
                    <div><dt className="inline">Request ID：</dt><dd className="inline break-all text-stone-200">{result.requestId}</dd></div>
                    <div><dt className="inline">Idempotency key：</dt><dd className="inline break-all text-stone-200">{result.idempotencyKey}</dd></div>
                    <div><dt className="inline">耗時：</dt><dd className="inline text-stone-200">{result.latencyMilliseconds} ms</dd></div>
                    <div><dt className="inline">音訊：</dt><dd className="inline text-stone-200">{(result.audioDurationMilliseconds / 1000).toFixed(1)} 秒 · {formatBytes(result.responseBytes)}</dd></div>
                  </dl>
                </>
              )}
            </section>

            <section className="rounded-2xl border border-amber-200 bg-amber-50 p-5 text-sm leading-6 text-amber-950">
              每分鐘上限 {overview.requestsPerMinute} 次。401、404、409、429、503 都會顯示可理解的狀態；活動只記錄字數與輸出 metadata，不保存文字內容。
              <div className="mt-3 flex flex-wrap gap-4">
                <Link className="font-semibold underline" to={`/developer/usage?project=${encodeURIComponent(projectId)}`}>查看用量</Link>
                <Link className="font-semibold underline" to="/developers/docs">查看 API 文件</Link>
              </div>
            </section>
          </aside>

          <section className="rounded-2xl border border-stone-200 bg-white/80 p-6 lg:col-span-2" aria-labelledby="playground-examples-heading">
            <p className="eyebrow">Server-side examples</p>
            <h2 className="mt-2 font-serif text-2xl text-stone-900" id="playground-examples-heading">帶回後端串接</h2>
            <p className="mt-2 text-sm leading-6 text-stone-500">範例只使用環境變數或伺服器端 secret store；不要把 token 貼進瀏覽器程式碼。</p>
            <div className="mt-4 flex flex-wrap gap-2" role="tablist" aria-label="API 範例語言">
              {(Object.keys(EXAMPLE_TAB_LABEL) as ExampleTab[]).map((tab) => (
                <button
                  aria-selected={exampleTab === tab}
                  className={exampleTab === tab ? 'rounded-full bg-stone-900 px-4 py-2 text-xs text-white' : 'rounded-full border border-stone-300 px-4 py-2 text-xs text-stone-600'}
                  key={tab}
                  onClick={() => setExampleTab(tab)}
                  role="tab"
                  type="button"
                >
                  {EXAMPLE_TAB_LABEL[tab]}
                </button>
              ))}
            </div>
            <pre className="mt-4 overflow-x-auto rounded-2xl bg-stone-950 p-5 text-xs leading-6 text-stone-200"><code>{exampleCode(exampleTab, voice)}</code></pre>
          </section>
        </form>
      )}
    </main>
  )
}
