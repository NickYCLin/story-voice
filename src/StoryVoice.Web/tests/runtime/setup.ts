import { cleanup } from '@testing-library/react'
import { afterEach, beforeEach } from 'vitest'

beforeEach(() => {
  window.localStorage.clear()
  window.localStorage.setItem('storyvoice.locale', 'zh-TW')
})

afterEach(() => cleanup())
