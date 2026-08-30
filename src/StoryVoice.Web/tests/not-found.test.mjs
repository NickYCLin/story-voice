import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8')
const page = readFileSync(new URL('../src/pages/NotFoundPage.tsx', import.meta.url), 'utf8')

test('unknown SPA routes render a visible 404 instead of an empty React tree', () => {
  const wildcardRoute = app.indexOf('<Route element={<NotFoundPage />} path="*" />')
  const routesEnd = app.indexOf('</Routes>')

  assert.notEqual(wildcardRoute, -1)
  assert.ok(wildcardRoute < routesEnd)
  assert.match(page, /404 · Page not found/)
  assert.match(page, /<h1[^>]*id="not-found-heading"[^>]*>找不到這個頁面。<\/h1>/)
  assert.match(page, /aria-labelledby="not-found-heading"/)
  assert.match(page, /to="\/"/)
  assert.match(page, /to="\/developers\/docs"/)
})
