import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    include: ['tests/runtime/**/*.test.tsx'],
    setupFiles: ['tests/runtime/setup.ts'],
    pool: 'threads',
    maxWorkers: 1,
    restoreMocks: true,
    unstubGlobals: true,
  },
})
