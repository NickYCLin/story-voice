import { useEffect, useMemo, useState } from 'react'
import { Link, useLocation } from 'react-router-dom'

import { apiUrl } from '../api'
import { isPublicVoiceCard, type PublicVoiceCard } from '../publicVoiceCatalog'
import { VoicePreviewButton } from '../components/PublicVoicePreview'

const PUBLIC_VOICE_ENDPOINT = '/api/public/v1/voices'

type CatalogState =
  | { status: 'loading' }
  | { status: 'ready'; voices: PublicVoiceCard[] }
  | { status: 'disabled' }
  | { status: 'error'; message: string }

function statusLabel(status: string) {
  switch (status) {
    case 'available':
      return '可公開試聽／可申請訂閱'
    case 'authorization-pending':
      return '授權審查中'
    case 'coming-soon':
      return '即將開放'
    default:
      return '尚未開放'
  }
}

function VoiceCard({ voice }: { voice: PublicVoiceCard }) {
  const canViewPlans = voice.ctaKind === 'view-plans' && voice.subscriptionAvailable
  const label = statusLabel(voice.status)

  return (
    <article className="public-voice-card">
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
            <h2 className="font-serif text-2xl text-stone-900">
              <Link className="public-focus rounded hover:text-amber-800 underline decoration-amber-300 underline-offset-4" to={`/voices/${voice.alias}`}>
                {voice.displayName}
              </Link>
            </h2>
            <p className="mt-1 text-sm leading-6 text-stone-600">{voice.subtitle}</p>
          </div>
          <span className="public-status-pill" data-status={voice.status}>
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
          <div>
            {canViewPlans ? (
              <a className="public-catalog-button public-catalog-button-primary public-focus" href="#subscription-access">
                查看訂閱與申請說明
              </a>
            ) : (
              <button className="public-catalog-button public-catalog-button-muted" disabled type="button">
                {voice.status === 'authorization-pending' ? '授權審查中' : '尚未開放訂閱'}
              </button>
            )}
            <p className="mt-2 text-xs leading-5 text-stone-500">
              {canViewPlans
                ? '此入口只提供申請說明，不會立即結帳或顯示價格；實際 API 範圍以有效授權與方案權限為準。'
                : '完成商用與跨專案授權後才會開放。'}
            </p>
          </div>
        </div>
      </div>
    </article>
  )
}

function CatalogHeader() {
  return (
    <header className="relative z-10 border-b border-stone-200/80 bg-[#faf6ee]/90 backdrop-blur">
      <div className="mx-auto flex max-w-7xl flex-wrap items-center justify-between gap-4 px-6 py-5 lg:px-10">
        <Link aria-label="StoryVoice 公開聲線館" className="group flex items-center gap-3 public-focus rounded-xl" to="/voices">
          <span className="grid h-11 w-11 place-items-center rounded-2xl border border-amber-300 bg-amber-50 font-serif text-lg text-amber-800 shadow-[0_4px_18px_rgba(180,101,15,.14)]">
            SV
          </span>
          <span>
            <strong className="block font-serif text-lg tracking-wide text-stone-900">StoryVoice</strong>
            <span className="block text-[10px] uppercase tracking-[.26em] text-stone-500">Public voice catalog</span>
          </span>
        </Link>

        <nav aria-label="公開頁面導覽" className="flex flex-wrap items-center gap-2">
          <a className="rounded-full px-4 py-2 text-sm text-stone-600 hover:bg-white hover:text-stone-900 public-focus" href="#voice-catalog">聲線目錄</a>
          <a className="rounded-full px-4 py-2 text-sm text-stone-600 hover:bg-white hover:text-stone-900 public-focus" href="#subscription-access">訂閱與 API</a>
          <Link className="secondary-button public-focus" to="/">登入 StoryVoice</Link>
        </nav>
      </div>
    </header>
  )
}

