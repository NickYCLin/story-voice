import { useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'

import { apiUrl } from './api'
import { LanguageSwitcher } from './components/LanguageSwitcher'
import { localize, useLocale } from './i18n'

type LoadState = 'idle' | 'loading' | 'ready' | 'error'

type AuthScreenProps = {
  csrfToken: string
  onAuthenticated: () => Promise<void>
}

export function AuthScreen({ csrfToken, onAuthenticated }: AuthScreenProps) {
  const { locale } = useLocale()
  const [mode, setMode] = useState<'login' | 'register'>('login')
  const [state, setState] = useState<LoadState>('idle')
  const [message, setMessage] = useState('')
  const t = (zh: string, en: string) => localize(locale, zh, en)

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = event.currentTarget
    const formData = new FormData(form)
    const email = String(formData.get('email') ?? '').trim()
    const password = String(formData.get('password') ?? '')
    const rememberMe = formData.get('rememberMe') === 'on'
    setState('loading')
    setMessage(mode === 'login'
      ? t('正在登入你的 StoryVoice…', 'Signing you in to StoryVoice…')
      : t('正在建立你的 StoryVoice 帳號…', 'Creating your StoryVoice account…'))

    try {
      const authEndpoint = mode === 'login' ? '/api/auth/login' : '/api/auth/register'
      const response = await fetch(apiUrl(authEndpoint), {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
        body: JSON.stringify({ email, password, rememberMe }),
      })
      if (!response.ok) {
        const problem = await response.json().catch(() => null) as {
          detail?: string
          errors?: Record<string, string[]>
        } | null
        const validationMessage = problem?.errors
          ? Object.values(problem.errors).flat()[0]
          : null
        throw new Error(validationMessage ?? problem?.detail ?? (response.status === 401
          ? t('電子郵件或密碼不正確。', 'The email address or password is incorrect.')
          : t(`登入失敗（${response.status}）`, `Sign-in failed (${response.status}).`)))
      }

      await onAuthenticated()
    } catch (error) {
      setState('error')
      setMessage(error instanceof Error
        ? error.message
        : t('帳號操作失敗，請稍後再試。', 'The account request failed. Try again later.'))
    }
  }

  return (
    <main className="relative grid min-h-screen place-items-center overflow-hidden bg-[#faf6ee] px-5 py-12 text-[#332a1f]">
      <div className="ambient ambient-one" aria-hidden="true" />
      <div className="ambient ambient-two" aria-hidden="true" />
      <LanguageSwitcher className="absolute right-5 top-5 z-20 sm:right-8 sm:top-8" />
      <section className="relative z-10 grid w-full max-w-5xl grid-cols-1 overflow-hidden rounded-[2rem] border border-stone-200 bg-white/90 shadow-2xl shadow-stone-400/20 backdrop-blur-xl lg:grid-cols-[1.05fr_.95fr]">
        <div className="border-b border-stone-200 bg-gradient-to-br from-amber-50/70 to-transparent p-8 sm:p-12 lg:border-b-0 lg:border-r">
          <div className="flex items-center gap-3">
            <span className="grid h-12 w-12 place-items-center rounded-2xl border border-amber-300 bg-amber-50 font-serif text-lg text-amber-800">SV</span>
            <div>
              <strong className="block font-serif text-xl text-stone-900">StoryVoice</strong>
              <span className="text-[10px] uppercase tracking-[.26em] text-stone-500">{t('你的私人故事書庫', 'Your private story library')}</span>
            </div>
          </div>
          <p className="eyebrow mt-14">{t('第一步', 'Step 1')}</p>
          <h1 className="mt-4 max-w-xl font-serif text-4xl leading-tight text-stone-900 sm:text-5xl">
            {t('先登入 StoryVoice，', 'Sign in to StoryVoice, then ')}
            <span className="text-amber-700">{t('再匯入自己的故事。', 'bring in your own stories.')}</span>
          </h1>
          <p className="mt-6 max-w-xl text-sm leading-7 text-stone-600">
            {t(
              '每個帳號都有獨立書庫。StoryVoice 只保存你主動匯入、且有權處理的無 DRM 檔案。',
              'Every account has a private library. StoryVoice only stores DRM-free files that you choose to import and have the right to process.',
            )}
          </p>
          <ol className="mt-10 space-y-4 text-sm text-stone-600">
            <li><span className="mr-3 text-amber-700">01</span>{t('登入或建立 StoryVoice 帳號', 'Sign in or create a StoryVoice account')}</li>
            <li><span className="mr-3 text-orange-700">02</span>{t('準備無 DRM 的 EPUB 或 UTF-8 TXT', 'Prepare an authorized DRM-free EPUB or UTF-8 TXT file')}</li>
            <li><span className="mr-3 text-rose-600">03</span>{t('匯入檔案並檢查解析後的章節', 'Import the file and review the parsed chapters')}</li>
          </ol>
          <div className="mt-8 flex flex-wrap gap-3">
            <Link className="secondary-button public-focus" to="/about">{t('了解 StoryVoice', 'Learn about StoryVoice')}</Link>
            <Link className="secondary-button public-focus" to="/voices">{t('瀏覽公開聲線館', 'Browse public voices')}</Link>
          </div>
        </div>

        <div className="p-8 sm:p-12">
          <div aria-label={t('帳號操作', 'Account action')} className="flex rounded-full border border-stone-200 bg-stone-100 p-1" role="group">
            <button aria-pressed={mode === 'login'} className={`auth-tab ${mode === 'login' ? 'active' : ''}`} onClick={() => { setMode('login'); setMessage('') }} type="button">{t('登入', 'Sign in')}</button>
            <button aria-pressed={mode === 'register'} className={`auth-tab ${mode === 'register' ? 'active' : ''}`} onClick={() => { setMode('register'); setMessage('') }} type="button">{t('建立帳號', 'Create account')}</button>
          </div>
          <h2 className="mt-9 font-serif text-3xl text-stone-900">{mode === 'login' ? t('登入 StoryVoice', 'Sign in to StoryVoice') : t('建立 StoryVoice 帳號', 'Create a StoryVoice account')}</h2>
          <p className="mt-2 text-sm text-stone-500">{mode === 'login' ? t('回到你的個人故事書庫。', 'Return to your private story library.') : t('使用電子郵件建立獨立書庫。', 'Create a private library with your email address.')}</p>

          <form className="mt-8 space-y-5" onSubmit={handleSubmit}>
            <label className="block text-sm text-stone-700">
              {t('電子郵件', 'Email address')}
              <input autoComplete="email" className="auth-input mt-2" name="email" required type="email" />
            </label>
            <label className="block text-sm text-stone-700">
              {t('密碼', 'Password')}
              <input autoComplete={mode === 'login' ? 'current-password' : 'new-password'} className="auth-input mt-2" minLength={10} name="password" required type="password" />
            </label>
            {mode === 'register' && <p className="text-xs leading-6 text-stone-400">{t('至少 10 字元，並包含大小寫英文字母、數字與符號。', 'Use at least 10 characters with uppercase and lowercase letters, a number, and a symbol.')}</p>}
            {mode === 'login' && (
              <label className="flex items-center gap-2 text-xs text-stone-500">
                <input className="h-4 w-4 accent-amber-600" name="rememberMe" type="checkbox" />
                {t('在這台裝置保持登入', 'Keep me signed in on this device')}
              </label>
            )}
            <button className="primary-button w-full disabled:cursor-wait disabled:opacity-60" disabled={state === 'loading'} type="submit">
              {state === 'loading' ? t('請稍候…', 'Please wait…') : mode === 'login' ? t('登入 StoryVoice', 'Sign in to StoryVoice') : t('建立帳號並登入', 'Create account and sign in')}
            </button>
            <p className={`min-h-6 text-sm ${state === 'error' ? 'text-rose-600' : 'text-stone-500'}`} role="status">{message}</p>
          </form>
        </div>
      </section>
    </main>
  )
}
