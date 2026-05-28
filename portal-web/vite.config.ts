import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // Portal API (NestJS): auth, users, portal data.
      '/api': 'http://localhost:3000',
      // Workflow API (.NET): read-only workflow/case data from WorkflowAutomation.
      '/workflow-api': {
        target: 'http://localhost:5100',
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/workflow-api/, ''),
      },
    },
  },
})
