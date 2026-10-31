// Development-only browser fixture. No real credentials, content, APIs or mutations.
import { createRoot } from 'react-dom/client'
import { HashRouter } from 'react-router-dom'
import App from '../../src/App'
import { LocaleProvider } from '../../src/i18n'
import { AudioPlayer } from '../../src/components/AudioPlayer'
import { ConfirmDialog } from '../../src/components/ConfirmDialog'
import { useState } from 'react'
import '../../src/index.css'

const wav = new ArrayBuffer(44 + 24000 * 12 * 2)
const view = new DataView(wav)
function ascii(offset:number,text:string){[...text].forEach((c,i)=>view.setUint8(offset+i,c.charCodeAt(0)))}
ascii(0,'RIFF');view.setUint32(4,wav.byteLength-8,true);ascii(8,'WAVEfmt ');view.setUint32(16,16,true);view.setUint16(20,1,true);view.setUint16(22,1,true);view.setUint32(24,24000,true);view.setUint32(28,48000,true);view.setUint16(32,2,true);view.setUint16(34,16,true);ascii(36,'data');view.setUint32(40,wav.byteLength-44,true)
for(let i=0;i<24000*12;i++)view.setInt16(44+i*2,Math.round(800*Math.sin(2*Math.PI*220*i/24000)),true)
const toneUrl=URL.createObjectURL(new Blob([wav],{type:'audio/wav'}))
const params = new URLSearchParams(location.search)
const scenario = params.get('scenario') ?? 'ready'
const delay = Number(params.get('delay') ?? '1200')
const books = Array.from({length:26}, (_,i) => ({id:`book-${i+1}`, title:`旅途札記 ${String(i+1).padStart(2,'0')}`, author:'合成測試作者', language:'zh-TW', fileType:'txt', status:'Ready', chapterCount:9, createdAt:'2026-01-01T00:00:00Z', contentBookId:null, authorizedTextAvailable:true}))
const characters = Array.from({length:100}, (_,i) => ({id:`character-${i+1}`,canonicalName:`測試角色 ${String(i+1).padStart(3,'0')}`,hasAvatar:false,isActive:true,createdAt:'2026-01-01',updatedAt:'2026-01-01'}))
window.fetch = async (input, init) => {
  const path = String(input).split('?')[0].replace(/\/$/,'')
  if (init?.method && init.method !== 'GET') return Response.json({detail:'驗證頁不執行寫入'}, {status:405})
  await new Promise(resolve=>setTimeout(resolve,delay))
  if (path.endsWith('/auth/session')) return Response.json({authenticated:scenario!=='anonymous',email:'layout@example.test',csrfToken:'synthetic-only'})
  if(scenario==='error') return Response.json({detail:'合成連線錯誤'}, {status:503})
  if(path.endsWith('/api/books')) return Response.json(scenario==='empty'?[]:books)
  const match=path.match(/\/api\/books\/(book-\d+)$/)
  if(match) return Response.json({...books.find(b=>b.id===match[1]),originalFileName:'synthetic.txt',chapters:Array.from({length:9},(_,i)=>({id:`chapter-${i}`,chapterNumber:i+1,sortOrder:i,title:`第 ${i+1} 章：途中`,originalText:'這是原創合成測試文字。'.repeat(40)}))})
  const trackMatch=path.match(/\/api\/books\/(book-\d+)\/narrations\/tracks$/)
  if(trackMatch)return Response.json({bookId:trackMatch[1],revision:0,selectedJobId:null,tracks:[]})
  if(path.endsWith('/api/character-profiles')) return Response.json(scenario==='empty'?[]:characters)
  if(path.endsWith('/character-analysis')) return Response.json({}, {status:404})
  if(path.endsWith('/local-clone-preview')) return Response.json({available:false,label:null})
  return Response.json([])
}

const targets = ['.workspace-header','.workspace-page-heading','.library-filters','.library-detail','.character-detail','.audio-player','.audio-player input[aria-label="播放進度"]','.voice-catalog-collection']
let geometry: Record<string,{minTop:number,maxTop:number,minHeight:number,maxHeight:number}>={}
let cls=0
new PerformanceObserver(list=>{for(const e of list.getEntries() as (PerformanceEntry & {hadRecentInput:boolean,value:number})[]) if(!e.hadRecentInput)cls+=e.value}).observe({type:'layout-shift',buffered:true})
export function QA() {
 const [open,setOpen]=useState(false)
 const [timeline,setTimeline]=useState(false)
 return <details style={{position:'fixed',bottom:0,right:0,zIndex:100,background:'white',border:'1px solid #bbb',fontSize:12,maxWidth:340,maxHeight:240,overflow:'auto'}}><summary>本機驗證 · 合成資料 · {scenario}</summary>
 <button onClick={()=>{geometry={};cls=0}}>重設量測</button> · <button onClick={()=>setOpen(true)}>測試確認視窗</button> · <button onClick={()=>setTimeline(!timeline)}>切換時間軸</button>
 <pre id="layout-measurements" style={{whiteSpace:'pre-wrap'}} />
 <ConfirmDialog open={open} title="合成測試確認" description="只測試焦點與版面，不改變資料。" onCancel={()=>setOpen(false)} onConfirm={()=>setOpen(false)} />
 {params.has('player')&&<div style={{position:'fixed',top:100,left:16,right:16,maxWidth:600,zIndex:99}}><LocaleProvider><AudioPlayer title="合成測試音訊" src={toneUrl} storageKey="layout-fixture" timeline={timeline?{jobId:'synthetic',textAvailable:true,chapters:[{chapterId:'one',sortOrder:0,title:'第一章',startMs:0}],turns:[{index:0,startMs:0,durationMs:60000,chapterSortOrder:0,kind:'narrator',characterId:null,characterName:null,text:'用長短不同的句子檢查播放器控制位置。'.repeat(12)}]}:null} /></LocaleProvider></div>}
 </details>
}
setInterval(()=>{
 const out=document.getElementById('layout-measurements');if(!out)return
 for(const selector of targets){const e=document.querySelector(selector);if(!e)continue;const r=e.getBoundingClientRect();const old=geometry[selector]??{minTop:r.top,maxTop:r.top,minHeight:r.height,maxHeight:r.height};geometry[selector]={minTop:Math.min(old.minTop,r.top),maxTop:Math.max(old.maxTop,r.top),minHeight:Math.min(old.minHeight,r.height),maxHeight:Math.max(old.maxHeight,r.height)}}
 out.textContent=JSON.stringify({viewport:[innerWidth,innerHeight],horizontalOverflow:Math.max(0,document.documentElement.scrollWidth-document.documentElement.clientWidth),cls,geometry},null,2)
},100)
createRoot(document.getElementById('qa')!).render(<QA />)
createRoot(document.getElementById('root')!).render(<LocaleProvider><HashRouter><App /></HashRouter></LocaleProvider>)
