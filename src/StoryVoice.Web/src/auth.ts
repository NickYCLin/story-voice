import { createContext, createElement, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'

import { apiUrl } from './api'

export type AuthSession = {
  authenticated: boolean
  email: string | null
  csrfToken: string
}

export type AuthState =
  | { status: 'loading'; email: null; csrfToken: string }
  | { status: 'error'; email: null; csrfToken: string }
  | { status: 'anonymous'; email: null; csrfToken: string }
  | { status: 'authenticated'; email: string; csrfToken: string }

type SessionController = { authState: AuthState; loadAuthSession: () => Promise<void>; logout: () => Promise<void> }
export const AuthSessionContext = createContext<SessionController | null>(null)

export function AuthSessionProvider({ children }: { children: ReactNode }) {
  const session = useAuthSession()
  return createElement(AuthSessionContext.Provider, { value: session }, children)
}

export function useAuthSession(): SessionController {
  const sharedSession = useContext(AuthSessionContext)
  const [authState, setAuthState] = useState<AuthState>({ status: 'loading', email: null, csrfToken: '' })

  const loadAuthSession = useCallback(async () => {
    try {
      const response = await fetch(apiUrl('/api/auth/session'), { credentials: 'same-origin' })
      if (!response.ok) throw new Error(`Auth API returned ${response.status}`)
      const session = await response.json() as AuthSession
      if (session.authenticated && session.email) {
        setAuthState({ status: 'authenticated', email: session.email, csrfToken: session.csrfToken })
      } else {
        setAuthState({ status: 'anonymous', email: null, csrfToken: session.csrfToken })
      }
    } catch {
      setAuthState({ status: 'error', email: null, csrfToken: '' })
    }
  }, [])

  useEffect(() => {
    if (!sharedSession) void loadAuthSession()
  }, [loadAuthSession, sharedSession])

  const logout = useCallback(async () => {
    if (authState.status !== 'authenticated') return

    const response = await fetch(apiUrl('/api/auth/logout'), {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': authState.csrfToken },
      body: JSON.stringify({}),
    })
    if (!response.ok) return

    setAuthState({ status: 'loading', email: null, csrfToken: '' })
    await loadAuthSession()
  }, [authState, loadAuthSession])

  return sharedSession ?? { authState, loadAuthSession, logout }
}
