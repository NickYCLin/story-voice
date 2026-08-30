import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const landingPage = await readFile(new URL('../src/pages/LandingPage.tsx', import.meta.url), 'utf8')

test('landing copy matches the current import and collection-sharing contracts', () => {
  assert.doesNotMatch(landingPage, /博客來|Companion|同步書櫃|擷取式摘要/)
  assert.doesNotMatch(landingPage, /上傳什麼、連結什麼|唯讀連結分享|關鍵字或標籤/)
  assert.match(landingPage, /EPUB 或 TXT/)
  assert.match(landingPage, /已註冊帳號的 email/)
})
