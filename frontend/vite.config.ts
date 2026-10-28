import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const apiProxy = {
    '/api': {
      target: env.VITE_API_PROXY_TARGET || 'http://localhost:5075',
      changeOrigin: true,
      secure: false,
    },
  };

  return {
    plugins: [react()],
    server: {
      port: 5173,
      host: 'localhost',
      proxy: apiProxy,
    },
    preview: {
      port: 4173,
      host: '127.0.0.1',
      proxy: apiProxy,
    },
  };
});
