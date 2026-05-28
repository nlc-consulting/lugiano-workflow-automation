import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Proxy API calls to the NestJS portal-api (matches biostar convention).
    proxy: {
      '/api': 'http://localhost:3000',
    },
  },
})
