import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import { useNavigate, useParams } from 'react-router-dom'

import { apiUrl } from '../api'
import { useAuthedOutletContext } from '../authOutletContext'
import { BookInsightsPanel } from '../BookInsightsPanel'
import {
  filterAndSortBooks,
  type LibraryCatalogFilters,
} from '../libraryCatalog'
import LibraryStatusMatrix from '../LibraryStatusMatrix'
import { NarrationPanel } from '../NarrationPanel'
import type { BookDetails, BookSummary } from '../types'

type LoadState = 'idle' | 'loading' | 'ready' | 'error'

const defaultCatalogFilters: LibraryCatalogFilters = {
  query: '',
  sort: 'created-desc',
}

export function LibraryPage() {
  const { csrfToken } = useAuthedOutletContext()
  const { bookId: routeBookId } = useParams<{ bookId?: string }>()
  const navigate = useNavigate()

  const [books, setBooks] = useState<BookSummary[]>([])
  const [libraryState, setLibraryState] = useState<'loading' | 'ready' | 'error'>('loading')
  const [selectedBook, setSelectedBook] = useState<BookDetails | null>(null)
  const [detailState, setDetailState] = useState<LoadState>('idle')
  const [uploadState, setUploadState] = useState<LoadState>('idle')
  const [uploadMessage, setUploadMessage] = useState('')
  const [catalogFilters, setCatalogFilters] = useState<LibraryCatalogFilters>(defaultCatalogFilters)

  const visibleBooks = useMemo(
    () => filterAndSortBooks(books, catalogFilters),
    [books, catalogFilters],
  )
  const hasCatalogFilters = catalogFilters.query !== ''
    || catalogFilters.sort !== 'created-desc'

  const loadLibrary = useCallback(async (signal?: AbortSignal) => {
    setLibraryState('loading')
    try {
      const response = await fetch(apiUrl('/api/books'), { signal, credentials: 'same-origin' })
      if (!response.ok) throw new Error(`API returned ${response.status}`)
      const items = await response.json() as BookSummary[]
      setBooks(items)
      setLibraryState('ready')
      return items
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') return null
      setLibraryState('error')
      return null
    }
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    void loadLibrary(controller.signal)
    return () => controller.abort()
  }, [loadLibrary])

  useEffect(() => {
    if (libraryState !== 'ready' || books.length === 0) return
    if (visibleBooks.length === 0) return
    // 只有這本書「真的不存在」（例如已刪除）才重導到第一本可見書；
    // 只是被目前的篩選條件濾出側欄時要保留使用者的選書，
    // 否則輸入關鍵字或匯入後套用篩選都會被搶走選擇。
    if (!routeBookId || !books.some((book) => book.id === routeBookId)) {
      navigate(`/library/${visibleBooks[0].id}`, { replace: true })
    }
  }, [books, libraryState, navigate, routeBookId, visibleBooks])

  useEffect(() => {
    if (!routeBookId) {
      setSelectedBook(null)
      setDetailState('idle')
      return
    }

    const controller = new AbortController()
    setDetailState('loading')
    fetch(apiUrl(`/api/books/${routeBookId}`), {
      signal: controller.signal,
      credentials: 'same-origin',
    })
      .then((response) => {
        if (!response.ok) throw new Error(`API returned ${response.status}`)
        return response.json() as Promise<BookDetails>
      })
      .then((book) => {
        setSelectedBook(book)
        setDetailState('ready')
      })
      .catch((error: unknown) => {
        if (error instanceof DOMException && error.name === 'AbortError') return
        setDetailState('error')
      })

    return () => controller.abort()
  }, [routeBookId])

  async function handleUpload(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    const form = event.currentTarget
    const formData = new FormData(form)
    const file = formData.get('file')
    if (!(file instanceof File) || file.size === 0) {
      setUploadState('error')
      setUploadMessage('請先選擇 EPUB 或 UTF-8 TXT 檔案。')
      return
    }
    if (file.size > 10 * 1024 * 1024) {
      setUploadState('error')
      setUploadMessage('檔案不可超過 10 MiB。')
      return
    }

    setUploadState('loading')
    setUploadMessage('正在解析章節並存入書庫…')
    try {
      const response = await fetch(apiUrl('/api/books/import'), {
        method: 'POST',
        body: formData,
        credentials: 'same-origin',
        headers: { 'X-CSRF-TOKEN': csrfToken },
      })
      if (!response.ok) {
        const problem = await response.json().catch(() => null) as { detail?: string; title?: string } | null
        throw new Error(problem?.detail ?? problem?.title ?? `匯入失敗（${response.status}）`)
      }

      const imported = await response.json() as BookDetails
      await loadLibrary()
      setSelectedBook(imported)
      navigate(`/library/${imported.id}`)
      setDetailState('ready')
      setUploadState('ready')
      setUploadMessage(`「${imported.title}」已匯入，共 ${imported.chapters.length} 章。`)
      form.reset()
    } catch (error) {
      setUploadState('error')
      setUploadMessage(error instanceof Error ? error.message : '匯入失敗，請稍後再試。')
    }
  }

  return (
    <section className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
      <div className="mb-10 flex flex-col justify-between gap-6 sm:flex-row sm:items-end">
        <div>
          <p className="eyebrow">Your library</p>
          <h1 className="mt-3 font-serif text-4xl tracking-tight sm:text-5xl">整理你的故事書庫。</h1>
          <p className="mt-4 max-w-2xl text-sm leading-7 text-stone-600">從任何來源取得、且你有權處理的 EPUB 或文字檔，都可以自行匯入、解析與整理。</p>
        </div>
        <span className="rounded-full border border-stone-200 px-3 py-1 text-xs text-stone-500">書庫已有 {books.length} 本</span>
      </div>

      <form className="mb-6 overflow-hidden rounded-3xl border border-amber-200 bg-gradient-to-br from-amber-50 via-white to-orange-50 p-5 sm:p-7" onSubmit={handleUpload}>
        <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-end">
          <div className="min-w-0">
            <span className="inline-flex rounded-full border border-emerald-300 bg-emerald-50 px-3 py-1 text-xs font-semibold text-emerald-700">推薦方式</span>
            <label className="mt-4 block font-serif text-2xl text-stone-900" htmlFor="book-file">選擇 EPUB 或 TXT</label>
            <p className="mt-2 text-sm leading-7 text-stone-600">書籍來自哪個平台都沒關係，只要準備無 DRM 的 EPUB 或 UTF-8 TXT。選好後按「匯入並解析」，不用填其他欄位。</p>
            <input
              accept=".epub,.txt,application/epub+zip,text/plain"
              className="mt-5 block w-full scroll-mt-6 cursor-pointer rounded-2xl border border-stone-200 bg-white p-2.5 text-sm text-stone-600 file:mr-4 file:rounded-xl file:border-0 file:bg-amber-100 file:px-4 file:py-2.5 file:font-semibold file:text-amber-800 hover:border-amber-300"
              id="book-file"
              name="file"
              required
              type="file"
            />
            <p className={`mt-3 min-h-5 text-xs ${uploadState === 'error' ? 'text-rose-600' : uploadState === 'ready' ? 'text-emerald-700' : 'text-stone-500'}`} role="status">
              {uploadMessage || '支援 EPUB、UTF-8 TXT，最大 10 MiB；請只處理你有權使用的內容。'}
            </p>
          </div>
          <button className="primary-button w-full disabled:cursor-wait disabled:opacity-60 lg:w-auto" disabled={uploadState === 'loading'} type="submit">
            {uploadState === 'loading' ? '正在解析，請稍候…' : '匯入並解析'}
          </button>
        </div>
      </form>

      {libraryState === 'ready' && books.length > 0 && (
        <LibraryStatusMatrix
          refreshKey={books.map((book) => `${book.id}:${book.contentBookId ?? ''}`).join('|')}
        />
      )}

      {libraryState === 'ready' && books.length > 0 && (
        <section aria-label="書庫整理工具" className="mb-6 rounded-3xl border border-stone-200 bg-white p-5 sm:p-6">
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-[minmax(0,1fr)_14rem]">
            <label className="text-xs text-stone-500">
              搜尋書名或作者
              <input
                className="auth-input mt-2"
                onChange={(event) => setCatalogFilters((current) => ({ ...current, query: event.target.value }))}
                placeholder="輸入關鍵字"
                type="search"
                value={catalogFilters.query}
              />
            </label>
            <label className="text-xs text-stone-500">
              排序
              <select className="auth-input mt-2" onChange={(event) => setCatalogFilters((current) => ({ ...current, sort: event.target.value as LibraryCatalogFilters['sort'] }))} value={catalogFilters.sort}>
                <option value="created-desc">最近加入</option>
                <option value="title">書名</option>
                <option value="author">作者</option>
              </select>
            </label>
          </div>
          <div className="mt-5 flex flex-col justify-between gap-3 border-t border-stone-200 pt-4 text-xs text-stone-500 sm:flex-row sm:items-center">
            <span role="status">符合 {visibleBooks.length}／全部 {books.length} 本</span>
            <button className="secondary-button disabled:opacity-40" disabled={!hasCatalogFilters} onClick={() => setCatalogFilters(defaultCatalogFilters)} type="button">清除條件</button>
          </div>
        </section>
      )}

      {libraryState === 'loading' && <div className="library-state">正在連接 StoryVoice API…</div>}
      {libraryState === 'error' && <div className="library-state border-rose-300 text-rose-700">API 尚未連線。請確認後端服務已啟動。</div>}
      {libraryState === 'ready' && books.length === 0 && (
        <div className="library-state min-h-64">
          <div>
            <span className="mx-auto mb-5 grid h-14 w-14 place-items-center rounded-2xl border border-amber-200 bg-amber-50 text-2xl">◇</span>
            <h3 className="font-serif text-2xl text-stone-800">還沒有書，從上面的檔案選擇開始。</h3>
            <p className="mx-auto mt-3 max-w-md text-sm leading-7 text-stone-500">選好 EPUB 或 TXT，再按「匯入並解析」。完成後，書名與章節會出現在這裡。</p>
            <ol className="mx-auto mt-6 grid max-w-md gap-2 text-left text-xs text-stone-500">
              <li><span className="mr-2 text-amber-700">01</span>準備一本你有權處理、無 DRM 的 EPUB 或 UTF-8 TXT</li>
              <li><span className="mr-2 text-amber-700">02</span>選擇檔案並按「匯入並解析」</li>
              <li><span className="mr-2 text-amber-700">03</span>匯入後選書、展開章節檢查解析內容</li>
            </ol>
            <a className="mt-6 inline-flex text-sm font-semibold text-amber-700 transition hover:text-amber-800" href="#book-file">回到選擇檔案 ↑</a>
          </div>
        </div>
      )}
      {libraryState === 'ready' && books.length > 0 && visibleBooks.length === 0 && (
        <div className="library-state min-h-56">
          <div>
            <h3 className="font-serif text-2xl text-stone-800">沒有符合條件的書。</h3>
            <p className="mt-3 text-sm text-stone-500">換一組條件，或清除全部篩選。</p>
            <button className="secondary-button mt-5" onClick={() => setCatalogFilters(defaultCatalogFilters)} type="button">清除條件</button>
          </div>
        </div>
      )}
      {libraryState === 'ready' && visibleBooks.length > 0 && (
        <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,.82fr)_minmax(0,1.18fr)]">
          <div className="space-y-3">
            {visibleBooks.map((book) => (
              <button
                className={`book-card w-full text-left ${routeBookId === book.id ? 'selected-book' : ''}`}
                key={book.id}
                onClick={() => navigate(`/library/${book.id}`)}
                type="button"
              >
                <div className="book-cover">
                  {book.coverImageUrl
                    ? <img alt="" loading="lazy" referrerPolicy="no-referrer" src={book.coverImageUrl} />
                    : <span>{book.title.slice(0, 1)}</span>}
                </div>
                <div className="min-w-0 flex-1">
                  <p className="truncate font-serif text-xl text-stone-900">{book.title}</p>
                  <p className="mt-1 truncate text-sm text-stone-500">{book.author}</p>
                  <div className="mt-7 flex flex-wrap items-center gap-3 text-xs text-stone-400">
                    <span>{book.chapterCount} 章</span><span>·</span><span>{book.fileType.toUpperCase()}</span><span>·</span><span>{book.status}</span>
                  </div>
                </div>
              </button>
            ))}
          </div>

          <aside className="min-h-80 rounded-3xl border border-stone-200 bg-white p-5 sm:p-7">
            {detailState === 'loading' && <div className="library-state h-full">正在展開章節…</div>}
            {detailState === 'error' && <div className="library-state h-full border-rose-300 text-rose-700">章節讀取失敗，請重新選擇書籍。</div>}
            {detailState === 'ready' && selectedBook && (
              <div>
                <div className="flex flex-col justify-between gap-4 border-b border-stone-200 pb-6 sm:flex-row sm:items-start">
                  <div className="min-w-0">
                    <p className="eyebrow">Selected story</p>
                    <h3 className="mt-2 break-words font-serif text-3xl text-stone-900">{selectedBook.title}</h3>
                    <p className="mt-2 text-sm text-stone-500">{selectedBook.author} · {selectedBook.language} · {selectedBook.fileType.toUpperCase()}</p>
                  </div>
                  <span className="shrink-0 rounded-full border border-stone-200 px-3 py-1 text-xs text-stone-500">{selectedBook.chapters.length} 章</span>
                </div>
                <BookInsightsPanel
                  book={selectedBook}
                  csrfToken={csrfToken}
                  key={selectedBook.id}
                />
                <NarrationPanel key={selectedBook.id} book={selectedBook} csrfToken={csrfToken} />
                <div className="mt-5 space-y-3">
                  {selectedBook.chapters.length === 0 && (
                    <div className="library-state min-h-52">
                      <div>
                        <span className="mx-auto mb-4 grid h-12 w-12 place-items-center rounded-2xl border border-amber-200 bg-amber-50 text-amber-700">◇</span>
                        <h4 className="font-serif text-xl text-stone-800">這筆舊書目沒有可處理的正文</h4>
                        <p className="mx-auto mt-3 max-w-md text-sm leading-7 text-stone-500">請另外匯入你有權使用、無 DRM 的 EPUB 或 UTF-8 TXT，再從匯入後的書籍繼續分析與配音。</p>
                        <a className="mt-5 inline-flex text-sm text-amber-700" href="#book-file">前往檔案匯入 ↑</a>
                      </div>
                    </div>
                  )}
                  {selectedBook.chapters.map((chapter) => (
                    <details className="chapter-panel group" key={chapter.id}>
                      <summary className="flex cursor-pointer list-none items-center gap-4 px-4 py-4 text-left">
                        <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-amber-50 font-mono text-xs text-amber-700">{String(chapter.chapterNumber).padStart(2, '0')}</span>
                        <span className="min-w-0 flex-1 truncate font-serif text-lg text-stone-800">{chapter.title}</span>
                        <span className="text-stone-400 transition group-open:rotate-45">＋</span>
                      </summary>
                      <div className="reading-text">{chapter.originalText}</div>
                    </details>
                  ))}
                </div>
              </div>
            )}
          </aside>
        </div>
      )}
    </section>
  )
}
