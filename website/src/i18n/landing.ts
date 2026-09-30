import english from "./locales/en.json";

// Keep this registry aligned with consumers.windows.locales in UI localization metadata.
export const supportedLocales = [
  "ko", "en", "ja", "zh-Hans", "zh-Hant", "uk", "ru", "it", "pt-PT", "es", "cs", "tr",
  "ro", "bg", "pt-BR", "sr-Cyrl", "pl", "sr-Latn", "nl-BE", "fr", "nl", "he", "de",
] as const;
export type Locale = (typeof supportedLocales)[number];
type LandingCopy = typeof english.landing;
export type WebsiteCopy = Omit<typeof english, "landing"> & {
  landing: Omit<LandingCopy, "hero" | "motion" | "download"> & {
    hero: LandingCopy["hero"] & { titleParts?: string[] };
    motion: LandingCopy["motion"] & {
      titleParts?: string[];
      presence: LandingCopy["motion"]["presence"] & { titleParts?: string[] };
      throw: LandingCopy["motion"]["throw"] & { titleParts?: string[] };
      pulse: LandingCopy["motion"]["pulse"] & { titleParts?: string[] };
    };
    download: LandingCopy["download"] & { titleParts?: string[] };
  };
};
const bundles = import.meta.glob<WebsiteCopy>("./locales/*.json", { eager: true, import: "default" });

export function getWebsiteCopy(locale: Locale): WebsiteCopy {
  const copy = bundles[`./locales/${locale}.json`];
  if (!copy) throw new Error(`Missing website translation: ${locale}`);
  return copy;
}
export function localePathSegment(locale: Locale) { return locale.toLowerCase(); }
export function localeRoot(locale: Locale, base = import.meta.env.BASE_URL) { return `${base}${localePathSegment(locale)}/`; }
export function localizeCanonicalPath(path: string, locale: Locale) { return path.replace(/^[^/]+\//, `${localePathSegment(locale)}/`); }
export function isLocale(value: string | undefined): value is Locale { return supportedLocales.includes(value as Locale); }
export function getTranslations(locale: Locale) { return getWebsiteCopy(locale).landing; }
export const getLandingCopy = getTranslations;
export const localeNames: Record<Locale, string> = {
  ko: "한국어", en: "English", ja: "日本語", "zh-Hans": "简体中文", "zh-Hant": "繁體中文",
  uk: "Українська", ru: "Русский", it: "Italiano", "pt-PT": "Português (Portugal)", es: "Español",
  cs: "Čeština", tr: "Türkçe", ro: "Română", bg: "Български", "pt-BR": "Português (Brasil)",
  "sr-Cyrl": "Српски (ћирилица)", pl: "Polski", "sr-Latn": "Srpski (latinica)",
  "nl-BE": "Nederlands (België)", fr: "Français", nl: "Nederlands", he: "עברית", de: "Deutsch",
};
