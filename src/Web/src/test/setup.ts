import '@testing-library/jest-dom/vitest'
import { afterEach, vi } from 'vitest'
import { cleanup } from '@testing-library/react'

afterEach(() => {
  cleanup()
  localStorage.clear()
})

// jsdom has no layout engine, so it does not implement scrolling APIs.
Element.prototype.scrollIntoView = vi.fn()
