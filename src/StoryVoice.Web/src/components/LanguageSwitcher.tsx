import { localize, useLocale, type SupportedLocale } from '../i18n'

const OPTIONS: { locale: SupportedLocale; label: string }[] = [
  { locale: 'zh-TW', label: '繁中' },
  { locale: 'en', label: 'EN' },
]

export function LanguageSwitcher({ className = '' }: { className?: string }) {
  const { locale, setLocale } = useLocale()

  return (
    <div
      aria-label={localize(locale, '介面語言', 'Interface language')}
      className={`inline-flex rounded-full border border-stone-200 bg-white/90 p-1 shadow-sm ${className}`}
      role="group"
    >
      {OPTIONS.map((option) => (
        <button
          aria-pressed={locale === option.locale}
          className={`rounded-full px-3 py-1.5 text-xs font-semibold transition ${locale === option.locale
            ? 'bg-amber-100 text-amber-900'
            : 'text-stone-500 hover:bg-stone-50 hover:text-stone-800'}`}
          key={option.locale}
          lang={option.locale}
          onClick={() => setLocale(option.locale)}
          type="button"
        >
          {option.label}
        </button>
      ))}
    </div>
  )
}
