# StoryVoice Web

StoryVoice Web is the React frontend for the self-hosted StoryVoice audiobook-production
platform. It covers the public product and voice-catalog surfaces, authenticated book and
character workflows, series voice casting, and the owner-scoped developer console.

> 繁中摘要：這是 StoryVoice 的 React 前端。公開頁面、書庫、角色聲線、系列配音與開發者
> 控制台都在此專案；修改前請保留 owner scope、CSRF、私有音訊與 token 不進瀏覽器的邊界。

## Stack

- React 19
- TypeScript 6
- Vite 8
- React Router 7
- Tailwind CSS 4
- Oxlint
- Node.js built-in test runner for static contract tests

## Prerequisites

- Node.js 22, matching CI
- npm
- StoryVoice API at `http://localhost:8080` for full end-to-end development

The repository pins the backend SDK separately in [`../../global.json`](../../global.json).

## Install and run

From this directory:

```bash
npm ci
npm run dev
```

Vite serves the frontend locally and proxies `/api` and `/health` to
`http://localhost:8080`. Start the backend from the repository root when API-backed
screens are needed:

```bash
dotnet run --project src/StoryVoice.Api
```

To run the complete stack instead, use `docker compose up --build` from the repository root.

## Scripts

| Command | Purpose |
|---|---|
| `npm run dev` | Start the Vite development server |
| `npm test` | Run the Web contract and static tests |
| `npm run lint` | Run Oxlint |
| `npm run build` | Type-check and create a production build |
| `npm run preview` | Preview the production build locally |

Before opening a pull request, run:

```bash
npm test
npm run lint
npm run build
```

## Main routes

The route assembly is defined in [`src/App.tsx`](src/App.tsx).

| Route | Surface |
|---|---|
| `/about` | Public bilingual product introduction |
| `/` | Sign-in entry, then the authenticated product home |
| `/voices` | Public fixed-demo voice catalog; feature-gated by the backend |
| `/developers/docs` | Public developer documentation |
| `/library` | Authenticated book library and import workflow |
| `/collections` | Owner-scoped book collections |
| `/shared` | Collections shared read-only with the current user |
| `/characters` | Character library and voice studio |
| `/series` | Series cast, speech-plan review, and staged narration |
| `/developer` | Owner-scoped API overview |
| `/developer/credentials` | Managed credential lifecycle |
| `/developer/playground` | Same-origin voice API playground |
| `/developer/usage` | Safe usage and error metadata |

Public route visibility does not grant access to private assets or synthesis. Backend
feature flags, authorization evidence, owner scope, and active profile checks remain the
source of truth.

## Base-path builds

StoryVoice can run at `/` or below a reverse-proxy subpath. Build and preview the production
path with the same environment value so Vite serves both the document and assets below that
path:

```bash
STORYVOICE_BASE_PATH=/StoryVoice/ npm run build
STORYVOICE_BASE_PATH=/StoryVoice/ npm run preview
```

In PowerShell, set `$env:STORYVOICE_BASE_PATH = '/StoryVoice/'` before each command and remove
the variable when the preview is finished.

Use `apiUrl` and React Router links instead of hard-coded root-relative application URLs.
CI builds both the root and `/StoryVoice/` variants.

## Authentication and sensitive data

- Authenticated product pages use the StoryVoice session and CSRF helpers.
- Do not place external API bearer tokens in React state, local storage, bundled code,
  URLs, screenshots, test fixtures, or logs.
- The developer playground calls the same-origin backend-for-frontend; it does not expose
  the server-to-server bearer token to the browser.
- Do not render private reference audio, transcripts, filesystem paths, raw evidence hashes,
  uploaded book text, or another owner's identifiers in public surfaces.
- Public voice playback is limited to explicitly approved fixed-demo assets.

## Localization

The static HTML document uses English as its neutral fallback. Runtime locale handling owns
the active document language and localized product copy. The product is currently most
complete in Traditional Chinese and Taiwan-Mandarin workflows.

When adding UI text:

- keep copy outside domain and API contracts;
- avoid hard-coding `zh-TW` for generic date/number formatting when the active locale is available;
- preserve language-neutral status and error codes from the API;
- update accessible names, alt text, empty states, errors, and metadata together;
- do not claim that a voice, locale, public catalog, or subscription tier is available unless
  the backend reports that state.

## UI and accessibility conventions

- Use the existing warm editorial visual system and shared component classes before adding
  one-off colors or controls.
- Keep one clear primary action per card or panel.
- Preserve keyboard navigation, visible focus, semantic headings, reduced-motion behavior,
  and meaningful loading, empty, disabled, expired, revoked, and error states.
- Use sanitized demo content in screenshots and tests.
- Keep public catalog metadata API-driven; do not hard-code candidate character names as
  production availability.

## Related documentation

- [Root project README](../../README.md)
- [Traditional Chinese README](../../README.zh-TW.md)
- [Verified project status](../../docs/PROJECT_STATUS.md)
- [UI/UX implementation handoff](../../docs/VOICE_PLATFORM_UI_UX_HANDOFF.md)
- [External voice API](../../docs/EXTERNAL_VOICE_API.md)
- [Security policy](../../SECURITY.md)
