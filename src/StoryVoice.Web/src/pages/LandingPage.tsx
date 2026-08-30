import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

import { apiUrl } from '../api'

type Feature = {
  eyebrow: string
  title: string
  description: string
  image: string
  imageAlt: string
}

const FEATURES: Feature[] = [
  {
    eyebrow: 'Your library',
    title: '一個檔案，一次搞定',
    description: '不用手動拆章、不用自己編目錄。丟一個 EPUB 或 TXT 進來，StoryVoice 幫你認出每一章的標題與順序，之後可用書名、作者或原始檔名搜尋書籍。',
    image: '/landing/01-library.jpg',
    imageAlt: '書庫頁面，顯示已匯入的書籍與章節解析結果',
  },
  {
    eyebrow: 'AI narration',
    title: '讀給你聽，而不是幫你讀',
    description: '你主動匯入什麼，StoryVoice 就只處理什麼——不會主動抓取外部平台或你沒授權的內容。生出來的音檔留在你自己的帳號裡，不公開、不外流。',
    image: '/landing/01b-library-reading.jpg',
    imageAlt: '章節閱讀畫面與 AI 朗讀功能入口',
  },
  {
    eyebrow: 'Character voice studio',
    title: '同一個角色，喜怒哀樂都不一樣',
    description: '主角平常講話跟嚇到、生氣時聽起來理應不同。你可以幫每個角色多錄幾種情緒的聲音，沒特別準備的情境就自動接回原本的聲音，不會整段對白都是同一種語氣。',
    image: '/landing/02b-character-voices.jpg',
    imageAlt: '角色管理頁面的聲線工作室，顯示基礎聲線與四種情境聲線',
  },
  {
    eyebrow: 'Series cast',
    title: '整套系列角色卡固定住',
    description: '追同一系列最怕角色聲音一冊一個樣。先把整個卡司定下來、逐章校過台詞，之後每一冊都照同一套聲線產出，中途某一冊出包也不會影響已經完成的版本。',
    image: '/landing/03-series-cast.jpg',
    imageAlt: '多角色系列配音控制台，顯示系列書籍與固定角色聲線',
  },
  {
    eyebrow: 'Collections',
    title: '追更的書歸追更的書',
    description: '手邊同時看好幾套作品時，書冊讓你把同系列的集數排好順序放在一起，也能依已註冊帳號的 email 分享唯讀書冊，不用把自己的帳號借出去。',
    image: '/landing/04b-collections-list.jpg',
    imageAlt: '書冊列表頁面，顯示已建立的書冊卡片',
  },
]

function BrowserFrameShot({ feature, onZoom }: { feature: Feature; onZoom: () => void }) {
  return (
    <button
      aria-label={`放大檢視：${feature.imageAlt}`}
      className="group block w-full overflow-hidden rounded-2xl border border-stone-300 bg-stone-800 text-left shadow-[0_24px_60px_rgba(41,30,10,.28)] transition hover:-translate-y-1 hover:shadow-[0_28px_70px_rgba(41,30,10,.36)]"
      onClick={onZoom}
      type="button"
    >
      <div className="flex items-center gap-1.5 px-4 py-2.5">
        <span className="h-2.5 w-2.5 rounded-full bg-rose-400" />
        <span className="h-2.5 w-2.5 rounded-full bg-amber-300" />
        <span className="h-2.5 w-2.5 rounded-full bg-emerald-400" />
      </div>
      <div className="relative bg-white">
        <img
          alt={feature.imageAlt}
          className="h-72 w-full object-cover object-top sm:h-80"
          loading="lazy"
          src={apiUrl(feature.image)}
        />
        <div className="pointer-events-none absolute inset-0 flex items-center justify-center bg-stone-900/0 transition group-hover:bg-stone-900/25">
          <span className="rounded-full bg-white/95 px-4 py-2 text-xs font-semibold text-stone-800 opacity-0 shadow-lg transition group-hover:opacity-100">
            點擊放大檢視
          </span>
        </div>
      </div>
    </button>
  )
}

function ImageLightbox({ feature, onClose }: { feature: Feature; onClose: () => void }) {
  useEffect(() => {
    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [onClose])

  return (
    <div
      aria-label={feature.imageAlt}
      aria-modal="true"
      className="fixed inset-0 z-50 flex items-center justify-center bg-stone-900/80 p-4 backdrop-blur-sm sm:p-8"
      onClick={onClose}
      role="dialog"
    >
      <button
        aria-label="關閉"
        className="absolute right-4 top-4 rounded-full bg-white/10 px-4 py-2 text-sm text-white transition hover:bg-white/20 sm:right-8 sm:top-8"
        onClick={onClose}
        type="button"
      >
        關閉 ✕
      </button>
      <img
        alt={feature.imageAlt}
        className="max-h-full max-w-full cursor-zoom-out rounded-xl object-contain shadow-2xl"
        onClick={(event) => event.stopPropagation()}
        src={apiUrl(feature.image)}
      />
    </div>
  )
}

export function LandingPage() {
  const [zoomedFeature, setZoomedFeature] = useState<Feature | null>(null)

  return (
    <main className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
      <section className="overflow-hidden rounded-3xl border border-amber-200 bg-gradient-to-br from-amber-50 via-white to-orange-50 p-8 text-center sm:p-14">
        <p className="eyebrow">AI Story Director</p>
        <h1 className="mx-auto mt-4 max-w-3xl font-serif text-4xl leading-tight text-stone-900 sm:text-5xl">
          把你有權閱讀的故事，<span className="text-amber-700">變成一齣有聲演出。</span>
        </h1>
        <p className="mx-auto mt-5 max-w-2xl text-sm leading-7 text-stone-600 sm:text-base">
          StoryVoice 把電子書轉成多角色、具情緒演出的 AI 有聲書：整理書庫、替每個角色設計專屬聲線、
          逐章校正劇本，再交給系列配音一次產出整批音訊。
        </p>
        <div className="mt-8 flex flex-col items-center justify-center gap-3 sm:flex-row">
          <Link className="primary-button" to="/library">
            進入書庫開始
          </Link>
          <Link className="secondary-button" to="/characters">
            先逛逛角色聲線工作室
          </Link>
        </div>
      </section>

      <section className="mt-16 space-y-16">
        {FEATURES.map((feature, index) => (
          <div
            className={`grid grid-cols-1 items-center gap-8 lg:grid-cols-2 ${index % 2 === 1 ? 'lg:[&>*:first-child]:order-2' : ''}`}
            key={feature.title}
          >
            <div>
              <p className="eyebrow">{feature.eyebrow}</p>
              <h2 className="mt-3 font-serif text-2xl text-stone-900 sm:text-3xl">{feature.title}</h2>
              <p className="mt-4 text-sm leading-7 text-stone-600 sm:text-base">{feature.description}</p>
            </div>
            <BrowserFrameShot feature={feature} onZoom={() => setZoomedFeature(feature)} />
          </div>
        ))}
      </section>

      <section className="mt-16 rounded-3xl border border-stone-200 bg-white p-8 text-center sm:p-12">
        <h2 className="font-serif text-2xl text-stone-900 sm:text-3xl">只處理你有權使用的內容</h2>
        <p className="mx-auto mt-3 max-w-2xl text-sm leading-7 text-stone-500">
          StoryVoice is open source. No DRM circumvention. Process only content you have the right to use.
        </p>
        <Link className="primary-button mt-6 inline-flex" to="/library">
          進入書庫
        </Link>
      </section>

      {zoomedFeature && <ImageLightbox feature={zoomedFeature} onClose={() => setZoomedFeature(null)} />}
    </main>
  )
}
