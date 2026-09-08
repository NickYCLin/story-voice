import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { apiUrl } from '../api'
import { VoicePreviewButton } from '../components/PublicVoicePreview'
import { isPublicVoiceDetail, type PublicVoiceDetail } from '../publicVoiceCatalog'

type DetailState =
  | { status: 'loading' | 'unavailable' | 'error'; alias: string }
  | { status: 'ready'; alias: string; detail: PublicVoiceDetail }

const dateFormat = new Intl.DateTimeFormat('zh-TW', {
  year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit',
  timeZone: 'Asia/Taipei', hour12: false,
})
const countryNames = new Intl.DisplayNames(['zh-TW'], { type: 'region' })

export function PublicVoiceDetailPage() {
  const { alias = '' } = useParams()
  const [attempt, setAttempt] = useState(0)
  const [state, setState] = useState<DetailState>({ status: 'loading', alias })

  useEffect(() => {
    if (!/^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$/.test(alias)) {
      setState({ status: 'unavailable', alias })
      return
    }
    const controller = new AbortController()
    let active = true
    let expiryTimer: ReturnType<typeof setTimeout> | undefined
    setState({ status: 'loading', alias })
    const timeout = setTimeout(() => {
      controller.abort()
      if (active) setState({ status: 'error', alias })
    }, 10_000)

    async function load() {
      try {
        const response = await fetch(apiUrl(`/api/public/v1/voices/${encodeURIComponent(alias)}`), {
          cache: 'no-store', credentials: 'omit', headers: { Accept: 'application/json' }, signal: controller.signal,
        })
        if (!active || controller.signal.aborted) return
        if (response.status === 404) {
          setState({ status: 'unavailable', alias })
          return
        }
        if (!response.ok) throw new Error('Voice unavailable')
        const detail: unknown = await response.json()
        if (!active || controller.signal.aborted) return
        if (!isPublicVoiceDetail(detail, alias)) throw new Error('Invalid voice detail')
        if (Date.parse(detail.license.effectiveAtUtc) > Date.now() || Date.parse(detail.license.expiresAtUtc) <= Date.now()) {
          setState({ status: 'unavailable', alias })
          return
        }
        setState({ status: 'ready', alias, detail })
        expiryTimer = setTimeout(() => setAttempt(current => current + 1),
          Math.min(Date.parse(detail.license.expiresAtUtc) - Date.now(), 2_147_483_647))
      } catch {
        if (active && !controller.signal.aborted) setState({ status: 'error', alias })
      } finally {
        clearTimeout(timeout)
      }
    }
    function refresh() { setAttempt(current => current + 1) }
    window.addEventListener('focus', refresh)
    void load()
    return () => {
      active = false
      controller.abort()
      clearTimeout(timeout)
      clearTimeout(expiryTimer)
      window.removeEventListener('focus', refresh)
    }
  }, [alias, attempt])

  const current = state.alias === alias ? state : { status: 'loading' as const, alias }
  return (
    <div className="min-h-screen bg-[#faf6ee] text-stone-900">
      <header className="border-b border-stone-200">
        <nav aria-label="公開頁面導覽" className="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-4 px-6 py-5">
          <Link className="public-focus rounded font-serif text-xl" to="/voices">StoryVoice 聲線館</Link>
          <Link className="secondary-button public-focus" to="/">登入 StoryVoice</Link>
        </nav>
      </header>
      <main className="mx-auto max-w-5xl px-6 py-10 sm:py-16">
        <Link className="public-focus rounded text-sm text-amber-800 underline" to="/voices">← 返回公開聲線</Link>
        {current.status === 'loading' && <p className="library-state mt-8" role="status">正在載入聲線資料…</p>}
        {current.status === 'unavailable' && (
          <section className="library-state mt-8" role="status">
            <h1 className="font-serif text-2xl">目前無法公開查看這個聲線</h1>
            <p className="mt-3 text-sm leading-6">聲線可能尚未開放，或已停止公開展示。請回聲線館查看目前可用的聲線。</p>
          </section>
        )}
        {current.status === 'error' && (
          <section className="library-state mt-8" role="alert">
            <h1 className="font-serif text-2xl">聲線資料暫時無法讀取</h1>
            <button className="secondary-button public-focus mt-4" onClick={() => setAttempt(current => current + 1)} type="button">重新載入</button>
          </section>
        )}
        {current.status === 'ready' && <VoiceDetail key={alias} detail={current.detail} />}
      </main>
    </div>
  )
}

