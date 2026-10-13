import { act, fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Link } from 'react-router-dom'
import { expect, it, vi } from 'vitest'
import { RouteViewport } from '../../src/components/RouteViewport'
import { AudioPlayer } from '../../src/components/AudioPlayer'
import { ConfirmDialog } from '../../src/components/ConfirmDialog'
import { LocaleProvider } from '../../src/i18n'

it('only explicit navigation between sections resets window position', async () => {
  const scroll = vi.spyOn(window, 'scrollTo').mockImplementation(()=>{})
  render(<MemoryRouter initialEntries={['/library/one']}><RouteViewport /><Link to="/library/two">切書</Link><Link to="/characters">角色</Link></MemoryRouter>)
  await userEvent.click(screen.getByRole('link',{name:'切書'}))
  expect(scroll).not.toHaveBeenCalled()
  await userEvent.click(screen.getByRole('link',{name:'角色'}))
  expect(scroll).toHaveBeenCalledExactlyOnceWith({top:0,left:0,behavior:'instant'})
})

it('keeps the same media element and reserved passage region when timeline or error arrives', async () => {
  vi.spyOn(HTMLMediaElement.prototype,'pause').mockImplementation(()=>{})
  const view = (timeline: null | {jobId:string,textAvailable:boolean,chapters:[],turns:[]})=><LocaleProvider><AudioPlayer src="/synthetic.wav" timeline={timeline} /></LocaleProvider>
  const {container,rerender}=render(view(null))
  const audio=container.querySelector('audio')
  const passage=screen.getByLabelText('目前朗讀內容')
  rerender(view({jobId:'one',textAvailable:true,chapters:[],turns:[]}))
  expect(container.querySelector('audio')).toBe(audio)
  expect(screen.getByLabelText('目前朗讀內容')).toBe(passage)
  await act(async()=>fireEvent.error(audio!))
  expect(container.querySelector('audio')).toBe(audio)
  expect(screen.getByRole('alert').closest('.audio-player-feedback')).toBeTruthy()
})

it('locks background scroll and restores focus without scrolling after dismissing a dialog', () => {
  const trigger=document.createElement('button');document.body.append(trigger);trigger.focus()
  const focus=vi.spyOn(trigger,'focus')
  document.body.style.overflow='auto'
  const {unmount}=render(<ConfirmDialog open title="確認" onConfirm={()=>{}} onCancel={()=>{}} />)
  expect(document.body.style.overflow).toBe('hidden')
  unmount()
  expect(document.body.style.overflow).toBe('auto')
  expect(focus).toHaveBeenCalledWith({preventScroll:true})
  trigger.remove();document.body.style.overflow=''
})
