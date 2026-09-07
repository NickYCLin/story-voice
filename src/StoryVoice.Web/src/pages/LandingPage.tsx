import { useState } from 'react'
import { Link } from 'react-router-dom'

import { apiUrl } from '../api'
import { LanguageSwitcher } from '../components/LanguageSwitcher'
import { product, productMarkdown } from '../content/productOverview'
import { localize, useLocale } from '../i18n'
import './LandingPage.css'

function PublicLandingHeader() {
  const { locale } = useLocale()
  const t = (zh: string, en: string) => localize(locale, zh, en)
  return (
    <header className="product-header">
      <Link aria-label={t('StoryVoice 介紹', 'About StoryVoice')} className="product-brand" to="/about">
        <span aria-hidden="true" className="product-brand-mark">SV</span>
        <span>StoryVoice<small>{t('從文字到有聲書', 'From text to audiobook')}</small></span>
      </Link>
      <nav aria-label={t('公開頁面導覽', 'Public navigation')}>
        <a href="#workflow">{t('製作流程', 'How it works')}</a>
        <Link to="/voices">{t('公開聲線', 'Voices')}</Link>
        <Link to="/developers/docs">{t('API 文件', 'API docs')}</Link>
      </nav>
      <div className="product-header-actions">
        <LanguageSwitcher />
        <Link className="secondary-button" to="/">{t('登入', 'Sign in')}</Link>
      </div>
    </header>
  )
}

