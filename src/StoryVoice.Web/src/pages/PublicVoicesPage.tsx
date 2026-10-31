import { useEffect, useMemo, useState } from 'react'
import { Link, useLocation } from 'react-router-dom'

import { PublicSiteHeader } from '../components/PublicSiteHeader'
import { apiUrl } from '../api'
import { isPublicVoiceCard, safeSampleUrl, type PublicVoiceCard } from '../publicVoiceCatalog'
import { VoicePreviewButton } from '../components/PublicVoicePreview'

const PUBLIC_VOICE_ENDPOINT = '/api/public/v1/voices'

type CatalogState =
  | { status: 'loading' }
  | { status: 'ready'; voices: PublicVoiceCard[] }
  | { status: 'disabled' }
  | { status: 'error' }

function statusLabel(status: string) {
  switch (status) {
    case 'available':
      return '可公開試聽'
    case 'authorization-pending':
      return '授權審查中'
    case 'coming-soon':
      return '即將開放'
    default:
      return '尚未開放'
  }
}

function VoiceCard({ voice }: { voice: PublicVoiceCard }) {
  const label = voice.status === 'available' && !safeSampleUrl(voice) ? '示範尚未開放' : statusLabel(voice.status)

  return (
    <article className="public-voice-card" aria-labelledby={`voice-title-${voice.alias}`}>
      <div className="public-voice-card-stage" aria-hidden="true">
        <span className="public-voice-mark">聲</span>
        <div className="public-voice-wave">
          {[30, 52, 76, 44, 88, 60, 38, 72, 50, 82, 58, 34].map((height, index) => (
            <span key={`${height}-${index}`} style={{ height: `${height}%` }} />
          ))}
        </div>
      </div>

      <div className="flex flex-1 flex-col p-6">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <h3 className="font-serif text-2xl text-stone-900" id={`voice-title-${voice.alias}`}>
              <Link className="public-focus rounded hover:text-amber-800 underline decoration-amber-300 underline-offset-4" to={`/voices/${voice.alias}`}>
                {voice.displayName}
              </Link>
            </h3>
            <p className="mt-1 text-sm leading-6 text-stone-600">{voice.subtitle}</p>
          </div>
          <span className="public-status-pill" data-status={voice.status === 'available' && !safeSampleUrl(voice) ? 'unavailable' : voice.status}>
            <span aria-hidden="true" className="public-status-dot" />
            {label}
          </span>
        </div>

        <p className="mt-5 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm leading-6 text-amber-950">
          <strong className="font-semibold">AI 聲音揭露：</strong> {voice.disclosure}
        </p>

        {(voice.styles.length > 0 || voice.useCases.length > 0) && (
          <div className="mt-5 space-y-3">
            {voice.styles.length > 0 && (
              <div className="flex flex-wrap items-center gap-2" aria-label="核准聲線風格">
                <span className="text-xs font-semibold text-stone-500">風格</span>
                {voice.styles.map((style) => <span className="public-voice-chip" key={style}>{style}</span>)}
              </div>
            )}
            {voice.useCases.length > 0 && (
              <div className="flex flex-wrap items-center gap-2" aria-label="核准用途">
                <span className="text-xs font-semibold text-stone-500">用途</span>
                {voice.useCases.map((useCase) => <span className="public-voice-chip" key={useCase}>{useCase}</span>)}
              </div>
            )}
          </div>
        )}

        <div className="mt-auto grid gap-3 pt-6 sm:grid-cols-2">
          <VoicePreviewButton voice={voice} />
          <Link className="public-catalog-button public-catalog-button-primary public-focus self-start" to={`/voices/${voice.alias}`}>
            查看聲線與使用範圍 <span aria-hidden="true">→</span>
          </Link>
        </div>
      </div>
    </article>
  )
}

