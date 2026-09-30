import { commerceProducts } from "../../public/assets/commerce-products.js";
import commerceCatalog from "../../../assets/v1/commerce-catalog.json";
import { getWebsiteCopy, supportedLocales, type Locale } from "../i18n/landing";

export type StoreCategory = "characters" | "throwables" | "bubbles";
export type StoreAssetMode = "character" | "throwable" | "cannon" | "bubble";

export interface StoreProduct {
  id: string;
  name: string;
  description: string;
  price: string;
  commerceID?: string;
  keepsake?: StoreProduct;
  sound?: string;
  isKeepsake?: boolean;
  isTree?: boolean;
  asset?: string;
  mode: StoreAssetMode;
  previewAsset?: string;
  emitterAsset?: string;
  mirrorsMovement?: boolean;
  bubbleTheme?: "default" | "bunny-pink" | "butter-chick" | "starry-cat";
}

export interface StoreCategoryContent {
  eyebrow: string;
  title: string;
  description: string;
  products: StoreProduct[];
}

export type StoreCatalog = Record<StoreCategory, StoreCategoryContent>;

interface CommerceLocalization {
  display_name: string;
  description: string;
}

const commerceBundles = import.meta.glob<Record<string, CommerceLocalization>>("../../../assets/v1/locale/commerce/*.json", { eager: true, import: "default" });
const commerceLocalizations = Object.fromEntries(supportedLocales.map(locale => [locale, commerceBundles[`../../../assets/v1/locale/commerce/${locale}.json`]])) as Record<Locale, Record<string, CommerceLocalization>>;

export const includedProductIDs = new Set(["pixel_hamster", "pixel_cat", "pixel_puppy", "pixel_rabbit", "pixel_penguin", "patch_soft_ball", "bubble_default"]);

// The same paid catalog drives the app, server, and public store. Never fall back
// to a different locale for a newly added product: a missing translation fails the build.
function completeCatalog(locale: Locale, catalog: StoreCatalog): StoreCatalog {
  for (const category of ["characters", "throwables", "bubbles"] as const) {
    const kind = category.slice(0, -1);
    const previous = catalog[category].products;
    const paid = commerceCatalog.filter((entry) => entry.kind === kind).sort((a, b) => a.sort_order - b.sort_order);
    const included = previous.filter((product) => includedProductIDs.has(product.id));
    catalog[category].products = [...included, ...paid.map((entry): StoreProduct => {
      const presentation = commerceProducts[entry.id as keyof typeof commerceProducts];
      const translated = commerceLocalizations[locale][entry.id];
      if (!translated) throw new Error(`Missing ${locale} store translation: ${entry.id}`);
      const renderID = entry.render_asset_id ?? entry.item_id;
      return {

        id: entry.item_id,
        commerceID: entry.id,
        name: translated.display_name,
        description: translated.description,
        price: locale === "ko" ? `${entry.direct_price.toLocaleString("ko-KR")}원` : `₩${entry.direct_price.toLocaleString("en-US")}`,
        mode: presentation.mode as StoreAssetMode,
        asset: presentation.asset,
        mirrorsMovement: ["pixel_starlight_upalupa", "pixel_guinea_pig"].includes(entry.item_id),
        emitterAsset: entry.id === "throwable_toy_cannon" ? "assets/cosmetics/throwable_toy_cannon_emitter.png" : undefined,
        bubbleTheme: kind === "bubble" ? entry.item_id.replace("bubble_", "").replaceAll("_", "-") as StoreProduct["bubbleTheme"] : undefined,
        isTree: entry.item_id === "pixel_tree",
        isKeepsake: Boolean(entry.related_character_product_id),
        sound: kind === "throwable" ? `assets/store/impact-${renderID}.wav` : undefined,
      };
    })];
  }
  for (const character of catalog.characters.products) {
    const paired = commerceCatalog.find((entry) => entry.related_character_product_id === character.commerceID);
    character.keepsake = paired ? catalog.throwables.products.find((product) => product.commerceID === paired.id) : undefined;
  }
  return catalog;
}

export const storeCategoriesByLocale = Object.fromEntries(supportedLocales.map(locale => [locale, completeCatalog(locale, structuredClone(getWebsiteCopy(locale).store) as StoreCatalog)])) as Record<Locale, StoreCatalog>;
export const storeCategories = storeCategoriesByLocale.ko;

export function getStoreCategories(locale: Locale): StoreCatalog {
  return storeCategoriesByLocale[locale];
}
