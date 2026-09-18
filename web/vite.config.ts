import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath, URL } from 'node:url'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  build: {
    // Straight into the host's web root. The interface and the API are served
    // from the same origin, which is what lets authentication stay a cookie
    // rather than a token in browser storage. See ADR-0006.
    outDir: '../src/EnterpriseObservatory.Host.AllInOne/wwwroot',
    emptyOutDir: true,
  },
  server: {
    // In development the SPA runs on its own port and the API is proxied, so
    // the browser still sees one origin and cookie behaviour matches
    // production. Developing against a different origin would mean discovering
    // every same-origin assumption at deployment time.
    proxy: {
      '/api': { target: 'http://localhost:5219', changeOrigin: false },
    },
  },
})
