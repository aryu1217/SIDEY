import recent from "./release-notes/recent.json";
import macos from "./release-notes/macos.json";
import { getWebsiteCopy, supportedLocales, type Locale } from "../i18n/landing";

export type ReleasePlatform = "macos" | "windows";
export const releaseCategories = ["new", "changed", "fixed"] as const;
export type ReleaseCategory = (typeof releaseCategories)[number];
export interface ReleaseCopy {
  introduction: string[];
  new: string[];
  changed: string[];
  fixed: string[];
  notes: string[];
}
interface ReleaseMetadata {
  version: string;
  url: string;
  publishedAt: string;
  asset?: { url: string; sha256: string };
}
export interface WebsiteRelease extends ReleaseMetadata {
  locales: Record<Locale, ReleaseCopy>;
}

// Editorial translations of published releases, reviewed alongside the website.
// Keep App Store history separate: native target metadata is not a shipped note.
const retained = (entries: WebsiteRelease[]) => entries.filter((entry) => Number(entry.version.split(".")[0]) >= 2).sort((left, right) => {
  const a = left.version.split(".").map(Number);
  const b = right.version.split(".").map(Number);
  for (let index = 0; index < Math.max(a.length, b.length); index++) {
    const difference = (b[index] ?? 0) - (a[index] ?? 0);
    if (difference) return difference;
  }
  return 0;
});
const translate = (platform: ReleasePlatform, entries: ReleaseMetadata[]): WebsiteRelease[] => entries.map(entry => ({
  ...entry,
  locales: Object.fromEntries(supportedLocales.map(locale => {
    const copies: Record<string, ReleaseCopy> = getWebsiteCopy(locale).releases[platform];
    const copy = copies[entry.version];
    if (!copy) throw new Error(`Missing ${locale} ${platform} release ${entry.version}`);
    return [locale, copy];
  })) as Record<Locale, ReleaseCopy>,
}));
const windowsReleases = retained(translate("windows", recent));
const macosReleases = retained(translate("macos", macos));

export function getWebsiteReleases(platform: ReleasePlatform) {
  return platform === "windows" ? windowsReleases : macosReleases;
}