export function LandingPage({ publicMode = false }: { publicMode?: boolean }) {
  const { locale } = useLocale()
  const copy = product.locales[locale]
  const t = (zh: string, en: string) => localize(locale, zh, en)
  const [copyStatus, setCopyStatus] = useState<{ locale: string; state: 'copied' | 'error' } | null>(null)
  const destination = publicMode ? '/' : '/library'
  const startLabel = t(publicMode ? '開啟工作室' : '進入書庫開始', publicMode ? 'Open the studio' : 'Open your library')

  async function copyIntroduction() {
    try {
      await navigator.clipboard.writeText(productMarkdown(locale))
      setCopyStatus({ locale, state: 'copied' })
    } catch {
      setCopyStatus({ locale, state: 'error' })
    }
  }

  const content = (
    <main className="product-page" id="product-content">
      <section aria-labelledby="product-title" className="product-hero">
        <div className="product-hero-copy">
          <p className="product-kicker">{copy.category}</p>
          <h1 id="product-title">{copy.headline}{locale === 'en' ? ' ' : null}<span>{copy.headlineAccent}</span></h1>
          <p className="product-lede">{copy.summary}</p>
          <div className="product-actions">
            <Link className="primary-button" to={destination}>{startLabel}<span aria-hidden="true">↗</span></Link>
            <a className="product-text-link" href="#workflow">{t('看看怎麼製作', 'See how it works')} <span aria-hidden="true">↓</span></a>
          </div>
          <p className="product-hero-note">{t('先從一章、一位角色開始。工作區需登入。', 'Start with one chapter and one character. Workspace sign-in required.')}</p>
        </div>
        <figure className="product-story">
          <div className="product-story-heading">
            <p>{copy.example.label}</p>
            <h2>{copy.example.title}</h2>
            <span>{copy.example.chapter}</span>
          </div>
          <ol className="product-script">
            {copy.example.lines.map((line, index) => (
              <li key={line.speaker}>
                <span aria-hidden="true" className={`product-speaker product-speaker-${index}`}>{String(index + 1).padStart(2, '0')}</span>
                <div><div className="product-script-meta"><strong>{line.speaker}</strong><span>{line.voice}</span></div><p>{line.text}</p></div>
              </li>
            ))}
          </ol>
          <figcaption>{copy.example.caption}</figcaption>
        </figure>
      </section>

      <ul aria-label={t('產品重點', 'Product highlights')} className="product-highlights">
        {copy.highlights.map(item => <li key={item}><span aria-hidden="true">✦</span>{item}</li>)}
      </ul>

      <section aria-labelledby="workflow-title" className="product-section" id="workflow">
        <div className="product-section-heading"><p className="product-kicker">01 / {t('從這裡開始', 'THE WORKFLOW')}</p><h2 id="workflow-title">{copy.workflowHeading}</h2><p>{copy.workflowIntro}</p></div>
        <ol className="product-steps">
          {copy.steps.map((step, index) => (
            <li key={step.title}>
              <span aria-hidden="true" className="product-step-number">{String(index + 1).padStart(2, '0')}</span>
              <h3>{step.title}</h3><p>{step.description}</p><span className="product-step-result">{step.result}</span>
            </li>
          ))}
        </ol>
      </section>

      <section aria-labelledby="features-title" className="product-section" id="features">
        <div className="product-section-heading"><p className="product-kicker">02 / {t('製作時用得到的功能', 'PRODUCTION TOOLS')}</p><h2 id="features-title">{copy.featureHeading}</h2><p>{copy.featureIntro}</p></div>
        <div className="product-features">
          {copy.features.map((feature, index) => (
            <article className={`product-feature product-feature-${feature.id}`} key={feature.id}>
              <span aria-hidden="true" className="product-feature-symbol">{['◎', '≋', '↺', '▤'][index]}</span>
              <p className="product-feature-detail">{feature.detail}</p><h3>{feature.title}</h3><p>{feature.description}</p>
            </article>
          ))}
        </div>
      </section>

      <section aria-labelledby="audience-title" className="product-section product-audience" id="audience">
        <div className="product-section-heading"><p className="product-kicker">03 / {t('誰適合使用', 'WHO IT IS FOR')}</p><h2 id="audience-title">{copy.audienceHeading}</h2><p>{copy.audienceIntro}</p></div>
        <div className="product-audience-grid">
          {copy.audiences.map((audience, index) => <article key={audience.title}><span aria-hidden="true">{String(index + 1).padStart(2, '0')}</span><h3>{audience.title}</h3><p>{audience.description}</p></article>)}
        </div>
      </section>

      <section aria-labelledby="facts-title" className="product-section product-facts" id="facts">
        <div className="product-section-heading"><p className="product-kicker">04 / {t('使用前須知', 'THE PRACTICAL DETAILS')}</p><h2 id="facts-title">{copy.factsHeading}</h2></div>
        <dl>{copy.facts.map(fact => <div key={fact.label}><dt>{fact.label}</dt><dd>{fact.value}</dd></div>)}</dl>
      </section>

      <section aria-labelledby="faq-title" className="product-section product-faq" id="faq">
        <div className="product-section-heading"><p className="product-kicker">FAQ</p><h2 id="faq-title">{copy.faqHeading}</h2></div>
        <div>{copy.faqs.map(faq => <details key={faq.question}><summary>{faq.question}<span aria-hidden="true">+</span></summary><p>{faq.answer}</p></details>)}</div>
      </section>

      <section aria-labelledby="read-title" className="product-section product-resources" id="read">
        <div><p className="product-kicker">{t('帶走這份介紹', 'SAVE THE OVERVIEW')}</p><h2 id="read-title">{t('給同事看，也能交給 AI 讀。', 'Share it with your team or AI tools.')}</h2><p>{t('複製完整介紹，或開啟不需登入的純文字版本。用途、流程、特色與限制都在同一份內容裡。', 'Copy the full introduction or open a public text version. Uses, workflow, features, and limitations stay together.')}</p></div>
        <div className="product-resource-controls">
          <button className="primary-button" onClick={() => void copyIntroduction()} type="button">{t('複製產品介紹', 'Copy product overview')}</button>
          <div className="product-resource-links">
            <a href={apiUrl(`/product.${locale}.md`)}>Markdown <span aria-hidden="true">↗</span></a>
            <a href={apiUrl('/product.json')}>JSON <span aria-hidden="true">↗</span></a>
            <a href={apiUrl('/llms.txt')}>llms.txt <span aria-hidden="true">↗</span></a>
          </div>
          <p className="product-copy-status" role="status">{copyStatus?.locale === locale && (copyStatus.state === 'copied' ? t('已複製，可貼到文件或 AI 對話。', 'Copied. Paste it into a document or AI conversation.') : t('瀏覽器無法複製，請開啟 Markdown 版本選取文字。', 'Copy is unavailable. Open the Markdown version to select the text.'))}</p>
        </div>
      </section>

      <section aria-labelledby="start-title" className="product-closing">
        <h2 id="start-title">{t('先選一本書，試做第一章。', 'Pick a book. Try the first chapter.')}</h2>
        <div className="product-actions"><Link className="primary-button" to={destination}>{startLabel}<span aria-hidden="true">↗</span></Link><a className="product-text-link" href={product.repository} rel="noreferrer" target="_blank">{t('查看 GitHub 原始碼', 'Explore the source on GitHub')} ↗</a></div>
      </section>
    </main>
  )

  if (!publicMode) return content
  return (
    <div className="product-public">
      <a className="product-skip" href="#product-content">{t('跳到產品介紹', 'Skip to product overview')}</a>
      <PublicLandingHeader />
      {content}
      <footer className="product-footer"><span>StoryVoice · {t('開源多角色有聲書工作室', 'Open-source audiobook studio')}</span><a href={apiUrl('/llms.txt')}>{t('文字與 AI 閱讀入口', 'Text and AI reading index')}</a><span>MIT · {product.updatedAt}</span></footer>
    </div>
  )
}
