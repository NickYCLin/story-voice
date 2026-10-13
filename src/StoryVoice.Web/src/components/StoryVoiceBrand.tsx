import { Link } from 'react-router-dom'
import { localize, useLocale } from '../i18n'

export function StoryVoiceBrand() {
  const { locale } = useLocale()
  return <Link className="storyvoice-brand public-focus" to="/" aria-label={localize(locale, 'StoryVoice 首頁', 'StoryVoice home')}>
    <span className="storyvoice-brand-mark" aria-hidden="true">SV</span>
    <span><strong>StoryVoice</strong><small>{localize(locale, 'AI 故事導演', 'AI Story Director')}</small></span>
  </Link>
}