export function PublicVoicesPage() {
  const { hash } = useLocation()
  const [catalog, setCatalog] = useState<CatalogState>({ status: 'loading' })
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

    async function loadCatalog() {
      try {
        const response = await fetch(apiUrl(PUBLIC_VOICE_ENDPOINT), {
          cache: 'no-store',
          credentials: 'omit',
          headers: { Accept: 'application/json' },
          signal: controller.signal,
        })
        if (response.status === 404) {
          setCatalog({ status: 'disabled' })
          return
        }
        if (!response.ok) throw new Error(`HTTP ${response.status}`)

        const body: unknown = await response.json()
        if (!Array.isArray(body)) throw new Error('Unexpected catalog response')
        const voices = body.filter(isPublicVoiceCard)
        setCatalog({ status: 'ready', voices })
      } catch (error) {
        if (controller.signal.aborted) return
        setCatalog({
          status: 'error',
          message: error instanceof Error ? error.message : 'Unknown error',
        })
      }
    }

    void loadCatalog()
    return () => controller.abort()
  }, [])

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
      <CatalogHeader />

      <main className="relative z-10">
        <section className="mx-auto max-w-7xl px-6 pb-14 pt-12 lg:px-10 lg:pb-20 lg:pt-16">
          <div className="overflow-hidden rounded-[2rem] border border-amber-200 bg-gradient-to-br from-amber-50 via-white to-orange-50 px-7 py-12 text-center shadow-[0_24px_80px_rgba(96,70,30,.09)] sm:px-12 sm:py-16">
            <p className="eyebrow">Public voice catalog</p>
            <h1 className="mx-auto mt-4 max-w-4xl font-serif text-4xl leading-tight text-stone-900 sm:text-5xl">
              先聽固定示範，再確認這個聲線<span className="text-amber-700">適用的授權方式。</span>
            </h1>
            <p className="mx-auto mt-5 max-w-2xl text-sm leading-7 text-stone-600 sm:text-base">
              這裡只展示已通過公開播放檢查的固定示範。所有聲音均為 AI 合成；跨專案 API、商用範圍、期限與撤銷條件，以有效授權及方案權限為準。
            </p>
            <a className="primary-button mt-8 public-focus" href="#voice-catalog">瀏覽公開聲線</a>
          </div>
        </section>

        <section className="mx-auto max-w-7xl px-6 pb-20 lg:px-10" id="voice-catalog">
          <div className="flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between">
            <div>
              <p className="eyebrow">Voice collection</p>
              <h2 className="mt-3 font-serif text-3xl text-stone-900">公開聲線</h2>
              <p className="mt-2 text-sm leading-6 text-stone-600">搜尋名稱、風格或已核准的使用情境。</p>
            </div>
            {catalog.status === 'ready' && voices.length > 0 && (
              <p aria-live="polite" className="text-sm text-stone-500">顯示 {filteredVoices.length}／{voices.length} 個聲線</p>
            )}
          </div>

          {catalog.status === 'ready' && voices.length > 0 && (
            <div className="mt-7 rounded-2xl border border-stone-200 bg-white/90 p-4 shadow-sm">
              <div className="grid gap-4 lg:grid-cols-[minmax(15rem,1.4fr)_repeat(3,minmax(9rem,.7fr))_auto]">
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
            <div aria-live="polite" className="library-state mt-8" role="status">正在載入公開聲線…</div>
          )}
          {catalog.status === 'disabled' && (
            <div className="library-state mt-8" role="status">
              <strong className="text-stone-800">公開聲線館目前尚未啟用</strong>
              <span className="mt-2 max-w-xl text-sm leading-6">啟用前不會公開任何角色、示範音檔或訂閱入口。</span>
            </div>
          )}
          {catalog.status === 'error' && (
            <div className="library-state mt-8 border-rose-200 text-rose-700" role="alert">
              <strong>暫時無法取得公開聲線</strong>
              <span className="mt-2 text-sm">請稍後重新整理頁面。</span>
              <span className="sr-only">{catalog.message}</span>
            </div>
          )}
          {catalog.status === 'ready' && voices.length === 0 && (
            <div className="library-state mt-8" role="status">
              <strong className="text-stone-800">目前沒有可公開展示的聲線</strong>
              <span className="mt-2 max-w-xl text-sm leading-6">只有完成公開展示、固定示範、商用與跨專案授權驗證的聲線才會出現在這裡。</span>
            </div>
          )}
          {catalog.status === 'ready' && voices.length > 0 && filteredVoices.length === 0 && (
            <div className="library-state mt-8" role="status">
              <strong className="text-stone-800">找不到符合條件的聲線</strong>
              <button className="secondary-button mt-4 public-focus" onClick={resetFilters} type="button">清除篩選</button>
            </div>
          )}
          {catalog.status === 'ready' && filteredVoices.length > 0 && (
            <div className="mt-8 grid gap-6 xl:grid-cols-2">
              {filteredVoices.map((voice) => <VoiceCard key={voice.alias} voice={voice} />)}
            </div>
          )}
        </section>

        <section className="border-y border-stone-200 bg-white/70" id="subscription-access">
          <div className="mx-auto max-w-7xl px-6 py-16 lg:px-10">
            <div className="max-w-3xl">
              <p className="eyebrow">Subscription & API</p>
              <h2 className="mt-3 font-serif text-3xl text-stone-900">訂閱與跨專案 API，跟著授權狀態開放</h2>
              <p className="mt-4 text-sm leading-7 text-stone-600 sm:text-base">
                公開卡片只負責展示已核准的固定示範；實際合成另以角色 entitlement 控制。只有授權仍在有效期限、用途涵蓋商用與跨專案 API，且未被撤銷時，專案金鑰才可使用該聲線。
              </p>
              <Link className="mt-4 inline-flex text-sm font-semibold text-amber-800 underline public-focus" to="/developers/docs">
                查看完整 API 文件 →
              </Link>
            </div>

            <div className="mt-8 grid gap-4 md:grid-cols-3">
              {[
                ['固定示範', '公開播放使用固定音檔，不接受訪客輸入文字，也不會臨時產生新語音。'],
                ['用途與期限', '商用、公開展示與跨專案 API 個別記錄，並依授權起訖時間判定是否可用。'],
                ['可撤銷', '授權撤銷或到期後，後續 API 要求會停止使用該角色聲線；既有資料依約定處理。'],
              ].map(([title, description], index) => (
                <article className="rounded-2xl border border-stone-200 bg-white p-6" key={title}>
                  <span aria-hidden="true" className="grid h-9 w-9 place-items-center rounded-full bg-amber-100 text-sm font-bold text-amber-800">{index + 1}</span>
                  <h3 className="mt-4 font-serif text-xl text-stone-900">{title}</h3>
                  <p className="mt-2 text-sm leading-6 text-stone-600">{description}</p>
                </article>
              ))}
            </div>

            <div className="mt-8 rounded-2xl border border-amber-200 bg-amber-50 p-6 text-sm leading-7 text-amber-950">
              <strong className="block font-semibold">公開可見，不代表所有用途自動獲准。</strong>
              API 消費者、可用角色、請求額度與授權範圍會分開核定；介面不會因聲線出現在目錄就自動授予合成權限。目前未連接即時結帳，也未提供方案價格，此處僅說明訂閱申請條件。
            </div>
          </div>
        </section>
      </main>

      <footer className="relative z-10 px-6 py-8 text-center text-xs leading-6 text-stone-500">
        StoryVoice AI-generated voice catalog. Use only within a valid, unexpired grant.
      </footer>
    </div>
  )
}
