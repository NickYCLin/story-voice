import product from './product-overview.json' with { type: 'json' }

export { product }
export type ProductLocale = keyof typeof product.locales

export function productMarkdown(locale: ProductLocale) {
  const copy = product.locales[locale]
  const en = locale === 'en'
  const sections = [
    `# ${product.name}\n\n> ${copy.summary}`,
    `${en ? 'Last updated' : '最後更新'}: ${product.updatedAt}\n\n${copy.highlights.join(' · ')}`,
    `## ${copy.audienceHeading}\n\n${copy.audiences.map(item => `### ${item.title}\n\n${item.description}`).join('\n\n')}`,
    `## ${copy.workflowHeading}\n\n${copy.steps.map((item, index) => `${index + 1}. **${item.title}** — ${item.description}`).join('\n')}`,
    `## ${copy.featureHeading}\n\n${copy.features.map(item => `### ${item.title}\n\n${item.description}`).join('\n\n')}`,
    `## ${copy.factsHeading}\n\n${copy.facts.map(item => `- **${item.label}**: ${item.value}`).join('\n')}`,
    `## ${copy.faqHeading}\n\n${copy.faqs.map(item => `### ${item.question}\n\n${item.answer}`).join('\n\n')}`,
    `## ${en ? 'Source and current status' : '原始碼與實作進度'}\n\n- [GitHub](${product.repository})\n- [${en ? 'Verified project status' : '開發進度'}](${product.repository}/blob/main/docs/PROJECT_STATUS.md)\n- ${en ? 'License' : '授權'}: ${product.license}`,
  ]
  return `${sections.join('\n\n')}\n`
}
