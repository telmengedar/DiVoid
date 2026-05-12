import { defineConfig } from 'vitest/config';
import path from 'path';

export default defineConfig({
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
    css: true,
    // Use forks pool so each test file gets its own process, avoiding OOM
    // accumulation from heavy jsdom environments (react-markdown + Radix dialogs).
    pool: 'forks',
    // Vitest 4: execArgv is a top-level test option
    execArgv: ['--max-old-space-size=1536'],
  },
});
