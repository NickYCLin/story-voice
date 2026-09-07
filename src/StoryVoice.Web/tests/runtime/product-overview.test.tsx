import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'

import { productAssets } from '../../build/productContent'
import { product, productMarkdown } from '../../src/content/productOverview'
import { LocaleProvider } from '../../src/i18n'
import { LandingPage } from '../../src/pages/LandingPage'

beforeEach(() => window.history.replaceState({}, '', '/'))

function openOverview() {
  return render(<LocaleProvider><MemoryRouter><LandingPage publicMode /></MemoryRouter></LocaleProvider>)
}

it.each(['/', '/StoryVoice/'])('產生 %s 部署下可直接讀取的雙語文件與正確索引連結', base => {
  const assets = productAssets(base)
  expect(JSON.parse(assets['product.json'])).toEqual(product)
  for (const locale of ['zh-TW', 'en'] as const) {
    const document = assets[`product.${locale}.md`]
    expect(document).toContain(product.locales[locale].summary)
    for (const fact of product.locales[locale].facts) expect(document).toContain(fact.value)
    for (const faq of product.locales[locale].faqs) expect(document).toContain(faq.answer)
    expect(assets['llms.txt']).toContain(`(${base}product.${locale}.md)`)
  }
  expect(assets['llms.txt']).toContain(`(${base}product.json)`)
  expect(assets['product.zh-TW.md']).not.toContain('<script')
})

it('不需登入即可讀取介紹，複製內容包含流程、功能與使用限制', async () => {
  const user = userEvent.setup()
  const write = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue()
  const fetch = vi.fn()
  vi.stubGlobal('fetch', fetch)
  openOverview()
  expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('把你的故事，做成多角色有聲書。')
  await user.click(screen.getByRole('button', { name: '複製產品介紹' }))
  expect(write).toHaveBeenCalledWith(productMarkdown('zh-TW'))
  expect(screen.getByRole('status').textContent).toContain('已複製')
  expect(fetch).not.toHaveBeenCalled()
  expect(screen.getByRole('link', { name: /Markdown/ }).getAttribute('href')).toBe('/product.zh-TW.md')
})

it('切到英文會同步介紹與文字入口，不沿用繁中的複製結果', async () => {
  const user = userEvent.setup()
  const write = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue()
  openOverview()
  await user.click(screen.getByRole('button', { name: '複製產品介紹' }))
  await user.click(screen.getByRole('button', { name: 'EN', exact: true }))
  expect(screen.getByRole('heading', { level: 1 }).textContent).toContain('a multi-character audiobook')
  expect(screen.getByRole('status').textContent).toBe('')
  expect(screen.getByRole('link', { name: /Markdown/ }).getAttribute('href')).toBe('/product.en.md')
  await user.click(screen.getByRole('button', { name: 'Copy product overview' }))
  expect(write).toHaveBeenLastCalledWith(productMarkdown('en'))
})

it('瀏覽器拒絕剪貼簿時說明替代方式，不顯示複製成功', async () => {
  const user = userEvent.setup()
  vi.spyOn(navigator.clipboard, 'writeText').mockRejectedValue(new DOMException('Denied', 'NotAllowedError'))
  openOverview()
  await user.click(screen.getByRole('button', { name: '複製產品介紹' }))
  expect(screen.getByRole('status').textContent).toContain('請開啟 Markdown')
  expect(screen.getByRole('status').textContent).not.toContain('已複製')
})
