import { Link } from 'react-router-dom'

import { LanguageSwitcher } from '../components/LanguageSwitcher'
import { useLocale } from '../i18n'

const ERROR_CODES: Array<[string, string, string, string]> = [
  ['400', 'invalid_request', 'path、JSON、欄位、文字內容或 Idempotency-Key 格式無效', 'Invalid path, JSON shape, field, text, or Idempotency-Key'],
  ['401', 'invalid_api_key', 'token、tier 前綴或 consumer 無效', 'Invalid token, tier prefix, or consumer'],
  ['404', 'voice_not_available', 'voice、授權、素材、owner 或 project 沒有交集', 'The voice, grant, assets, owner, and project do not form a valid intersection'],
  ['409', 'idempotency_conflict', '同一組 Idempotency-Key 已綁定不同的 request', 'The Idempotency-Key is already bound to a different request'],
  ['413', 'request_too_large', '請求內容超過大小上限', 'The request exceeds the body limit'],
  ['415', 'unsupported_media_type', '不是 application/json，或帶有 Content-Encoding', 'The request is not application/json or includes Content-Encoding'],
  ['429', 'rate_limited', '已達驗證前來源／全域防濫用額度或 consumer 速率限制，請依 Retry-After 重試', 'The pre-authentication source or global abuse limit, or the consumer rate limit, was reached; retry after Retry-After'],
  ['503', 'synthesis_unavailable', '合成服務暫時不可用，可依 Retry-After 重試', 'Synthesis is temporarily unavailable; retry after Retry-After'],
]

const CURL_EXAMPLE = `curl --fail-with-body \\
  --request POST \\
  --header "Authorization: Bearer $STORYVOICE_VOICE_TOKEN" \\
  --header "Idempotency-Key: $(uuidgen)" \\
  --header "Content-Type: application/json" \\
  --data '{"voice":"<authorized-alias>","text":"<authorized-text>"}' \\
  --output sample.wav \\
  https://<host>/api/external/v1/speech`

const NODE_EXAMPLE = `// Server-side only. Keep the token in a backend environment variable.
const response = await fetch('https://<host>/api/external/v1/speech', {
  method: 'POST',
  headers: {
    Authorization: \`Bearer \${process.env.STORYVOICE_VOICE_TOKEN}\`,
    'Idempotency-Key': crypto.randomUUID(),
    'Content-Type': 'application/json',
  },
  body: JSON.stringify({ voice: '<authorized-alias>', text: '<authorized-text>' }),
})
if (!response.ok) throw new Error(\`StoryVoice API \${response.status}\`)
const wav = Buffer.from(await response.arrayBuffer())`

const PYTHON_EXAMPLE = `import os, uuid, requests

response = requests.post(
    "https://<host>/api/external/v1/speech",
    headers={
        "Authorization": f"Bearer {os.environ['STORYVOICE_VOICE_TOKEN']}",
        "Idempotency-Key": str(uuid.uuid4()),
        "Content-Type": "application/json",
    },
    json={"voice": "<authorized-alias>", "text": "<authorized-text>"},
    timeout=30,
)
response.raise_for_status()
with open("sample.wav", "wb") as file:
    file.write(response.content)`

const CSHARP_EXAMPLE = `using var client = new HttpClient();
client.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("STORYVOICE_VOICE_TOKEN"));
client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

var payload = JsonContent.Create(new { voice = "<authorized-alias>", text = "<authorized-text>" });
using var response = await client.PostAsync("https://<host>/api/external/v1/speech", payload);
response.EnsureSuccessStatusCode();
await using var wav = await response.Content.ReadAsStreamAsync();`

function CodeBlock({ label, code }: { label: string; code: string }) {
  return (
    <div className="overflow-hidden rounded-2xl border border-stone-200 bg-stone-950">
      <p className="border-b border-white/10 px-4 py-2 text-xs font-semibold uppercase tracking-[.16em] text-stone-400">{label}</p>
      <pre className="overflow-x-auto px-4 py-4 text-xs leading-6 text-stone-100"><code>{code}</code></pre>
    </div>
  )
}

