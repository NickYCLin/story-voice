import { readFile, writeFile } from 'node:fs/promises'
import { productMarkdown } from '../src/content/productOverview.ts'

const check = process.argv.includes('--check')
for (const locale of ['zh-TW', 'en']) {
  const target = new URL(`../../../docs/PRODUCT_OVERVIEW.${locale}.md`, import.meta.url)
  const content = productMarkdown(locale)
  if (check) {
    const existing = await readFile(target, 'utf8').catch(() => '')
    if (existing.replaceAll('\r\n', '\n') !== content) {
      console.error(`PRODUCT_OVERVIEW.${locale}.md is out of date. Run npm run docs:product.`)
      process.exitCode = 1
    }
  } else {
    await writeFile(target, content, 'utf8')
  }
}
