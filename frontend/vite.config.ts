import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react-swc';
import tailwindcss from '@tailwindcss/vite';
import path from 'path';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    // Port 3000 matches the registered Keycloak callback URL (http://localhost:3000/callback)
    // and the CORS allowlist in the backend (PR #34).
    port: 3000,
  },
});