function DocsHeader() {
  const { isEnglish } = useLocale()

  return (
    <header className="relative z-10 border-b border-stone-200/80 bg-[#faf6ee]/90 backdrop-blur">
      <div className="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-4 px-6 py-5 lg:px-10">
        <Link aria-label="StoryVoice" className="group flex items-center gap-3 public-focus rounded-xl" to="/voices">
          <span className="grid h-11 w-11 place-items-center rounded-2xl border border-amber-300 bg-amber-50 font-serif text-lg text-amber-800 shadow-[0_4px_18px_rgba(180,101,15,.14)]">
            SV
          </span>
          <span>
            <strong className="block font-serif text-lg tracking-wide text-stone-900">StoryVoice</strong>
            <span className="block text-[10px] uppercase tracking-[.26em] text-stone-500">Developer docs</span>
          </span>
        </Link>

        <nav aria-label={isEnglish ? 'Public navigation' : '公開頁面導覽'} className="flex flex-wrap items-center gap-2">
          <Link className="rounded-full px-4 py-2 text-sm text-stone-600 hover:bg-white hover:text-stone-900 public-focus" to="/about">{isEnglish ? 'About' : '產品介紹'}</Link>
          <Link className="rounded-full px-4 py-2 text-sm text-stone-600 hover:bg-white hover:text-stone-900 public-focus" to="/voices">{isEnglish ? 'Voice catalog' : '公開聲線館'}</Link>
          <LanguageSwitcher />
          <Link className="secondary-button public-focus" to="/">{isEnglish ? 'Sign in' : '登入 StoryVoice'}</Link>
        </nav>
      </div>
    </header>
  )
}

