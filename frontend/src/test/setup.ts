import '@testing-library/jest-dom/vitest';
import { afterEach, beforeEach, vi } from 'vitest';

/**
 * Render-stability harness (DiVoid #273)
 *
 * React detects infinite render loops and logs them to console.error the moment
 * the cascade starts — even if the test process doesn't deadlock.  Vitest treats
 * console.error as informational, so those messages would slip past silently.
 *
 * This spy promotes any render-stability message to a thrown Error, so every test
 * that mounts a component with the bug fails visibly.
 *
 * Convention: every route gets an integration test that mounts the full app shell
 * (router + auth + queryclient + Toaster + sonner).  Those tests exercise the
 * conditions that trigger the cascade, and this spy turns the React warning into a
 * hard failure.  See docs/architecture/frontend-bootstrap.md for the full pattern.
 */

const RENDER_STABILITY_ERRORS = [
  'Maximum update depth exceeded',
  'Cannot update a component',
  'Warning: Cannot update during an existing state transition',
  'Warning: Cannot perform a React state update on an unmounted component',
];

let consoleErrorSpy: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  consoleErrorSpy = vi.spyOn(console, 'error');
});

afterEach(() => {
  const calls = consoleErrorSpy.mock.calls;
  consoleErrorSpy.mockRestore();
  for (const call of calls) {
    const msg = call[0];
    const text = typeof msg === 'string' ? msg : String(msg);
    for (const bug of RENDER_STABILITY_ERRORS) {
      if (text.includes(bug)) {
        throw new Error(`React render-stability error during test:\n${text}`);
      }
    }
  }
});
