import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'
import { productContent } from './build/productContent.ts'

const configuredBase = (process.env.STORYVOICE_BASE_PATH ?? '/').trim()
const normalizedBase = configuredBase.replace(/^\/+|\/+$/g, '')

export default defineConfig({
  base: normalizedBase ? `/${normalizedBase}/` : '/',
  plugins: [react(), tailwindcss(), productContent()],
  server: {
    proxy: {
      '/api': 'http://localhost:8080',
      '/health': 'http://localhost:8080',
    },
  },
})
