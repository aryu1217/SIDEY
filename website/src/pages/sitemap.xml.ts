import type { APIRoute } from "astro";
import { localePathSegment, supportedLocales } from "../i18n/landing";

const routeGroups = [
  "",
  "store/",
  "store/characters/",
  "store/throwables/",
  "store/bubbles/",
  "whats-new/",
  "whats-new/macos/",
  "whats-new/windows/",
  "terms/",
  "privacy/",
  "refund/",
  "support/",
] as const;

const lastModified = "2026-09-30";

export const GET: APIRoute = ({ site }) => {
  if (!site) throw new Error("Astro site URL is required to generate the sitemap");
  const root = new URL(import.meta.env.BASE_URL, site);
  const url = (path: string) => new URL(path, root).href;
  const entries = routeGroups.flatMap((suffix) => {
    const alternates = [
      ...supportedLocales.map((locale) =>
        `    <xhtml:link rel="alternate" hreflang="${locale}" href="${url(`${localePathSegment(locale)}/${suffix}`)}"/>`,
      ),
      `    <xhtml:link rel="alternate" hreflang="x-default" href="${url(suffix)}"/>`,
    ].join("\n");
    return [suffix, ...supportedLocales.map((locale) => `${localePathSegment(locale)}/${suffix}`)].map((path) => [
      "  <url>",
      `    <loc>${url(path)}</loc>`,
      `    <lastmod>${lastModified}</lastmod>`,
      alternates,
      "  </url>",
    ].join("\n"));
  });
  const body = [
    '<?xml version="1.0" encoding="UTF-8"?>',
    '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">',
    ...entries,
    "</urlset>",
    "",
  ].join("\n");
  return new Response(body, { headers: { "Content-Type": "application/xml; charset=utf-8" } });
};
