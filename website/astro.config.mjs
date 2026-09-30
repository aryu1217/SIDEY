import { defineConfig } from "astro/config";

export default defineConfig({
  site: "https://sidey-app.github.io",
  base: "/SIDEY",
  output: "static",
  trailingSlash: "always",
  i18n: {
    locales: ["ko", "en", "ja", "zh-hans", "zh-hant", "uk", "ru", "it", "pt-pt", "es", "cs", "tr", "ro", "bg", "pt-br", "sr-cyrl", "pl", "sr-latn", "nl-be", "fr", "nl", "he", "de"],
    defaultLocale: "ko",
    routing: {
      prefixDefaultLocale: true,
    },
  },
  build: {
    format: "directory",
  },
  compressHTML: false,
  devToolbar: {
    enabled: false,
  },
});
