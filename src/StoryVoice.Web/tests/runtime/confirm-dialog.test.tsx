import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { expect, it } from 'vitest'

import { ConfirmDialog } from '../../src/components/ConfirmDialog'

function Example({ destructive = true }) {
  const [open, setOpen] = useState(false)
  return <>
    <button onClick={() => setOpen(true)}>開啟確認</button>
    <ConfirmDialog open={open} title="確認移除" description="這是合成測試項目" destructive={destructive} onConfirm={() => setOpen(false)} onCancel={() => setOpen(false)} />
  </>
}

it('刪除對話框預設聚焦取消，Tab 正反向循環且 Escape 返回觸發按鈕', async () => {
  const user = userEvent.setup()
  render(<Example />)
  const trigger = screen.getByRole('button', { name: '開啟確認' })
  await user.click(trigger)
  const cancel = screen.getByRole('button', { name: '取消' })
  const confirm = screen.getByRole('button', { name: '確定' })
  expect(document.activeElement).toBe(cancel)
  await user.tab({ shift: true })
  expect(document.activeElement).toBe(confirm)
  await user.tab()
  expect(document.activeElement).toBe(cancel)
  await user.tab()
  expect(document.activeElement).toBe(confirm)
  await user.keyboard('{Escape}')
  expect(screen.queryByRole('dialog')).toBeNull()
  expect(document.activeElement).toBe(trigger)
})

it('一般確認預設聚焦確定，送出後還原焦點', async () => {
  const user = userEvent.setup()
  render(<Example destructive={false} />)
  const trigger = screen.getByRole('button', { name: '開啟確認' })
  await user.click(trigger)
  expect(document.activeElement).toBe(screen.getByRole('button', { name: '確定' }))
  await user.keyboard('{Enter}')
  expect(screen.queryByRole('dialog')).toBeNull()
  expect(document.activeElement).toBe(trigger)
})
