import { useLayoutEffect, useRef } from 'react'
import { useLocation, useNavigationType } from 'react-router-dom'

// Book selections and browser Back preserve position; explicit section navigation starts at the heading.
export function RouteViewport() {
  const { pathname, hash } = useLocation()
  const navigationType = useNavigationType()
  const section = pathname.split('/')[1] ?? ''
  const previous = useRef(section)
  useLayoutEffect(() => {
    if (previous.current !== section && navigationType !== 'POP' && !hash) {
      window.scrollTo({ top: 0, left: 0, behavior: 'instant' })
    }
    previous.current = section
  }, [section, navigationType, hash])
  return null
}
