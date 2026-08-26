import { useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'

import { apiUrl } from './api'

type LoadState = 'idle' | 'loading' | 'ready' | 'error'

type AuthScreenProps = {
  csrfToken: string
  onAuthenticated: () => Promise<void>
}

export function AuthScreen({ csrfToken, onAuthenticated }: AuthScreenProps) {
  const [mode, setMode] = useState<'login' | 'register'>('login')
  const [state, setState] = useState<LoadState>('idle')
  const [message, setMessage] = useState('')

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = event.currentTarget
    const formData = new FormData(form)
    const email = String(formData.get('email') ?? '').trim()
    const password = String(formData.get('password') ?? '')
    const rememberMe = formData.get('rememberMe') === 'on'
    setState('loading')
    setMessage(mode === 'login' ? '正在登入你的 StoryVoice…' : '正在建立你的 StoryVoice 帳號…')

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
        throw new Error(validationMessage ?? problem?.detail ?? (response.status === 401 ? '電子郵件或密碼不正確。' : `登入失敗（${response.status}）`))
      }

      await onAuthenticated()
    } catch (error) {
      setState('error')
      setMessage(error instanceof Error ? error.message : '帳號操作失敗，請稍後再試。')
    }
  }

  return (
    <main className="relative grid min-h-screen place-items-center overflow-hidden bg-[#faf6ee] px-5 py-12 text-[#332a1f]">
      <div className="ambient ambient-one" aria-hidden="true" />
      <div className="ambient ambient-two" aria-hidden="true" />
      <section className="relative z-10 grid w-full max-w-5xl grid-cols-1 overflow-hidden rounded-[2rem] border border-stone-200 bg-white/90 shadow-2xl shadow-stone-400/20 backdrop-blur-xl lg:grid-cols-[1.05fr_.95fr]">
        <div className="border-b border-stone-200 bg-gradient-to-br from-amber-50/70 to-transparent p-8 sm:p-12 lg:border-b-0 lg:border-r">
          <div className="flex items-center gap-3">
            <span className="grid h-12 w-12 place-items-center rounded-2xl border border-amber-300 bg-amber-50 font-serif text-lg text-amber-800">SV</span>
            <div>
              <strong className="block font-serif text-xl text-stone-900">StoryVoice</strong>
              <span className="text-[10px] uppercase tracking-[.26em] text-stone-500">Your private story library</span>
            </div>
          </div>
          <p className="eyebrow mt-14">Step 1</p>
          <h1 className="mt-4 max-w-xl font-serif text-4xl leading-tight text-stone-900 sm:text-5xl">先登入 StoryVoice，<span className="text-amber-700">再匯入自己的故事。</span></h1>
          <p className="mt-6 max-w-xl text-sm leading-7 text-stone-600">每個帳號都有獨立書庫。StoryVoice 只保存你主動匯入、且有權處理的無 DRM 檔案。</p>
          <ol className="mt-10 space-y-4 text-sm text-stone-600">
            <li><span className="mr-3 text-amber-700">01</span>登入或建立 StoryVoice 帳號</li>
            <li><span className="mr-3 text-orange-700">02</span>準備無 DRM 的 EPUB 或 UTF-8 TXT</li>
            <li><span className="mr-3 text-rose-600">03</span>匯入檔案並檢查解析後的章節</li>
          </ol>
          <Link className="secondary-button mt-8 public-focus" to="/voices">瀏覽公開聲線館</Link>
        </div>

        <div className="p-8 sm:p-12">
          <div className="flex rounded-full border border-stone-200 bg-stone-100 p-1">
            <button className={`auth-tab ${mode === 'login' ? 'active' : ''}`} onClick={() => { setMode('login'); setMessage('') }} type="button">登入</button>
            <button className={`auth-tab ${mode === 'register' ? 'active' : ''}`} onClick={() => { setMode('register'); setMessage('') }} type="button">建立帳號</button>
          </div>
          <h2 className="mt-9 font-serif text-3xl text-stone-900">{mode === 'login' ? '登入 StoryVoice' : '建立 StoryVoice 帳號'}</h2>
          <p className="mt-2 text-sm text-stone-500">{mode === 'login' ? '回到你的個人故事書庫。' : '使用電子郵件建立獨立書庫。'}</p>

          <form className="mt-8 space-y-5" onSubmit={handleSubmit}>
            <label className="block text-sm text-stone-700">
              電子郵件
              <input autoComplete="email" className="auth-input mt-2" name="email" required type="email" />
            </label>
            <label className="block text-sm text-stone-700">
              密碼
              <input autoComplete={mode === 'login' ? 'current-password' : 'new-password'} className="auth-input mt-2" minLength={10} name="password" required type="password" />
            </label>
            {mode === 'register' && <p className="text-xs leading-6 text-stone-400">至少 10 字元，並包含大小寫英文字母、數字與符號。</p>}
            {mode === 'login' && (
              <label className="flex items-center gap-2 text-xs text-stone-500">
                <input className="h-4 w-4 accent-amber-600" name="rememberMe" type="checkbox" />
                在這台裝置保持登入
              </label>
            )}
            <button className="primary-button w-full disabled:cursor-wait disabled:opacity-60" disabled={state === 'loading'} type="submit">
              {state === 'loading' ? '請稍候…' : mode === 'login' ? '登入 StoryVoice' : '建立帳號並登入'}
            </button>
            <p className={`min-h-6 text-sm ${state === 'error' ? 'text-rose-600' : 'text-stone-500'}`} role="status">{message}</p>
          </form>
        </div>
      </section>
    </main>
  )
}
