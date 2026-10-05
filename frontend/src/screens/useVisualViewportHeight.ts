import { useEffect } from 'react'

// Keeps `--visual-viewport-height` on <html> equal to the part of the screen
// that is actually visible. The editor is a fixed-height box with its own
// scroller, sized `100dvh`. Safari treats the on-screen keyboard as part of
// that box's viewport correctly, but iOS Brave and Firefox (WKWebView
// wrappers) don't shrink `dvh` for the keyboard. Instead they scroll the
// whole page to reveal the focused field, which pushes the field out of
// sight inside our non-scrolling box. Sizing the box to the visual viewport
// makes the keyboard shrink it, so the inner scroller can keep the field
// visible, and undoing the page-level scroll stops the jump.
export function useVisualViewportHeight() {
  useEffect(() => {
    const viewport = window.visualViewport
    if (!viewport) return

    const root = document.documentElement

    function sync() {
      if (!viewport) return
      root.style.setProperty('--visual-viewport-height', `${viewport.height}px`)
      // The page itself never scrolls here; any offset is the browser
      // revealing a focused field by moving the layout out from under us.
      if (window.scrollY !== 0) window.scrollTo(0, 0)
    }

    sync()
    viewport.addEventListener('resize', sync)
    viewport.addEventListener('scroll', sync)
    return () => {
      viewport.removeEventListener('resize', sync)
      viewport.removeEventListener('scroll', sync)
      root.style.removeProperty('--visual-viewport-height')
    }
  }, [])
}