export function DeveloperDocsPage() {
  const { isEnglish } = useLocale()

  return (
    <div className="relative min-h-screen overflow-hidden bg-[#faf6ee] text-[#332a1f]">
      <div className="ambient ambient-one" aria-hidden="true" />
      <div className="ambient ambient-two" aria-hidden="true" />
      <DocsHeader />

      <main className="relative z-10 mx-auto max-w-5xl px-6 pb-24 pt-12 lg:px-10 lg:pt-16">
        <p className="eyebrow">API reference</p>
        <h1 className="mt-4 max-w-3xl font-serif text-4xl leading-tight text-stone-900 sm:text-5xl">
          {isEnglish ? 'Build with original synthetic character voices' : '跨專案語音合成 API'}
        </h1>
        <p className="mt-5 max-w-2xl text-sm leading-7 text-stone-600 sm:text-base">
          {isEnglish
            ? 'Call StoryVoice from another project\'s backend to turn text into WAV audio with an authorized synthetic voice. StoryVoice accepts owner-created synthetic voices only: no human voice source and no request to imitate an identifiable person.'
            : '讓其他專案的伺服器代碼呼叫 StoryVoice，用已授權的合成聲線把一段文字轉成 WAV 語音。所有聲線均為 owner 自建的原創合成素材，沒有真人聲音來源，也不模仿任何可識別的真人。'}
        </p>

        <div className="mt-8 rounded-2xl border border-amber-200 bg-amber-50 p-6 text-sm leading-7 text-amber-950">
          <strong className="block font-semibold">
            {isEnglish ? 'Private development beta' : '目前為 private-development 早期接用階段。'}
          </strong>
          {isEnglish
            ? 'Signed-in owners can inspect issued projects, expiry, and voices, then create, rotate, or revoke managed API keys. StoryVoice still issues each project and consumer entitlement manually. Private-development access lasts no more than 30 days and is private, non-public, and non-commercial. Subscription-commercial access is not open yet.'
            : '登入後可從開發者控制台查看已核發的專案、效期與聲線，並為既有專案自行建立、換發或撤銷 API 金鑰；每個專案與 consumer entitlement 仍由 StoryVoice 團隊核發，private-development 效期最長 30 天，僅限私人、非公開、非商用用途。公開／商用的 subscription-commercial 存取層仍在準備中。'}
        </div>

        <section className="mt-14" id="tiers">
          <h2 className="font-serif text-2xl text-stone-900">{isEnglish ? 'Access tiers' : '兩種存取層級'}</h2>
          <p className="mt-2 text-sm leading-6 text-stone-600">
            {isEnglish ? 'Tiers never cross. A token with the wrong tier prefix returns 401; the API never falls back to a lower privilege.' : '層級之間互不相通——用錯 tier 的 token 一律回 401，不會退回較低權限。'}
          </p>
          <div className="mt-5 grid gap-4 sm:grid-cols-2">
            <article className="rounded-2xl border border-stone-200 bg-white p-6">
              <span className="public-status-pill" data-status="authorization-pending">
                <span aria-hidden="true" className="public-status-dot" />private-development
              </span>
              <p className="mt-4 text-sm leading-6 text-stone-600">
                {isEnglish ? 'Short-lived, private, non-public, non-commercial access. The token prefix is ' : '短期、私人、非公開、非商用的第一階段接用。token 前綴固定為 '}
                <code className="text-xs">svd1.</code>
                {isEnglish ? ', access lasts no more than 30 days, and each consumer receives one voice.' : '，效期最長 30 天，每個 consumer 只授權一個聲線。'}
              </p>
            </article>
            <article className="rounded-2xl border border-stone-200 bg-white p-6">
              <span className="public-status-pill" data-status="coming-soon">
                <span aria-hidden="true" className="public-status-dot" />subscription-commercial
              </span>
              <p className="mt-4 text-sm leading-6 text-stone-600">
                {isEnglish ? 'Subscription and commercial access requires the full publication and provider-rights chain. The token prefix is ' : '完成公開／商用權利鏈後的訂閱商用接用，token 前綴固定為 '}
                <code className="text-xs">svv1.</code>
                {isEnglish ? '. Applications are not open yet.' : '。尚未開放申請，詳見下方「如何取得存取」。'}
              </p>
            </article>
          </div>
        </section>

        <section className="mt-14" id="authentication">
          <h2 className="font-serif text-2xl text-stone-900">{isEnglish ? 'Authentication' : '驗證'}</h2>
          <p className="mt-2 text-sm leading-6 text-stone-600">
            {isEnglish ? 'Every request requires an issued bearer token. The token is already bound to its tier, so the request never specifies a tier.' : '每個請求都必須帶上你取得的 bearer token；token 已經固定綁定存取層級，不需要另外指定 tier。'}
          </p>
          <pre className="mt-4 overflow-x-auto rounded-2xl border border-stone-200 bg-white p-4 text-xs leading-6 text-stone-800"><code>Authorization: Bearer &lt;your-token&gt;</code></pre>
        </section>

        <section className="mt-14" id="request">
          <h2 className="font-serif text-2xl text-stone-900">Request</h2>
          <p className="mt-2 text-sm leading-6 text-stone-600">
            {isEnglish ? 'The endpoint accepts exactly ' : '單一端點，固定只接受 '}
            <code className="text-xs">voice</code>
            {isEnglish ? ' and ' : ' 與 '}
            <code className="text-xs">text</code>
            {isEnglish ? '. Query strings, Content-Encoding, and unknown or duplicate JSON properties are rejected.' : ' 兩個欄位；不接受查詢字串、Content-Encoding，或未知／重複的 JSON 屬性。'}
          </p>
          <pre className="mt-4 overflow-x-auto rounded-2xl border border-stone-200 bg-white p-4 text-xs leading-6 text-stone-800"><code>{`POST /api/external/v1/speech
Authorization: Bearer <your-token>
Idempotency-Key: <${isEnglish ? '16-64 letters, digits, underscores, or hyphens' : '16-64 個字母、數字、底線或連字號'}>
Content-Type: application/json

{
  "voice": "<authorized-voice-alias>",
  "text": "<authorized-text>"
}`}</code></pre>
          <p className="mt-3 text-xs leading-6 text-stone-500">
            <code className="text-xs">Idempotency-Key</code>
            {isEnglish ? ' makes retrying the same request safe. Reusing the key with a different body returns 409.' : ' 用來安全重試同一次請求；同一把 key 綁定不同 request body 時，第二次呼叫會收到 409。'}
          </p>
        </section>

        <section className="mt-14" id="response">
          <h2 className="font-serif text-2xl text-stone-900">Response</h2>
          <p className="mt-2 text-sm leading-6 text-stone-600">
            {isEnglish ? 'Success returns the ' : '成功時回傳 '}
            <code className="text-xs">audio/wav</code>
            {isEnglish ? ' body with ' : ' 音檔本體，並附上 '}
            <code className="text-xs">private, no-store</code>
            {isEnglish ? ' cache headers. Errors return a JSON problem response.' : ' 快取標頭；失敗一律回傳 JSON 錯誤說明。'}
          </p>
          <div className="mt-4 overflow-x-auto rounded-2xl border border-stone-200 bg-white">
            <table className="w-full text-left text-sm">
              <thead className="border-b border-stone-200 text-xs uppercase tracking-wide text-stone-500">
                <tr><th className="px-4 py-3">HTTP</th><th className="px-4 py-3">code</th><th className="px-4 py-3">{isEnglish ? 'Meaning' : '意義'}</th></tr>
              </thead>
              <tbody className="divide-y divide-stone-100">
                {ERROR_CODES.map(([status, code, meaningZh, meaningEn]) => (
                  <tr key={code}>
                    <td className="px-4 py-3 font-mono text-stone-800">{status}</td>
                    <td className="px-4 py-3 font-mono text-amber-800">{code}</td>
                    <td className="px-4 py-3 text-stone-600">{isEnglish ? meaningEn : meaningZh}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>

        <section className="mt-14" id="examples">
          <h2 className="font-serif text-2xl text-stone-900">{isEnglish ? 'Examples' : '範例'}</h2>
          <p className="mt-2 text-sm leading-6 text-stone-600">
            {isEnglish ? 'Every example runs on a backend and reads the token from a server-side environment variable. Never embed the token in browser code, a URL, a mobile bundle, or a frontend log.' : '全部範例都假設在伺服器端執行，token 來自後端環境變數，絕對不要放進瀏覽器程式碼、URL 或前端 log。'}
          </p>
          <div className="mt-5 grid gap-4">
            <CodeBlock code={CURL_EXAMPLE} label="curl" />
            <CodeBlock code={NODE_EXAMPLE} label={isEnglish ? 'Node.js (server-side)' : 'Node.js（伺服器端）'} />
            <CodeBlock code={PYTHON_EXAMPLE} label="Python" />
            <CodeBlock code={CSHARP_EXAMPLE} label="C#" />
          </div>
        </section>

        <section className="mt-14" id="limits">
          <h2 className="font-serif text-2xl text-stone-900">{isEnglish ? 'Limits and retries' : '限制與重試'}</h2>
          <ul className="mt-4 space-y-3 text-sm leading-6 text-stone-600">
            <li className="rounded-xl border border-stone-200 bg-white px-4 py-3">{isEnglish ? 'The protocol hard limit is 200 Unicode characters and 2,048 UTF-8 bytes. A deployment may configure a lower limit; use the signed-in project page or Playground for the effective value.' : '協定允許的文字硬上限為 200 字元／2,048 UTF-8 bytes；部署可設定得更低，實際值以登入後專案頁或 Playground 顯示為準。'}</li>
            <li className="rounded-xl border border-stone-200 bg-white px-4 py-3">{isEnglish ? 'The protocol hard limit for WAV responses is 3 MiB. A deployment may configure a lower limit.' : '協定允許的 WAV 回應硬上限為 3 MiB；部署可設定得更低。'}</li>
            <li className="rounded-xl border border-stone-200 bg-white px-4 py-3">
              {isEnglish ? 'External speech requests first pass source and global pre-authentication abuse limits, then a per-consumer limit after successful authentication. Playground and external API calls share that consumer allowance. After 429, wait for the ' : 'External speech POST 會先通過來源／全域防濫用額度；驗證成功後再按 consumer 計算，Playground 與 external API 會共同消耗同一 consumer 的每分鐘額度。收到 429 時請依 '}
              <code className="text-xs">Retry-After</code>
              {isEnglish ? ' duration before retrying.' : ' 標頭等待再重試。'}
            </li>
            <li className="rounded-xl border border-stone-200 bg-white px-4 py-3">{isEnglish ? 'The service currently supports one API process. Shared multi-replica rate limiting, single-flight, and idempotency coordination are not available yet.' : '目前僅支援單一 API process；尚未提供跨 replica 的共用速率限制、single-flight 或冪等協調。'}</li>
          </ul>
        </section>

        <section className="mt-14" id="access">
          <h2 className="font-serif text-2xl text-stone-900">{isEnglish ? 'Getting access' : '如何取得存取'}</h2>
          <p className="mt-3 max-w-2xl text-sm leading-7 text-stone-600">
            {isEnglish
              ? 'StoryVoice currently issues private-development projects and entitlements manually. Signed-in owners can inspect project expiry, create, rotate, or revoke managed keys, use the owner-session Playground, and review safe usage records. Self-service project applications are not available yet. Contact the StoryVoice maintainer with the intended private use and voice. Subscription-commercial applications will open only after the publication, provider-rights, and plan workflows are ready.'
              : '目前 private-development 專案與 entitlement 仍由 StoryVoice 團隊核發。登入後可查看專案與效期，並在 API 金鑰頁自行建立、輪替或撤銷受管金鑰，也能使用 owner-session Playground 與 usage 查詢；自助申請尚未提供。如需新的短期私人專案，請直接聯繫 StoryVoice 團隊說明用途與想使用的聲線；subscription-commercial 存取要等公開／商用權利鏈與方案上線後才會開放申請。'}
          </p>
        </section>
      </main>

      <footer className="relative z-10 px-6 py-8 text-center text-xs leading-6 text-stone-500">
        {isEnglish ? 'StoryVoice synthetic voice API. Use only within a valid, unexpired grant.' : 'StoryVoice AI 合成聲線 API。僅限有效、未過期授權範圍內使用。'}
      </footer>
    </div>
  )
}
