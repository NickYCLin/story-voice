import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

import { apiUrl } from '../api'
import { LanguageSwitcher } from '../components/LanguageSwitcher'
import { localize, useLocale, type SupportedLocale } from '../i18n'

type Feature = {
  eyebrow: string
  title: string
  description: string
  image: string
  imageAlt: string
}

type FeatureDefinition = {
  eyebrow: { zh: string; en: string }
  title: { zh: string; en: string }
  description: { zh: string; en: string }
  image: string
  imageAlt: { zh: string; en: string }
}

const FEATURE_DEFINITIONS: FeatureDefinition[] = [
  {
    eyebrow: { zh: '你的書庫', en: 'Your library' },
    title: { zh: '一個檔案，一次搞定', en: 'One file, structured from the start' },
    description: {
      zh: '不用手動拆章、不用自己編目錄。丟一個 EPUB 或 TXT 進來，StoryVoice 幫你認出每一章的標題與順序，之後可用書名、作者或原始檔名搜尋書籍。',
      en: 'Import an authorized DRM-free EPUB or UTF-8 TXT file and keep its table of contents, chapter order, and source text organized in one private library.',
    },
    image: '/landing/01-library.jpg',
    imageAlt: {
      zh: '書庫頁面，顯示已匯入的書籍與章節解析結果',
      en: 'Library page showing imported books and parsed chapters',
    },
  },
  {
    eyebrow: { zh: '權利邊界清楚的朗讀', en: 'Rights-aware narration' },
    title: { zh: '讀給你聽，而不是幫你讀', en: 'Private and rights-aware by default' },
    description: {
      zh: '你主動匯入什麼，StoryVoice 就只處理什麼——不會主動抓取外部平台或你沒授權的內容。生出來的音檔留在你自己的帳號裡，不公開、不外流。',
      en: 'StoryVoice processes only the files and text you intentionally provide. Generated audio remains owner-scoped, and the project does not include DRM circumvention.',
    },
    image: '/landing/01b-library-reading.jpg',
    imageAlt: {
      zh: '章節閱讀畫面與 AI 朗讀功能入口',
      en: 'Chapter reader with an AI narration entry point',
    },
  },
  {
    eyebrow: { zh: '角色聲線工作室', en: 'Character voice studio' },
    title: { zh: '同一個角色，喜怒哀樂都不一樣', en: 'Give every side of a character its own voice' },
    description: {
      zh: '主角平常講話跟嚇到、生氣時聽起來理應不同。你可以幫每個角色多錄幾種情緒的聲音，沒特別準備的情境就自動接回原本的聲音，不會整段對白都是同一種語氣。',
      en: 'Maintain a reusable character library, assign a base voice and scene-specific variants, and fall back safely when a scene has no specialized voice.',
    },
    image: '/landing/02b-character-voices.jpg',
    imageAlt: {
      zh: '角色管理頁面的聲線工作室，顯示基礎聲線與四種情境聲線',
      en: 'Character voice studio with a base voice and four scene variants',
    },
  },
  {
    eyebrow: { zh: '系列卡司', en: 'Series cast' },
    title: { zh: '整套系列角色卡固定住', en: 'Keep one consistent cast across a series' },
    description: {
      zh: '追同一系列最怕角色聲音一冊一個樣。先把整個卡司定下來、逐章校過台詞，之後每一冊都照同一套聲線產出，中途某一冊出包也不會影響已經完成的版本。',
      en: 'Lock a cast revision, review the speech plan chapter by chapter, and rebuild staged audio without disturbing the version your listeners already use.',
    },
    image: '/landing/03-series-cast.jpg',
    imageAlt: {
      zh: '多角色系列配音控制台，顯示系列書籍與固定角色聲線',
      en: 'Multi-character series console with books and a consistent voice cast',
    },
  },
  {
    eyebrow: { zh: '書冊', en: 'Collections' },
    title: { zh: '追更的書歸追更的書', en: 'Organize books into shareable collections' },
    description: {
      zh: '手邊同時看好幾套作品時，書冊讓你把同系列的集數排好順序放在一起，也能依已註冊帳號的 email 分享唯讀書冊，不用把自己的帳號借出去。',
      en: 'Group related books, control their order, and share a read-only collection with another registered reader without sharing your account.',
    },
    image: '/landing/04b-collections-list.jpg',
    imageAlt: {
      zh: '書冊列表頁面，顯示已建立的書冊卡片',
      en: 'Collections page showing organized book cards',
    },
  },
]

