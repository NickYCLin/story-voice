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
import { toBookSummary, type BookDetails, type BookSummary } from '../types'

type LoadState = 'idle' | 'loading' | 'ready' | 'error'

const companionDownloadUrl = `${import.meta.env.BASE_URL}storyvoice-books-companion.zip`
const defaultCatalogFilters: LibraryCatalogFilters = {
  query: '',
  source: 'all',
  layout: 'all',
  tts: 'all',
  sort: 'created-desc',
}

export function LibraryPage() {
  const { csrfToken } = useAuthedOutletContext()
  const { bookId: routeBookId } = useParams<{ bookId?: string }>()
  const navigate = useNavigate()

  const [companionToken, setCompanionToken] = useState('')
  const [companionTokenState, setCompanionTokenState] = useState<LoadState>('idle')
  const [companionTokenMessage, setCompanionTokenMessage] = useState('')
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
    || catalogFilters.source !== 'all'
    || catalogFilters.layout !== 'all'
    || catalogFilters.tts !== 'all'
    || catalogFilters.sort !== 'created-desc'

  const handleBookUpdated = useCallback((updated: BookDetails) => {
    setSelectedBook(updated)
    setBooks((current) => current.map((book) => book.id === updated.id ? toBookSummary(updated) : book))
  }, [])

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

  async function handleIssueCompanionToken() {
    setCompanionToken('')
    setCompanionTokenState('loading')
    setCompanionTokenMessage('正在建立只限書櫃同步的連線金鑰…')
    try {
      const response = await fetch(apiUrl('/api/auth/companion-token'), {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
        body: JSON.stringify({}),
      })
      const body = await response.json().catch(() => null) as {
        accessToken?: string
        expiresAt?: string
        detail?: string
      } | null
      if (!response.ok || !body?.accessToken) {
        throw new Error(body?.detail ?? `建立連線金鑰失敗（${response.status}）`)
      }

      setCompanionToken(body.accessToken)
      setCompanionTokenState('ready')
      const expiresAt = body.expiresAt ? new Date(body.expiresAt).toLocaleString('zh-TW') : '7 天後'
      setCompanionTokenMessage(`請立即複製到 Companion；有效期限至 ${expiresAt}。`)
    } catch (error) {
      setCompanionTokenState('error')
      setCompanionTokenMessage(error instanceof Error ? error.message : '建立連線金鑰失敗。')
    }
  }

  async function handleCopyCompanionToken() {
    if (!companionToken) return
    try {
      await navigator.clipboard.writeText(companionToken)
      setCompanionTokenMessage('連線金鑰已複製；請貼到 Companion。')
    } catch {
      setCompanionTokenMessage('瀏覽器不允許自動複製，請手動選取金鑰。')
    }
  }

  async function handleRevokeCompanionTokens() {
    setCompanionTokenState('loading')
    setCompanionTokenMessage('正在撤銷 Companion 連線金鑰…')
    try {
      const response = await fetch(apiUrl('/api/auth/companion-token/revoke'), {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
        body: JSON.stringify({}),
      })
      if (!response.ok) throw new Error(`撤銷失敗（${response.status}）`)
      setCompanionToken('')
      setCompanionTokenState('ready')
      setCompanionTokenMessage('所有 Companion 連線金鑰已撤銷。')
    } catch (error) {
      setCompanionTokenState('error')
      setCompanionTokenMessage(error instanceof Error ? error.message : '撤銷連線金鑰失敗。')
    }
  }

  return (
    <section className="relative z-10 mx-auto max-w-7xl px-6 py-12 lg:px-10">
      <div className="mb-10 flex flex-col justify-between gap-6 sm:flex-row sm:items-end">
        <div>
          <p className="eyebrow">Your library</p>
          <h1 className="mt-3 font-serif text-4xl tracking-tight sm:text-5xl">整理你的故事書庫。</h1>
          <p className="mt-4 max-w-2xl text-sm leading-7 text-stone-600">搜尋、篩選與排序書庫；更自由的分類請使用書冊，有合法正文的 EPUB／TXT 可直接匯入章節。</p>
        </div>
        <span className="rounded-full border border-stone-200 px-3 py-1 text-xs text-stone-500">書庫已有 {books.length} 本</span>
      </div>

      <form className="mb-6 overflow-hidden rounded-3xl border border-amber-200 bg-gradient-to-br from-amber-50 via-white to-orange-50 p-5 sm:p-7" onSubmit={handleUpload}>
        <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-end">
          <div className="min-w-0">
            <span className="inline-flex rounded-full border border-emerald-300 bg-emerald-50 px-3 py-1 text-xs font-semibold text-emerald-700">推薦方式</span>
            <label className="mt-4 block font-serif text-2xl text-stone-900" htmlFor="book-file">選擇 EPUB 或 TXT</label>
            <p className="mt-2 text-sm leading-7 text-stone-600">只要準備一個無 DRM 的 EPUB 或 UTF-8 TXT。選好後按「匯入並解析」，不用填其他欄位。</p>
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

      <details className="group mb-10 overflow-hidden rounded-2xl border border-stone-200 bg-white">
        <summary className="flex cursor-pointer list-none items-center justify-between gap-4 px-5 py-4 text-sm text-stone-700 sm:px-6">
          <span><strong className="font-semibold text-stone-800">進階：同步博客來書櫃書目</strong><span className="ml-2 text-stone-400">不含受保護內文</span></span>
          <span className="text-stone-400 transition group-open:rotate-45">＋</span>
        </summary>
        <div className="grid grid-cols-1 gap-6 border-t border-stone-200 px-5 py-6 sm:px-6 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-start">
          <div className="min-w-0">
            <p className="text-sm leading-7 text-stone-600">這條路只同步書名、作者、封面、官方閱讀連結，以及頁面明確標示的版型／官方 TTS 狀態；不會把電子書內文匯入 StoryVoice。</p>
            <ol className="mt-4 grid grid-cols-1 gap-2 text-xs text-stone-500 sm:grid-cols-3">
              <li><span className="mr-2 text-orange-600">01</span>登入博客來官方書櫃</li>
              <li><span className="mr-2 text-orange-600">02</span>建立金鑰並貼到 Companion</li>
              <li><span className="mr-2 text-orange-600">03</span>勾選同步後重新整理</li>
            </ol>
            <div className="mt-5 rounded-2xl border border-sky-200 bg-sky-50 p-4">
              <strong className="text-sm text-stone-800">第一次安裝 Companion</strong>
              <ol className="mt-3 space-y-2 text-xs leading-6 text-stone-500">
                <li><span className="mr-2 text-sky-700">1.</span>按「下載 Companion ZIP」，下載後解壓縮。</li>
                <li><span className="mr-2 text-sky-700">2.</span>在 Chrome 網址列輸入 <code className="rounded bg-sky-100 px-1 text-sky-800">chrome://extensions</code>，開啟「開發人員模式」。</li>
                <li><span className="mr-2 text-sky-700">3.</span>按「載入未封裝項目」，選擇剛才解壓縮後的資料夾。</li>
              </ol>
              <p className="mt-3 text-xs leading-5 text-stone-400">Chrome 基於安全規則，這一步必須由你親自確認；安裝後不用提供博客來帳密給 StoryVoice。</p>
              <a className="mt-3 inline-flex text-xs text-sky-700 transition hover:text-sky-800" href="https://github.com/NickYCLin/story-voice/tree/main/extensions/books-com-tw-companion" rel="noreferrer" target="_blank">檢視 Companion 原始碼 ↗</a>
            </div>
            <div className="mt-5 rounded-2xl border border-orange-200 bg-orange-50 p-4">
              <div className="flex flex-col justify-between gap-3 sm:flex-row sm:items-center">
                <div>
                  <strong className="text-sm text-stone-800">StoryVoice 連線金鑰</strong>
                  <p className="mt-1 text-xs leading-5 text-stone-500">只允許 Companion 把 metadata 同步到你的帳號；重新建立會撤銷舊金鑰。</p>
                </div>
                <div className="flex shrink-0 flex-wrap gap-2">
                  <button className="secondary-button disabled:cursor-wait disabled:opacity-60" disabled={companionTokenState === 'loading'} onClick={handleIssueCompanionToken} type="button">
                    {companionTokenState === 'loading' ? '處理中…' : companionToken ? '重新建立金鑰' : '建立連線金鑰'}
                  </button>
                  <button className="secondary-button disabled:cursor-wait disabled:opacity-60" disabled={companionTokenState === 'loading'} onClick={handleRevokeCompanionTokens} type="button">撤銷所有金鑰</button>
                </div>
              </div>
              {companionToken && (
                <div className="mt-4 flex flex-col gap-2 sm:flex-row">
                  <input aria-label="只顯示一次的 StoryVoice 連線金鑰" className="auth-input min-w-0 flex-1 font-mono text-xs" readOnly type="text" value={companionToken} />
                  <button className="secondary-button shrink-0" onClick={handleCopyCompanionToken} type="button">複製金鑰</button>
                </div>
              )}
              <p className={`mt-2 min-h-5 text-xs ${companionTokenState === 'error' ? 'text-rose-600' : 'text-stone-500'}`} role="status">{companionTokenMessage || '金鑰只會在建立當下顯示；請貼到 Companion 後再同步。'}</p>
            </div>
          </div>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-3 lg:w-52 lg:grid-cols-1">
            <a className="primary-button text-center" download href={companionDownloadUrl}>下載 Companion ZIP</a>
            <a className="secondary-button text-center" href="https://viewer-ebook.books.com.tw/viewer/index.html?readlist=all" rel="noreferrer" target="_blank">開啟官方書櫃 ↗</a>
            <button className="secondary-button disabled:cursor-wait disabled:opacity-60" disabled={libraryState === 'loading'} onClick={() => void loadLibrary()} type="button">
              {libraryState === 'loading' ? '重新整理中…' : '重新整理書庫'}
            </button>
          </div>
        </div>
      </details>

      {libraryState === 'ready' && books.length > 0 && (
        <LibraryStatusMatrix
          refreshKey={books.map((book) => `${book.id}:${book.contentBookId ?? ''}`).join('|')}
        />
      )}

      {libraryState === 'ready' && books.length > 0 && (
        <section aria-label="書庫整理工具" className="mb-6 rounded-3xl border border-stone-200 bg-white p-5 sm:p-6">
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
            <label className="text-xs text-stone-500 sm:col-span-2 lg:col-span-1">
              搜尋書名、作者或書籍 ID
              <input
                className="auth-input mt-2"
                onChange={(event) => setCatalogFilters((current) => ({ ...current, query: event.target.value }))}
                placeholder="輸入關鍵字"
                type="search"
                value={catalogFilters.query}
              />
            </label>
            <label className="text-xs text-stone-500">
              來源
              <select className="auth-input mt-2" onChange={(event) => setCatalogFilters((current) => ({ ...current, source: event.target.value as LibraryCatalogFilters['source'] }))} value={catalogFilters.source}>
                <option value="all">全部來源</option>
                <option value="books-com-tw">博客來連結</option>
                <option value="uploaded">StoryVoice 匯入</option>
              </select>
            </label>
            <label className="text-xs text-stone-500">
              博客來版型
              <select className="auth-input mt-2" onChange={(event) => setCatalogFilters((current) => ({ ...current, layout: event.target.value as LibraryCatalogFilters['layout'] }))} value={catalogFilters.layout}>
                <option value="all">全部版型</option>
                <option value="Reflowable">流動版</option>
                <option value="Fixed">固定版</option>
                <option value="unknown">未標示／非博客來</option>
              </select>
            </label>
            <label className="text-xs text-stone-500">
              博客來官方 TTS
              <select className="auth-input mt-2" onChange={(event) => setCatalogFilters((current) => ({ ...current, tts: event.target.value as LibraryCatalogFilters['tts'] }))} value={catalogFilters.tts}>
                <option value="all">全部狀態</option>
                <option value="true">可用</option>
                <option value="false">未開放</option>
                <option value="unknown">未標示／非博客來</option>
              </select>
            </label>
            <label className="text-xs text-stone-500">
              排序
              <select className="auth-input mt-2" onChange={(event) => setCatalogFilters((current) => ({ ...current, sort: event.target.value as LibraryCatalogFilters['sort'] }))} value={catalogFilters.sort}>
                <option value="created-desc">最近加入</option>
                <option value="title">書名</option>
                <option value="author">作者</option>
                <option value="synced-desc">最近同步</option>
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
                    <span>{book.chapterCount} 章</span><span>·</span><span>{book.sourceProvider === 'books-com-tw' ? '博客來' : book.fileType.toUpperCase()}</span><span>·</span><span>{book.status === 'Linked' ? '已連結' : book.status}</span>
                    {book.nativeTtsAvailable === true && <><span>·</span><span className="text-emerald-600">官方 TTS</span></>}
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
                    <p className="eyebrow">{selectedBook.sourceProvider === 'books-com-tw' ? 'Books.com.tw linked book' : 'Selected story'}</p>
                    <h3 className="mt-2 break-words font-serif text-3xl text-stone-900">{selectedBook.title}</h3>
                    <p className="mt-2 text-sm text-stone-500">{selectedBook.author} · {selectedBook.language} · {selectedBook.sourceProvider === 'books-com-tw' ? '博客來書櫃' : selectedBook.fileType.toUpperCase()}</p>
                    {selectedBook.sourceProvider === 'books-com-tw' && (
                      <p className="mt-2 text-xs text-stone-400">
                        {selectedBook.ebookLayout === 'Reflowable' ? 'EPUB 流動版型' : selectedBook.ebookLayout === 'Fixed' ? 'EPUB 固定版型' : '版型未標示'}
                        {' · '}
                        {selectedBook.nativeTtsAvailable === true ? '博客來官方 TTS 可用' : selectedBook.nativeTtsAvailable === false ? '博客來官方 TTS 未開放' : '官方 TTS 狀態未標示'}
                      </p>
                    )}
                    {selectedBook.sourceUrl && (
                      <a className="mt-4 inline-flex text-sm text-orange-700 transition hover:text-orange-800" href={selectedBook.sourceUrl} rel="noreferrer" target="_blank">回博客來官方閱讀器 ↗</a>
                    )}
                  </div>
                  <span className="shrink-0 rounded-full border border-stone-200 px-3 py-1 text-xs text-stone-500">{selectedBook.chapters.length} 章</span>
                </div>
                <BookInsightsPanel
                  book={selectedBook}
                  books={books}
                  csrfToken={csrfToken}
                  key={selectedBook.id}
                  onBookUpdated={handleBookUpdated}
                />
                <NarrationPanel key={selectedBook.id} book={selectedBook} csrfToken={csrfToken} />
                <div className="mt-5 space-y-3">
                  {selectedBook.chapters.length === 0 && selectedBook.sourceProvider === 'books-com-tw' && (
                    <div className="library-state min-h-52">
                      <div>
                        <span className="mx-auto mb-4 grid h-12 w-12 place-items-center rounded-2xl border border-orange-200 bg-orange-50 text-orange-700">↗</span>
                        <h4 className="font-serif text-xl text-stone-800">{selectedBook.nativeTtsAvailable === true ? '這本書可用博客來官方 TTS' : '書櫃資料已連結，內文仍在博客來'}</h4>
                        <p className="mx-auto mt-3 max-w-md text-sm leading-7 text-stone-500">{selectedBook.nativeTtsAvailable === true ? '請從上方官方連結開啟博客來 App／閱讀器使用授權朗讀；StoryVoice 不會接收書籍正文。' : 'StoryVoice 不會抓取或解密受保護內容。要進行故事分析，請另外匯入你有權處理的無 DRM EPUB／TXT。'}</p>
                        <a className="mt-5 inline-flex text-sm text-amber-700" href="#book-file">前往合法檔案匯入 ↑</a>
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