function VoiceDetail({ detail: { voice, license } }: { detail: PublicVoiceDetail }) {
  const territory = license.territoryMode === 'worldwide' ? '全球'
    : license.territoryCountryCodes.map(code => `${countryNames.of(code) ?? code} (${code})`).join('、')
  return (
    <article className="mt-8 space-y-8 [overflow-wrap:anywhere]">
      <section className="rounded-3xl border border-amber-200 bg-white p-6 shadow-sm sm:p-10">
        <p className="eyebrow">公開聲線</p>
        <h1 className="mt-3 font-serif text-3xl sm:text-4xl">{voice.displayName}</h1>
        <p className="mt-3 leading-7 text-stone-600">{voice.subtitle}</p>
        <p className="mt-5 rounded-xl bg-amber-50 p-4 text-sm text-amber-950"><strong>AI 聲音揭露：</strong> {voice.disclosure}</p>
        <dl className="my-6 space-y-4 text-sm">
          <div><dt className="font-semibold">核准聲線風格</dt><dd className="mt-2 flex flex-wrap gap-2">{voice.styles.map(style => <span className="public-voice-chip" key={style}>{style}</span>)}</dd></div>
          <div><dt className="font-semibold">核准用途</dt><dd className="mt-2 flex flex-wrap gap-2">{voice.useCases.map(useCase => <span className="public-voice-chip" key={useCase}>{useCase}</span>)}</dd></div>
        </dl>
        <div className="max-w-sm"><VoicePreviewButton voice={voice} /></div>
      </section>
      <section className="rounded-3xl border border-stone-200 bg-white p-6 sm:p-10">
        <h2 className="font-serif text-2xl">授權摘要</h2>
        <p className="mt-3 text-sm leading-7 text-stone-600">以下是這個聲線目前的核准範圍。使用前仍需取得符合用途、地區與期限的專案授權。</p>
        <dl className="mt-6 grid gap-5 text-sm sm:grid-cols-2">
          {[
            ['商業使用', license.commercialUseAllowed ? '核准範圍內可申請' : '尚未開放'],
            ['公開散布', license.publicDistributionAllowed ? '核准範圍內可申請' : '尚未開放'],
            ['跨專案 API', license.crossProjectApiAllowed ? '須另外取得專案授權' : '尚未開放'],
            ['適用地區', territory],
          ].map(([label, value]) => <div key={label}><dt className="font-semibold text-stone-500">{label}</dt><dd className="mt-1 leading-6">{value}</dd></div>)}
          <div><dt className="font-semibold text-stone-500">生效時間（台北）</dt><dd className="mt-1"><time dateTime={license.effectiveAtUtc}>{dateFormat.format(new Date(license.effectiveAtUtc))}</time></dd></div>
          <div><dt className="font-semibold text-stone-500">到期時間（台北）</dt><dd className="mt-1"><time dateTime={license.expiresAtUtc}>{dateFormat.format(new Date(license.expiresAtUtc))}</time></dd></div>
        </dl>
        <p className="mt-6 rounded-xl bg-amber-50 p-4 text-sm leading-7 text-amber-950">授權到期或撤銷後會停止公開展示與後續使用。公開試聽不會自動開通 API，也不代表取得永久或不限用途的授權。</p>
        <div className="mt-6 flex flex-wrap gap-4">
          {voice.ctaKind === 'view-plans' && voice.subscriptionAvailable && <Link className="primary-button public-focus" to="/voices#subscription-access">查看訂閱與申請說明</Link>}
          <Link className="secondary-button public-focus" to="/developers/docs">閱讀 API 文件</Link>
        </div>
      </section>
    </article>
  )
}
