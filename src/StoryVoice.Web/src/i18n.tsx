import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react'

export type SupportedLocale = 'zh-TW' | 'en'

const LOCALE_STORAGE_KEY = 'storyvoice.locale'

type LocaleContextValue = {
  locale: SupportedLocale
  setLocale: (locale: SupportedLocale) => void
  isEnglish: boolean
  numberLocale: string
  dateLocale: string
}

const LocaleContext = createContext<LocaleContextValue | null>(null)

const parseLocale = (value: string | null | undefined): SupportedLocale | null =>
  value === 'en' || value === 'zh-TW' ? value : null

function initialLocale(): SupportedLocale {
  if (typeof window !== 'undefined') {
    const urlLocale = parseLocale(new URLSearchParams(window.location.search).get('lang'))
    if (urlLocale) return urlLocale

    try {
      const storedLocale = parseLocale(window.localStorage.getItem(LOCALE_STORAGE_KEY))
      if (storedLocale) return storedLocale
    } catch {
      // Storage can be unavailable in privacy-restricted browser contexts.
    }
  }

  const browserLocale = typeof navigator === 'undefined' ? '' : navigator.language
  return browserLocale.toLowerCase().startsWith('zh') ? 'zh-TW' : 'en'
}

// oxlint-disable-next-line react/only-export-components -- The locale helper is part of this provider's public contract.
export const localize = <T,>(locale: SupportedLocale, zh: T, en: T): T =>
  locale === 'en' ? en : zh

export function LocaleProvider({ children }: { children: ReactNode }) {
  const [locale, updateLocale] = useState<SupportedLocale>(initialLocale)

  const setLocale = useCallback((nextLocale: SupportedLocale) => {
    updateLocale(nextLocale)
    if (typeof window !== 'undefined') {
      const url = new URL(window.location.href)
      url.searchParams.set('lang', nextLocale)
      window.history.replaceState(
        window.history.state,
        '',
        `${url.pathname}${url.search}${url.hash}`,
      )
    }
  }, [])

  useEffect(() => {
    document.documentElement.lang = locale
    try {
      window.localStorage.setItem(LOCALE_STORAGE_KEY, locale)
    } catch {
      // The active language still works for this page view when storage is blocked.
    }
  }, [locale])

  const value = useMemo<LocaleContextValue>(() => ({
    locale,
    setLocale,
    isEnglish: locale === 'en',
    numberLocale: locale === 'en' ? 'en-US' : 'zh-TW',
    dateLocale: locale === 'en' ? 'en-US' : 'zh-TW',
  }), [locale, setLocale])

  return <LocaleContext.Provider value={value}>{children}</LocaleContext.Provider>
}

// oxlint-disable-next-line react/only-export-components -- Consumers access the provider through this colocated hook.
export function useLocale(): LocaleContextValue {
  const context = useContext(LocaleContext)
  if (!context) throw new Error('useLocale must be used within LocaleProvider.')
  return context
}
