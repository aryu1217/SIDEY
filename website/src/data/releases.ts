import { getWebsiteReleases } from "./release-notes";
import type { Locale } from "../i18n/landing";

const appStoreStorefronts: Partial<Record<Locale, string>> = {
  ko: "kr",
  en: "us",
  ja: "jp",
  "zh-Hant": "tw",
};

export function appStoreURLForLocale(locale: Locale) {
  return `https://apps.apple.com/${appStoreStorefronts[locale] ?? "us"}/app/sidey/id6808528060?mt=12`;
}

const windowsRelease = getWebsiteReleases("windows")[0];
if (!windowsRelease?.asset) throw new Error("Published Windows installer metadata is missing");

export const releases = {
  windows: {
    version: windowsRelease.version,
    url: windowsRelease.asset.url,
    // Published preview metadata. Pages replaces it with the verified release asset hash.
    sha256: windowsRelease.asset.sha256,
  },
} as const;
