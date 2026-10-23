import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// The portal talks to the core API under /api, so it is same-origin in every environment (no CORS):
// Vite proxies it in dev and preview; production puts the same prefix on the reverse proxy.
const target = process.env.VITE_API_URL ?? "http://localhost:5000";
const proxy = { "/api": { target, changeOrigin: true, ws: true, rewrite: (p: string) => p.replace(/^\/api/, "") } };

export default defineConfig({
  plugins: [react()],
  server: { proxy },
  preview: { proxy },
});