export function PublicVoicesPage() {
  const { hash } = useLocation()
  const [catalog, setCatalog] = useState<CatalogState>({ status: 'loading' })
  const [attempt, setAttempt] = useState(0)
  const [query, setQuery] = useState('')
  const [style, setStyle] = useState('')
  const [useCase, setUseCase] = useState('')
  const [availability, setAvailability] = useState('')

  useEffect(() => {
    if (hash === '#subscription-access' || hash === '#voice-catalog') {
      document.getElementById(hash.slice(1))?.scrollIntoView()
    }
  }, [hash, catalog.status])

  useEffect(() => {
    const controller = new AbortController()
    setCatalog({ status: 'loading' })
    const timeout = setTimeout(() => {
      controller.abort()
      setCatalog({ status: 'error' })
    }, 10_000)

    async function loadCatalog() {
      try {
        const response = await fetch(apiUrl(PUBLIC_VOICE_ENDPOINT), {
          cache: 'no-store',
          credentials: 'omit',
          headers: { Accept: 'application/json' },
          signal: controller.signal,
        })
        if (controller.signal.aborted) return
        if (response.status === 404) {
          setCatalog({ status: 'disabled' })
          return
        }
        if (!response.ok) throw new Error(`HTTP ${response.status}`)

        const body: unknown = await response.json()
        if (controller.signal.aborted) return
        if (!Array.isArray(body)) throw new Error('Unexpected catalog response')
        if (!body.every(isPublicVoiceCard)) throw new Error('Invalid catalog response')
        const voices = body
        setCatalog({ status: 'ready', voices })
      } catch {
        if (!controller.signal.aborted) setCatalog({ status: 'error' })
      } finally {
        clearTimeout(timeout)
      }
    }

    void loadCatalog()
    return () => { controller.abort(); clearTimeout(timeout) }
  }, [attempt])

  const voices = useMemo(() => catalog.status === 'ready' ? catalog.voices : [], [catalog])
  const styleOptions = useMemo(() => [...new Set(voices.flatMap((voice) => voice.styles))].sort(), [voices])
  const useCaseOptions = useMemo(() => [...new Set(voices.flatMap((voice) => voice.useCases))].sort(), [voices])
  const availabilityOptions = useMemo(() => [...new Set(voices.map((voice) => voice.status))].sort(), [voices])
  const showFilters = styleOptions.length > 1 || useCaseOptions.length > 1 || availabilityOptions.length > 1
  const normalizedQuery = query.trim().toLocaleLowerCase('zh-TW')
  const filteredVoices = voices.filter((voice) => {
    const searchable = [voice.displayName, voice.subtitle, ...voice.styles, ...voice.useCases]
      .join(' ')
      .toLocaleLowerCase('zh-TW')
    return (!normalizedQuery || searchable.includes(normalizedQuery))
      && (!style || voice.styles.includes(style))
      && (!useCase || voice.useCases.includes(useCase))
      && (!availability || voice.status === availability)
  })
  const filtersActive = Boolean(query || style || useCase || availability)

  function resetFilters() {
    setQuery('')
    setStyle('')
    setUseCase('')
    setAvailability('')
  }

  return (
    <div className="relative min-h-screen overflow-hidden bg-[#faf6ee] text-[#332a1f]">
      <div className="ambient ambient-one" aria-hidden="true" />
      <div className="ambient ambient-two" aria-hidden="true" />
      <PublicSiteHeader />

      <main className="voice-catalog-main" id="main-content" tabIndex={-1}>
        <section className="voice-catalog-intro" aria-labelledby="catalog-title">
          <div>
            <p className="eyebrow">STORYVOICE / VOICE COLLECTION</p>
            <h1 id="catalog-title">為故事，找到合適的聲音。</h1>
            <p>先聽固定示範，再查看聲線風格與使用範圍。你的私人角色與書籍不會出現在這裡。</p>
          </div>
          <a className="voice-help-link public-focus" href="#subscription-access">如何使用聲線 <span aria-hidden="true">↓</span></a>
        </section>

        <section className="voice-catalog-collection" id="voice-catalog" aria-labelledby="collection-title" aria-busy={catalog.status === 'loading'}>
          <div className="voice-collection-heading">
            <h2 id="collection-title">公開聲線館</h2>
            {catalog.status === 'ready' && voices.length > 0 && <p aria-live="polite">顯示 {filteredVoices.length}／{voices.length} 個聲線</p>}
          </div>
          {catalog.status === 'ready' && voices.length > 0 && (
            <div className="mt-7 rounded-2xl border border-stone-200 bg-white/90 p-4 shadow-sm">
              <div className="voice-catalog-filters">
                <label className="text-xs font-semibold text-stone-600">
                  搜尋公開聲線
                  <input
                    className="auth-input mt-2 public-focus"
                    onChange={(event) => setQuery(event.target.value)}
                    placeholder="名稱、風格或用途"
                    type="search"
                    value={query}
                  />
                </label>

                {showFilters && styleOptions.length > 1 && (
                  <label className="text-xs font-semibold text-stone-600">
                    聲線風格
                    <select className="auth-input mt-2 public-focus" onChange={(event) => setStyle(event.target.value)} value={style}>
                      <option value="">全部風格</option>
                      {styleOptions.map((option) => <option key={option} value={option}>{option}</option>)}
                    </select>
                  </label>
                )}

                {showFilters && useCaseOptions.length > 1 && (
                  <label className="text-xs font-semibold text-stone-600">
                    核准用途
                    <select className="auth-input mt-2 public-focus" onChange={(event) => setUseCase(event.target.value)} value={useCase}>
                      <option value="">全部用途</option>
                      {useCaseOptions.map((option) => <option key={option} value={option}>{option}</option>)}
                    </select>
                  </label>
                )}

                {showFilters && availabilityOptions.length > 1 && (
                  <label className="text-xs font-semibold text-stone-600">
                    開放狀態
                    <select className="auth-input mt-2 public-focus" onChange={(event) => setAvailability(event.target.value)} value={availability}>
                      <option value="">全部狀態</option>
                      {availabilityOptions.map((option) => <option key={option} value={option}>{statusLabel(option)}</option>)}
                    </select>
                  </label>
                )}

                {filtersActive && (
                  <button className="secondary-button self-end public-focus" onClick={resetFilters} type="button">清除篩選</button>
                )}
              </div>
            </div>
          )}

          {catalog.status === 'loading' && (
            <div aria-live="polite" className="voice-catalog-state" role="status">正在載入公開聲線…</div>
          )}
          {catalog.status === 'disabled' && (
            <div className="voice-catalog-state" role="status">
              <span className="voice-state-symbol" aria-hidden="true">♪</span>
              <h3>公開聲線館目前尚未啟用</h3>
              <span className="mt-2 max-w-xl text-sm leading-6">目前沒有開放試聽。你仍可進入私人工作區，使用自己的角色與故事。</span>
            </div>
          )}
          {catalog.status === 'error' && (
            <div className="voice-catalog-state voice-catalog-error" role="alert">
              <strong>暫時無法取得公開聲線</strong>
              <span className="mt-2 text-sm">連線可能暫時中斷，請重試。你的私人資料不受影響。</span>
              <button className="secondary-button public-focus mt-4" onClick={() => setAttempt(value => value + 1)} type="button">重新載入</button>
            </div>
          )}
          {catalog.status === 'ready' && voices.length === 0 && (
            <div className="voice-catalog-state" role="status">
              <strong className="text-stone-800">目前沒有可公開展示的聲線</strong>
              <span className="mt-2 max-w-xl text-sm leading-6">目前沒有已核准的公開示範。私人角色仍保留在你的工作區。</span>
            </div>
          )}
          {catalog.status === 'ready' && voices.length > 0 && filteredVoices.length === 0 && (
            <div className="voice-catalog-state" role="status">
              <strong className="text-stone-800">找不到符合條件的聲線</strong>
              <button className="secondary-button mt-4 public-focus" onClick={resetFilters} type="button">清除篩選</button>
            </div>
          )}
          {catalog.status === 'ready' && filteredVoices.length > 0 && (
            <div className="mt-6 grid gap-5 xl:grid-cols-2">
              {filteredVoices.map((voice) => <VoiceCard key={voice.alias} voice={voice} />)}
            </div>
          )}
        </section>

        <section className="voice-next-steps" id="subscription-access" aria-labelledby="voice-use-title">
          <div className="voice-next-intro">
            <p className="eyebrow">NEXT STEP</p>
            <h2 id="voice-use-title">找到喜歡的聲音之後</h2>
            <p>公開試聽與私人創作分開管理。選擇符合你用途的入口。</p>
          </div>
          <div className="voice-paths">
            <article>
              <span className="voice-step-number" aria-hidden="true">01</span>
              <h3>為自己的故事配音</h3>
              <p>進入工作區管理私人角色，再到系列配音設定角色聲線。公開聲線目前不能一鍵加入故事。</p>
              <Link className="voice-text-link public-focus" to="/characters">前往角色管理 <span aria-hidden="true">→</span></Link>
              <small>需要登入；只會看到自己的角色。</small>
            </article>
            <article>
              <span className="voice-step-number" aria-hidden="true">02</span>
              <h3>在你的產品中使用</h3>
              <p>先閱讀 API 文件，確認專案授權、可用聲線與額度。公開試聽不會自動開通合成權限。</p>
              <Link className="voice-text-link public-focus" to="/developers/docs">閱讀 API 文件 <span aria-hidden="true">→</span></Link>
              <small>目前不提供線上訂閱或結帳。</small>
            </article>
          </div>
          <details className="voice-authorization">
            <summary>公開示範與使用授權有什麼不同？</summary>
            <div>
              <p>所有示範均為 AI 合成的固定音檔。訪客不能輸入文字，也不會在試聽時產生新的語音。</p>
              <p>公開可見，不代表所有用途自動獲准。商用、公開散布與跨專案 API，需個別確認用途、地區、有效期限與撤銷條件；到期或撤銷後，後續使用會停止。</p>
              <p>目前未連接即時結帳，也未提供方案價格。請在聲線詳細頁查看目前核准範圍。</p>
            </div>
          </details>
        </section>
      </main>
      <footer className="voice-catalog-footer">StoryVoice · AI 故事導演 <span>僅使用你擁有或獲得授權的內容。</span></footer>
    </div>
  )
}
