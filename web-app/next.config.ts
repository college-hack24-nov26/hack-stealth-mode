import type { NextConfig } from "next";

// Static export: Cloudflare Pages serves ./out, Supabase is the backend.
const config: NextConfig = {
  output: "export",
  trailingSlash: true,
  images: { unoptimized: true },
};

export default config;
