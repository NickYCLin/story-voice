import { Link } from 'react-router-dom'

export function NotFoundPage() {
  return (
    <main className="relative grid min-h-screen place-items-center overflow-hidden bg-[#faf6ee] px-6 py-16 text-center text-[#332a1f]">
      <div className="ambient ambient-one" aria-hidden="true" />
      <div className="ambient ambient-two" aria-hidden="true" />
      <section className="relative z-10 max-w-xl rounded-3xl border border-stone-200 bg-white/90 p-8 shadow-[0_18px_60px_rgba(96,70,30,.10)] sm:p-12" aria-labelledby="not-found-heading">
        <p className="eyebrow">404 · Page not found</p>
        <h1 className="mt-4 font-serif text-4xl text-stone-900" id="not-found-heading">找不到這個頁面。</h1>
        <p className="mt-4 text-sm leading-7 text-stone-600">
          網址可能已變更，或這個功能目前沒有對應的 StoryVoice 頁面。
        </p>
        <div className="mt-7 flex flex-wrap justify-center gap-3">
          <Link className="primary-button public-focus" to="/">返回 StoryVoice 首頁</Link>
          <Link className="secondary-button public-focus" to="/developers/docs">查看 API 文件</Link>
        </div>
      </section>
    </main>
  )
}
