import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const dialog = readFileSync(new URL('../src/components/ConfirmDialog.tsx', import.meta.url), 'utf8')

test('confirmation dialog has unique accessible name and description relationships', () => {
  assert.match(dialog, /const titleId = useId\(\)/)
  assert.match(dialog, /aria-labelledby=\{titleId\}/)
  assert.match(dialog, /aria-describedby=\{description \? descriptionId : undefined\}/)
  assert.match(dialog, /aria-modal="true"/)
})

test('confirmation dialog traps keyboard focus, restores its trigger and handles Escape', () => {
  assert.match(dialog, /destructive \? cancelButton : confirmButton/)
  assert.match(dialog, /event\.key !== 'Tab'/)
  assert.match(dialog, /event\.shiftKey/)
  assert.match(dialog, /event\.key === 'Escape'/)
  assert.match(dialog, /previouslyFocused\?\.focus\(\)/)
})
