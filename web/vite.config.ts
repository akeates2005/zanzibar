import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// The API's CORS policy allows exactly one origin, http://localhost:5173, so the dev server and preview both pin it.
export default defineConfig({
  plugins: [react()],
  server: { port: 5173, strictPort: true, host: 'localhost' },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.{ts,tsx}'],
    setupFiles: ['src/test/setup.ts'],
    css: false,
  },
});
