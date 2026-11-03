import type { Metadata, Viewport } from "next";
import { Archivo, IBM_Plex_Mono } from "next/font/google";
import Link from "next/link";
import "./globals.css";

const sans = Archivo({ subsets: ["latin"], axes: ["wdth"], variable: "--font-sans", display: "swap" });
const mono = IBM_Plex_Mono({ subsets: ["latin"], weight: ["400", "500"], variable: "--font-mono", display: "swap" });

export const metadata: Metadata = {
  title: "Stealth Mode — Debrief",
  description: "After-action review for every Stealth Mode training run.",
};

export const viewport: Viewport = { themeColor: "#0b0c0a", width: "device-width", initialScale: 1, viewportFit: "cover" };

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" className={`${sans.variable} ${mono.variable}`}>
      <body>
        <header className="topbar">
          <Link href="/" className="wordmark" aria-label="Stealth Mode debrief home">
            <svg className="wordmark-mark" viewBox="0 0 18 18" aria-hidden="true">
              <circle cx="9" cy="9" r="6.5" fill="none" stroke="var(--signal)" strokeWidth="1.5" />
              <path d="M9 0v5M9 13v5M0 9h5M13 9h5" stroke="var(--signal)" strokeWidth="1.5" />
            </svg>
            <span className="wordmark-name">Stealth Mode</span>
            <span className="wordmark-sub">Debrief</span>
          </Link>
          <span className="eyebrow">Scenario One</span>
        </header>
        <main className="shell">{children}</main>
      </body>
    </html>
  );
}