function localizedFeatures(locale: SupportedLocale): Feature[] {
  return FEATURE_DEFINITIONS.map((feature) => ({
    eyebrow: localize(locale, feature.eyebrow.zh, feature.eyebrow.en),
    title: localize(locale, feature.title.zh, feature.title.en),
    description: localize(locale, feature.description.zh, feature.description.en),
    image: feature.image,
    imageAlt: localize(locale, feature.imageAlt.zh, feature.imageAlt.en),
  }))
}

function BrowserFrameShot({ feature, expandLabel, onZoom }: { feature: Feature; expandLabel: string; onZoom: () => void }) {
  return (
    <button
      aria-label={`${expandLabel}: ${feature.imageAlt}`}
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
            {expandLabel}
          </span>
        </div>
      </div>
    </button>
  )
}

function ImageLightbox({ closeLabel, feature, onClose }: { closeLabel: string; feature: Feature; onClose: () => void }) {
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
        aria-label={closeLabel}
        className="absolute right-4 top-4 rounded-full bg-white/10 px-4 py-2 text-sm text-white transition hover:bg-white/20 sm:right-8 sm:top-8"
        onClick={onClose}
        type="button"
      >
        {closeLabel} ✕
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

function PublicLandingHeader() {
  const { locale } = useLocale()
  const t = (zh: string, en: string) => localize(locale, zh, en)

  return (
    <header className="relative z-10 border-b border-stone-200/80 bg-[#faf6ee]/90 backdrop-blur-xl">
      <div className="mx-auto flex max-w-7xl flex-wrap items-center justify-between gap-4 px-6 py-5 lg:px-10">
        <Link aria-label={t('StoryVoice 介紹', 'About StoryVoice')} className="group flex items-center gap-3" to="/about">
          <span className="grid h-11 w-11 place-items-center rounded-2xl border border-amber-300 bg-amber-50 font-serif text-lg text-amber-800">SV</span>
          <span>
            <strong className="block font-serif text-lg text-stone-900">StoryVoice</strong>
            <span className="block text-[10px] uppercase tracking-[.26em] text-stone-500">{t('AI 故事導演', 'AI Story Director')}</span>
          </span>
        </Link>
        <nav aria-label={t('公開頁面導覽', 'Public navigation')} className="flex flex-wrap items-center justify-end gap-2">
          <Link className="rounded-full px-4 py-2 text-sm text-stone-600 transition hover:bg-white hover:text-stone-900 public-focus" to="/voices">{t('公開聲線', 'Voices')}</Link>
          <Link className="rounded-full px-4 py-2 text-sm text-stone-600 transition hover:bg-white hover:text-stone-900 public-focus" to="/developers/docs">{t('API 文件', 'API docs')}</Link>
          <Link className="secondary-button public-focus" to="/">{t('登入', 'Sign in')}</Link>
          <LanguageSwitcher />
        </nav>
      </div>
    </header>
  )
}

export function LandingPage({ publicMode = false }: { publicMode?: boolean }) {
  const { locale, isEnglish } = useLocale()
  const [zoomedFeature, setZoomedFeature] = useState<Feature | null>(null)
  const t = (zh: string, en: string) => localize(locale, zh, en)
  const features = localizedFeatures(locale)
  const expandLabel = t('點擊放大檢視', 'Open larger preview')
  const closeLabel = t('關閉', 'Close')

  const content = (
    <main className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
      <section className="overflow-hidden rounded-3xl border border-amber-200 bg-gradient-to-br from-amber-50 via-white to-orange-50 p-8 text-center sm:p-14">
        <p className="eyebrow">{publicMode ? t('開源 AI 有聲書工作室', 'Open-source AI audiobook studio') : t('AI 故事導演', 'AI Story Director')}</p>
        <h1 className="mx-auto mt-4 max-w-3xl font-serif text-4xl leading-tight text-stone-900 sm:text-5xl">
          {t('把你有權閱讀的故事，', 'Turn stories you have the right to use ')}
          <span className="text-amber-700">{t('變成一齣有聲演出。', 'into a cast of voices.')}</span>
        </h1>
        <p className="mx-auto mt-5 max-w-3xl text-sm leading-7 text-stone-600 sm:text-base">
          {t(
            'StoryVoice 把電子書轉成多角色、具情緒演出的 AI 有聲書：整理書庫、建立角色表、固定聲線卡司、逐章審核台詞，再產出可安全重建與切換的分階段音訊。',
            'StoryVoice is a self-hosted AI audiobook studio for authorized DRM-free EPUB and TXT files. Organize a private library, build a character bible, cast consistent voices, review every speech plan, and produce staged narration that can be rebuilt safely.',
          )}
        </p>
        <div className="mt-8 flex flex-col items-center justify-center gap-3 sm:flex-row">
          <Link className="primary-button" to={publicMode ? '/' : '/library'}>
            {publicMode ? t('登入 StoryVoice', 'Sign in to StoryVoice') : t('進入書庫開始', 'Open your library')}
          </Link>
          <Link className="secondary-button" to={publicMode ? '/voices' : '/characters'}>
            {publicMode ? t('瀏覽公開聲線', 'Browse public voices') : t('先逛逛角色聲線工作室', 'Open the character voice studio')}
          </Link>
        </div>
        <p className="mx-auto mt-6 max-w-3xl rounded-2xl border border-amber-200 bg-amber-50/80 px-5 py-4 text-left text-xs leading-6 text-amber-950" role="note">
          {t(
            '目前聲線與有聲書製作流程以台灣華語為主；系統採開源、自架與可插拔 provider 架構，但不應解讀為已支援任意語言的語音產製。',
            'Voice and audiobook production are currently Taiwan-Mandarin-first. The open-source, self-hosted architecture supports pluggable providers, but the current product should not be presented as arbitrary-language speech synthesis.',
          )}
        </p>
      </section>

      {publicMode && isEnglish && (
        <p className="mt-6 text-center text-xs leading-6 text-stone-500">
          Product screenshots below currently show the Traditional Chinese production workspace.
        </p>
      )}

      <section className="mt-16 space-y-16">
        {features.map((feature, index) => (
          <div
            className={`grid grid-cols-1 items-center gap-8 lg:grid-cols-2 ${index % 2 === 1 ? 'lg:[&>*:first-child]:order-2' : ''}`}
            key={feature.eyebrow}
          >
            <div>
              <p className="eyebrow">{feature.eyebrow}</p>
              <h2 className="mt-3 font-serif text-2xl text-stone-900 sm:text-3xl">{feature.title}</h2>
              <p className="mt-4 text-sm leading-7 text-stone-600 sm:text-base">{feature.description}</p>
            </div>
            <BrowserFrameShot expandLabel={expandLabel} feature={feature} onZoom={() => setZoomedFeature(feature)} />
          </div>
        ))}
      </section>

      <section className="mt-16 rounded-3xl border border-stone-200 bg-white p-8 text-center sm:p-12">
        <h2 className="font-serif text-2xl text-stone-900 sm:text-3xl">{t('只處理你有權使用的內容', 'Open source, rights-aware by design')}</h2>
        <p className="mx-auto mt-3 max-w-2xl text-sm leading-7 text-stone-500">
          {t(
            'StoryVoice 是開源軟體，不提供 DRM 規避功能。僅處理你擁有或獲得授權使用的內容。',
            'StoryVoice is open source. No DRM circumvention. Process only content you own or have the right to transform.',
          )}
        </p>
        <div className="mt-6 flex flex-wrap items-center justify-center gap-3">
          <Link className="primary-button inline-flex" to={publicMode ? '/' : '/library'}>
            {publicMode ? t('登入 StoryVoice', 'Sign in to StoryVoice') : t('進入書庫', 'Open your library')}
          </Link>
          {publicMode && (
            <a className="secondary-button inline-flex" href="https://github.com/NickYCLin/story-voice" rel="noreferrer" target="_blank">
              {t('在 GitHub 查看原始碼 ↗', 'View the source on GitHub ↗')}
            </a>
          )}
        </div>
      </section>

      {zoomedFeature && <ImageLightbox closeLabel={closeLabel} feature={zoomedFeature} onClose={() => setZoomedFeature(null)} />}
    </main>
  )

  if (!publicMode) return content

  return (
    <div className="relative min-h-screen overflow-hidden bg-[#faf6ee] text-[#332a1f]">
      <div className="ambient ambient-one" aria-hidden="true" />
      <div className="ambient ambient-two" aria-hidden="true" />
      <PublicLandingHeader />
      {content}
      <footer className="relative z-10 border-t border-stone-200 px-6 py-8 text-center text-xs leading-6 text-stone-500">
        {t('開源、自架、聲線 provider 可插拔的多角色有聲書工作室。', 'An open-source, self-hosted audiobook studio with pluggable voice providers.')}
      </footer>
    </div>
  )
}
