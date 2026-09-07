import type { Plugin } from 'vite'
import { product, productMarkdown } from '../src/content/productOverview.ts'

export function productAssets(base: string) {
  const prefix = `/${base.replace(/^\/+|\/+$/g, '')}`.replace(/\/$/, '')
  return {
    'product.zh-TW.md': productMarkdown('zh-TW'),
    'product.en.md': productMarkdown('en'),
    'product.json': `${JSON.stringify(product, null, 2)}\n`,
    'llms.txt': [
      '# StoryVoice',
      '',
      `> ${product.locales.en.summary}`,
      '',
      'These public documents describe the product, not the live availability of a deployment.',
      'Markdown and JSON are generated from the same content as the product page. No sign-in or JavaScript is required to read these files.',
      '',
      '## Product',
      `- [繁體中文產品介紹](${prefix}/product.zh-TW.md): Uses, workflow, features, requirements, and FAQ.`,
      `- [English product overview](${prefix}/product.en.md): Uses, workflow, features, requirements, and FAQ.`,
      `- [Structured product data](${prefix}/product.json): Bilingual content with schema version and update date.`,
      `- [Product page](${prefix}/about): Visual overview.`,
      '',
      '## Technical documentation',
      `- [README](${product.repository}/blob/main/README.md): Setup and architecture.`,
      `- [Project status](${product.repository}/blob/main/docs/PROJECT_STATUS.md): Implemented features and remaining work.`,
      `- [API documentation](${prefix}/developers/docs): Public developer documentation.`,
      '',
    ].join('\n'),
  }
}

export function productContent(): Plugin {
  let base = '/'
  return {
    name: 'storyvoice-product-content',
    configResolved(config) { base = config.base },
    configureServer(server) {
      server.middlewares.use((request, response, next) => {
        const pathname = new URL(request.url ?? '/', 'http://localhost').pathname
        if (!pathname.startsWith(base)) return next()
        const file = pathname.slice(base.length)
        const assets = productAssets(base)
        if (!Object.hasOwn(assets, file)) return next()
        response.setHeader('Content-Type', file.endsWith('.json') ? 'application/json; charset=utf-8' : file.endsWith('.md') ? 'text/markdown; charset=utf-8' : 'text/plain; charset=utf-8')
        response.end(assets[file as keyof typeof assets])
      })
    },
    generateBundle() {
      for (const [fileName, source] of Object.entries(productAssets(base))) {
        this.emitFile({ type: 'asset', fileName, source })
      }
    },
    transformIndexHtml() {
      return [
        { tag: 'link', attrs: { rel: 'describedby', type: 'text/plain', href: `${base}llms.txt` }, injectTo: 'head' },
        { tag: 'link', attrs: { rel: 'alternate', type: 'text/markdown', hreflang: 'zh-TW', href: `${base}product.zh-TW.md`, title: 'StoryVoice 產品介紹' }, injectTo: 'head' },
        { tag: 'link', attrs: { rel: 'alternate', type: 'text/markdown', hreflang: 'en', href: `${base}product.en.md`, title: 'StoryVoice product overview' }, injectTo: 'head' },
        { tag: 'link', attrs: { rel: 'alternate', type: 'application/json', href: `${base}product.json`, title: 'StoryVoice structured product data' }, injectTo: 'head' },
      ]
    },
  }
}
