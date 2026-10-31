# Contributing to StoryVoice

Thanks for helping build a dependable open-source AI Story Director.

## Before opening a pull request

1. Open or reference an issue for substantial behavior changes.
2. Keep provider-specific code behind an interface.
3. Never commit API keys, uploaded books, generated audio or user data.
4. Do not add DRM circumvention features.
5. Add tests for behavior changes.

## Verification

```bash
dotnet build StoryVoice.sln
dotnet test StoryVoice.sln
cd src/StoryVoice.Web
npm ci
npm run lint
npm run build
```

For runtime changes, also run `docker compose up --build` and verify `/health/ready` plus the web UI.

## Commit style

Use Conventional Commits:

```text
<type>(<scope>): <subject>

<body>

<footer>
```

- Use `feat`, `fix`, `docs`, `style`, `refactor`, `test`, or `chore` as the `type`.
- The optional `scope` should identify the affected area, such as `series`,
  `narration`, or `web`.
- Maintainer-authored subjects normally use direct, present-tense Traditional Chinese,
  stay within 50 characters, and omit the final period.
- International contributors may discuss issues and pull requests, write code, tests,
  comments, and documentation, and describe changes in English. If you cannot write
  Traditional Chinese, use a clear English Conventional Commit subject; maintainers may
  normalize the final squash title before merge.
- Separate the subject and body with a blank line. Explain what changed and why, keeping
  body lines within 100 characters where practical.
- Use `Closes #123` in the footer for an issue and `BREAKING CHANGE:` for an incompatible change.

Traditional Chinese example:

```text
feat(series): 新增系列配音管理 API

讓系列、冊次、角色與 alias 都經過 owner 篩選與 CSRF 驗證，
並限制只能選擇伺服器允許的聲線，避免任意 provider ID 進入持久層。
```

English example:

```text
fix(web): preserve the configured base path in voice demo links

Route public demo requests through the shared URL helper so deployments below
/StoryVoice/ do not fall back to the site root.
```

Traditional Chinese reference: [Git Commit Message 規範](https://ithelp.ithome.com.tw/articles/10310628).
