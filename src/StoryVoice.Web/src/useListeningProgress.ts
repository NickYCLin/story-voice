import { useCallback, useEffect, useMemo, useState } from 'react'
import { apiUrl } from './api'

export type ListeningProgress = {
  jobId: string
  positionMs: number
  durationMs: number
  version: string | null
  updatedAt: string | null
}

type Status = 'loading' | 'ready' | 'error' | 'conflict'
type Position = { positionMs: number; durationMs: number }

// A session serializes writes and only retains the latest pending position. Its requests may
// finish after navigation, but its subscription is detached when the player unmounts.
class ProgressSession {
  private version: string | null | undefined
  private pending: Position | null = null
  private busy = false
  private conflicted = false
  private retryRequested = false
  private listener: ((status: Status, progress?: ListeningProgress) => void) | null = null

  private readonly jobId: string
  private readonly csrfToken: string

  constructor(jobId: string, csrfToken: string) {
    this.jobId = jobId
    this.csrfToken = csrfToken
  }

  subscribe(listener: (status: Status, progress?: ListeningProgress) => void) {
    this.listener = listener
    void this.flush()
    return () => { this.listener = null }
  }

  save(positionMs: number, durationMs: number) {
    this.pending = { positionMs, durationMs }
    void this.flush()
  }

  retry() {
    if (!this.listener || this.conflicted) return
    if (this.busy) {
      this.retryRequested = true
      return
    }
    void this.flush()
  }

  private async request(method: 'GET' | 'PUT', body?: unknown) {
    const controller = new AbortController()
    const timeout = setTimeout(() => controller.abort(), 10_000)
    try {
      const response = await fetch(apiUrl(`/api/narrations/${this.jobId}/progress`), {
        method,
        credentials: 'same-origin',
        headers: method === 'PUT' ? { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': this.csrfToken } : undefined,
        body: body === undefined ? undefined : JSON.stringify(body),
        keepalive: method === 'PUT',
        signal: controller.signal,
      })
      const progress = response.ok ? await response.json() as ListeningProgress : undefined
      if (progress && (progress.jobId !== this.jobId || !Number.isSafeInteger(progress.positionMs)
        || !Number.isSafeInteger(progress.durationMs) || progress.positionMs < 0
        || progress.positionMs > progress.durationMs
        || (progress.version !== null && typeof progress.version !== 'string'))) {
        throw new Error('Invalid progress response')
      }
      return { ok: response.ok, status: response.status, progress }
    } finally {
      clearTimeout(timeout)
    }
  }

  private async flush() {
    if (this.busy || this.conflicted) return
    this.busy = true
    let writing: Position | null = null
    try {
      if (this.version === undefined) {
        const response = await this.request('GET')
        if (!response.ok) throw new Error('Progress unavailable')
        const progress = response.progress!
        this.version = progress.version
        this.listener?.('ready', progress)
      }
      while (this.pending) {
        writing = this.pending
        this.pending = null
        const response = await this.request('PUT', { ...writing, expectedVersion: this.version })
        if (response.status === 409) {
          this.conflicted = true
          this.pending = null
          this.listener?.('conflict')
          return
        }
        if (!response.ok) throw new Error('Progress save failed')
        const saved = response.progress!
        this.version = saved.version
        writing = null
        this.listener?.('ready')
      }
    } catch {
      // Keep the last unsent position while paused/offline. A newer seek or
      // completed position takes precedence over the request that just failed.
      if (writing && !this.pending) this.pending = writing
      this.listener?.('error')
    } finally {
      this.busy = false
      if (this.retryRequested) {
        this.retryRequested = false
        if (this.listener) void this.flush()
      }
    }
  }
}

export function useListeningProgress(jobId: string, csrfToken: string) {
  const session = useMemo(() => new ProgressSession(jobId, csrfToken), [jobId, csrfToken])
  const [state, setState] = useState<{ session: ProgressSession; status: Status; progress?: ListeningProgress }>({ session, status: 'loading' })
  useEffect(() => {
    const unsubscribe = session.subscribe((status, progress) => {
      setState((current) => ({ session, status, progress: progress ?? (current.session === session ? current.progress : undefined) }))
    })
    const retry = () => session.retry()
    window.addEventListener('online', retry)
    return () => {
      window.removeEventListener('online', retry)
      unsubscribe()
    }
  }, [session])
  const save = useCallback((positionMs: number, durationMs: number) => session.save(positionMs, durationMs), [session])
  return {
    status: state.session === session ? state.status : 'loading' as Status,
    progress: state.session === session ? state.progress : undefined,
    save,
  }
}
