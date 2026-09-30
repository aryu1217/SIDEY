import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, existsSync } from "node:fs";

const catalog = JSON.parse(readFileSync(new URL("../../assets/v1/commerce-catalog.json", import.meta.url)));
const localeMetadata = JSON.parse(readFileSync(new URL("../../assets/v1/ui-localizations.json", import.meta.url)));
const locales = localeMetadata.consumers.windows.locales;
const bundles = Object.fromEntries(locales.map(locale => [locale,
  JSON.parse(readFileSync(new URL(`../src/i18n/locales/${locale}.json`, import.meta.url))),
]));
const routeFor = locale => locale.toLowerCase();
const escapeHTML = text => text.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll("'", "&#39;").replaceAll('"', "&quot;");
const commerceLocalizations = Object.fromEntries(
  locales.map((locale) => [
    locale,
    JSON.parse(readFileSync(new URL(`../../assets/v1/locale/commerce/${locale}.json`, import.meta.url))),
  ]),
);
const read = (path) => readFileSync(new URL(`../dist/${path}`, import.meta.url), "utf8");
const manifest = JSON.parse(readFileSync(new URL("../../assets/v1/manifest.json", import.meta.url)));
const root = new URL("../../", import.meta.url);
const included = { characters: 5, throwables: 1, bubbles: 1 };

test("website locale registry and complete translations match Windows languages", () => {
  assert.equal(locales.length, 23);
  const registry = readFileSync(new URL("../src/i18n/landing.ts", import.meta.url), "utf8");
  const declared = [...registry.match(/export const supportedLocales = \[([\s\S]*?)\] as const/)[1].matchAll(/"([^"]+)"/g)].map(match => match[1]);
  assert.deepEqual(declared, locales);
  const shape = (value, key, original = false) => {
    if (Array.isArray(value)) return value.map(child => shape(child, undefined, original));
    if (value && typeof value === "object") return Object.fromEntries(Object.entries(value).filter(([key]) => key !== "titleParts").map(([key, child]) => [key, shape(child, key, original)]));
    assert.equal(typeof value, "string");
    if (!(original && ["translationNotice", "originalLabel"].includes(key))) assert.ok(value.trim().length > 0);
    return "string";
  };
  for (const locale of locales) {
    const copy = bundles[locale];
    assert.deepEqual(shape(copy, undefined, locale === "ko"), shape(bundles.en), `${locale}: complete key and array coverage`);
    assert.deepEqual([...copy.legalNotice.matchAll(/\{([^}]+)\}/g)].map(match => match[1]).sort(), ["privacy", "terms"]);
    for (const policy of ["terms", "privacy", "refund"]) {
      const body = copy.policies[policy].body;
      const original = bundles.en.policies[policy].body;
      assert.equal((body.match(/^## /gm) ?? []).length, (original.match(/^## /gm) ?? []).length, `${locale}/${policy}: every section retained`);
      assert.equal((body.match(/^### /gm) ?? []).length, (original.match(/^### /gm) ?? []).length, `${locale}/${policy}: every subsection retained`);
      assert.equal((body.match(/^- /gm) ?? []).length, (original.match(/^- /gm) ?? []).length, `${locale}/${policy}: every list item retained`);
      if (locale !== "en") {
        assert.notEqual(body, original, `${locale}/${policy}: translated body`);
        assert.notEqual(copy.policies[policy].translationNotice, bundles.en.policies[policy].translationNotice, `${locale}/${policy}: native translation notice`);
        assert.doesNotMatch(body, /\/SIDEY\/en\//, `${locale}/${policy}: localized policy links`);
      }
    }
    assert.ok(copy.policies.terms.body.includes("AGPL-3.0-only"), `${locale}: historical licensing identifier`);
    assert.ok(copy.policies.terms.body.includes("sidey-app/SIDEY"), `${locale}: public repository boundary`);
  }
});

test("downloads show published versions and link both platform histories on the website", () => {
  const windows = JSON.parse(readFileSync(new URL("../src/data/release-notes/recent.json", import.meta.url)))[0];
  const macos = JSON.parse(readFileSync(new URL("../src/data/release-notes/macos.json", import.meta.url)))[0];
  assert.equal(macos.version, "2.0.0", "published Mac App Store version");
  assert.equal(windows.version, "2.2640.12", "published Windows version");
  assert.match(windows.asset.sha256, /^[a-f0-9]{64}$/);
  for (const locale of locales) {
    const route = routeFor(locale);
    const html = read(`${route}/index.html`);
    assert.ok(html.includes(`SIDEY v${macos.version}`), `${locale}: published macOS label`);
    assert.ok(html.includes(`data-release-version="windows">${windows.version}</span>`), `${locale}: published Windows label`);
    assert.ok(html.includes(windows.asset.url), `${locale}: published installer`);
    assert.ok(html.includes(windows.asset.sha256), `${locale}: installer hash`);
    for (const platform of ["macos", "windows"]) assert.ok(html.includes(`/SIDEY/${route}/whats-new/${platform}/`), `${locale}: ${platform} website notes`);
  }
});

const checkoutPrepared = {
  product_id: "fixture-product",
  order_name: "Fixture product",
  amount: 1100,
  currency: "KRW",
  policy_version: "fixture-policy",
  policy_notice: "Fixture purchase policy",
  payment_environment: "test",
};

const checkoutAuthorized = {
  ...checkoutPrepared,
  store_id: "store-fixture",
  channel_key: "channel-fixture",
  payment_id: "payment-fixture",
  pay_method: "CARD",
  portone_currency: "CURRENCY_KRW",
  redirect_url: "https://sidey-app.github.io/SIDEY/checkout-result/",
};

const jsonResponse = (payload, status = 200) => ({
  ok: status >= 200 && status < 300,
  status,
  json: async () => payload,
});

async function waitFor(predicate, message) {
  for (let attempt = 0; attempt < 20; attempt++) {
    if (predicate()) return;
    await new Promise(resolve => setImmediate(resolve));
  }
  assert.fail(message);
}

async function createCheckoutHarness({ fetchResponse, requestPayment } = {}) {
  const { runInNewContext } = await import("node:vm");
  let source = readFileSync(new URL("../public/assets/checkout.js", import.meta.url), "utf8");
  source = source.replace(/^import .*;\r?\n/, `const commerceProducts = ${JSON.stringify({
    "fixture-product": { name: "Fixture product", image: "assets/store/fixture.png", kind: "character" },
    "other-fixture-product": { name: "Other fixture product", image: "assets/store/other-fixture.png", kind: "character" },
  })};\n`);
  source = source.replaceAll("import.meta.url", '"https://sidey-app.github.io/SIDEY/assets/checkout.js"');

  class Element {
    constructor(values = {}) { Object.assign(this, values); }
    listeners = {};
    dataset = {};
    hidden = false;
    disabled = false;
    checked = false;
    value = "";
    textContent = "";
    focused = 0;
    addEventListener(name, listener) { this.listeners[name] = listener; }
    focus() { this.focused++; }
  }

  const elements = Object.fromEntries([
    "loading", "error", "error-message", "product", "product-image", "preview-frame",
    "order-name", "amount", "consent", "policy-notice", "pay", "status",
  ].map(id => [`#checkout-${id}`, new Element()]));
  elements["#checkout-error"].hidden = true;
  elements["#checkout-product"].hidden = true;

  const requests = [];
  const paymentRequests = [];
  const assigned = [];
  const defaultFetchResponse = async (url, init, body) => {
    if (url.endsWith("/commerce-checkout") && body.action === "prepare") return jsonResponse(checkoutPrepared);
    if (url.endsWith("/commerce-checkout") && body.action === "authorize") return jsonResponse(checkoutAuthorized);
    if (url.endsWith("/commerce-complete")) {
      return jsonResponse({ result_url: "https://sidey-app.github.io/SIDEY/checkout-result/?result=success" });
    }
    return jsonResponse({ error: "unexpected_request" }, 500);
  };
  const location = {
    hash: `#token=${"a".repeat(43)}`,
    pathname: "/SIDEY/checkout/",
    origin: "https://sidey-app.github.io",
    assign(url) { assigned.push(url); },
  };
  const sdkRequest = requestPayment ?? (async () => ({ code: "USER_CANCEL", message: "결제가 취소되었습니다." }));

  runInNewContext(source, {
    URL,
    URLSearchParams,
    Intl,
    document: { querySelector: selector => elements[selector] },
    window: {
      location,
      history: { replaceState() {} },
      PortOne: {
        async requestPayment(request) {
          paymentRequests.push(request);
          return sdkRequest(request);
        },
      },
    },
    console: { error() {} },
    fetch: async (url, init) => {
      const body = JSON.parse(init.body);
      requests.push({ url, body });
      return (fetchResponse ?? defaultFetchResponse)(url, init, body);
    },
  });

  await waitFor(() => elements["#checkout-product"].hidden === false || elements["#checkout-error"].hidden === false, "checkout did not finish preparing");
  return { elements, requests, paymentRequests, assigned };
}

for (const locale of locales) {
  const routeLocale = routeFor(locale);
  for (const category of Object.keys(included)) {
    test(`${locale}/${category}: complete catalog, exact reference prices, assets, separate keepsakes`, () => {
      const html = read(`${routeLocale}/store/${category}/index.html`);
      const cards = [...html.matchAll(/<button class="store-product-card"[^>]*>[\s\S]*?<\/button>/g)].map(([card]) => card);
      const paid = catalog.filter((entry) => entry.kind === category.slice(0, -1));
      assert.equal(cards.length, paid.length + included[category]);
      assert.doesNotMatch(html, /store-price-basis/);
      assert.doesNotMatch(html, /Windows direct-purchase|Windows 직접 결제|Windows版の直接決済/);
      assert.doesNotMatch(html, /direct macOS edition|macOS 직배포판|macOS直接配布版/);
      for (const entry of paid) {
        const card = cards.find((candidate) => candidate.includes(`data-product-id="${entry.id}"`));
        assert.ok(card, entry.id);
        const price = locale === "ko" ? `${entry.direct_price.toLocaleString("ko-KR")}원` : `₩${entry.direct_price.toLocaleString("en-US")}`;
        assert.ok(card.includes(`<strong>${price}</strong>`), `${entry.id}: ${price}`);
        const localized = commerceLocalizations[locale][entry.id];
        assert.ok(localized, `${entry.id}: ${locale} localization`);
        assert.ok(card.includes(escapeHTML(localized.display_name)), `${entry.id}: localized display name`);
        assert.ok(card.includes(escapeHTML(localized.description)), `${entry.id}: localized description`);
        const keepsake = catalog.find((candidate) => candidate.related_character_product_id === entry.id);
        if (keepsake) {
          assert.match(card, /store-keepsake-summary/);
          assert.match(card, /store-keepsake-badge/);
          assert.ok(html.includes(`id="details-${keepsake.item_id}"`), "keepsake transition retains its price details");
        }
        if (entry.related_character_product_id) assert.match(card, /store-keepsake-badge/);
      }
      for (const [, path] of html.matchAll(/(?:src|data-preview-src|data-preview-emitter|data-preview-sound)="\/SIDEY\/([^"?#]+)"/g)) {
        assert.ok(existsSync(new URL(`../dist/${path}`, import.meta.url)), path);
      }
      assert.doesNotMatch(html, /production|staging|Sidey-dev|출시 예정|준비 중|checkout\?token=/);
      if (category === "throwables") {
        for (const card of cards) assert.match(card, /data-preview-sound="[^"]+\.wav"/);
        const soundButton = html.match(/<button[^>]*data-store-preview-sound-toggle[^>]*>[\s\S]*?<\/button>/)?.[0];
        assert.ok(soundButton);
        assert.match(soundButton, /aria-pressed="false"/);
        assert.match(soundButton, /<svg[^>]*viewBox="0 0 24 24"/);
        assert.doesNotMatch(soundButton, /material-symbols|volume_off|volume_up/);
      }
      if (category === "characters") {
        assert.equal(html.match(/class="store-keepsake-summary"/g)?.length, catalog.filter(entry => entry.related_character_product_id).length);
      }
    });
  }
  test(`${locale}: store entry and character tab show the same products`, () => {
    const ids = (html) => [...html.matchAll(/<button class="store-product-card"[^>]*data-product-id="([^"]+)"/g)].map((match) => match[1]);
    assert.deepEqual(ids(read(`${routeLocale}/store/index.html`)), ids(read(`${routeLocale}/store/characters/index.html`)));
  });
}

test("public sheets and sounds exactly match the approved canonical assets", () => {
  for (const entry of catalog.filter((entry) => entry.kind !== "bubble")) {
    const id = entry.render_asset_id ?? entry.item_id;
    const source = entry.kind === "character" ? `characters/${id}/base.png` : `throwables/${id}/sprite.png`;
    assert.deepEqual(readFileSync(new URL(`website/public/assets/store/${id}.png`, root)), readFileSync(new URL(`assets/v1/${source}`, root)));
  }
  const throwableIDs = ["patch_soft_ball", ...catalog.filter(entry => entry.kind === "throwable").map(entry => entry.render_asset_id ?? entry.item_id)];
  for (const id of throwableIDs) {
    const path = `impact-${id}.wav`;
    const publicAudio = readFileSync(new URL(`website/public/assets/store/${path}`, root));
    const asset = manifest.throwables.find(entry => entry.id === id);
    if (asset.supported_platforms.includes("macos")) {
      assert.deepEqual(publicAudio, readFileSync(new URL(`macos/SIDEY/Resources/DirectImpactAudio/${path}`, root)));
    }
    const canonical = new URL(`assets/v1/audio/${path}`, root);
    if (existsSync(canonical)) assert.deepEqual(publicAudio, readFileSync(canonical));
    assert.ok(read("ko/store/throwables/index.html").includes(`data-preview-sound="/SIDEY/assets/store/${path}"`));
  }
});

test("checkout and store share all current products and correct base-relative image URLs", async () => {
  const { commerceProducts } = await import("../public/assets/commerce-products.js");
  assert.deepEqual(Object.keys(commerceProducts).sort(), catalog.map(p => p.id).sort());
  for (const entry of catalog) {
    const product = commerceProducts[entry.id];
    assert.equal(product.name, entry.name);
    assert.ok(existsSync(new URL(`website/public/${product.image}`, root)), product.image);
    for (const base of ["https://example.test/SIDEY/", "https://example.test/"]) {
      const url = new URL(`../${product.image}`, `${base}assets/checkout.js`);
      assert.equal(url.href, base + product.image);
    }
  }
  for (const page of ["checkout", "checkout-result"]) {
    assert.match(read(`${page}/index.html`), new RegExp(`type="module"[^>]*src="[^\"]*${page}\\.js\\?v=[a-f0-9]{12}"|src="[^\"]*${page}\\.js\\?v=[a-f0-9]{12}"[^>]*type="module"`));
  }
});

test("preview sound responds to clicks, motion preference, product changes and dismissal", async () => {
  const { runInNewContext } = await import("node:vm");
  class Element {
    dataset = {}; listeners = {}; attributes = {}; children = [];
    classList = { add() {}, toggle() {} };
    addEventListener(name, fn) { this.listeners[name] = fn; }
    setAttribute(name, value) { this.attributes[name] = value; }
    appendChild(child) { this.children.push(child); }
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.children = children; }
    closest() { return this; }
    focus() {}
  }
  class Dialog extends Element {
    open = false;
    showModal() { this.open = true; }
    close() { this.open = false; this.listeners.close(); }
  }
  const dialog = new Dialog();
  const controls = Object.fromEntries(["stage", "title", "description", "close", "details", "sound-toggle"].map(key => [`[data-store-preview-${key}]`, new Element()]));
  dialog.querySelector = selector => controls[selector];
  const sound = controls["[data-store-preview-sound-toggle]"];
  const stage = controls["[data-store-preview-stage]"];
  const document = {
    hidden: false, documentElement: { dataset: { baseUrl: "/SIDEY/" } }, listeners: {},
    querySelectorAll: () => [], getElementById: id => id === "store-preview-dialog" ? dialog : null,
    createElement: () => new Element(), addEventListener(name, fn) { this.listeners[name] = fn; },
  };
  const players = [];
  class Audio {
    plays = 0; pauses = 0;
    constructor(source) { this.source = source; players.push(this); }
    play() { this.plays++; return new Promise((resolve, reject) => { this.reject = reject; }); }
    pause() { this.pauses++; }
  }
  const timers = new Map();
  let timerID = 0;
  let reducedMotion = true;
  const window = {
    matchMedia: () => ({ matches: reducedMotion }),
    setTimeout: fn => { timers.set(++timerID, fn); return timerID; },
    clearTimeout: id => timers.delete(id),
  };
  runInNewContext(readFileSync(new URL("../public/assets/store.js", import.meta.url), "utf8"), {
    document, window, Element, HTMLDialogElement: Dialog, HTMLTemplateElement: class {}, Audio,
  });
  const open = (soundPath, kind = "throwable") => {
    const target = new Element();
    target.dataset = { previewKind: kind, previewSrc: "sprite.png", previewSound: soundPath };
    document.listeners.click({ target });
  };
  open("ball.wav");
  assert.equal(sound.hidden, false);
  assert.equal(players[0].plays, 0, "default is silent");
  sound.listeners.click();
  assert.equal(players[0].plays, 1, "explicit click works even with reduced motion");
  assert.equal(sound.attributes["aria-pressed"], "true");
  open("duck.wav");
  assert.equal(players[0].pauses, 1, "switching products stops the previous sound");
  players[0].reject(new Error("old playback interrupted"));
  await Promise.resolve();
  assert.equal(sound.attributes["aria-pressed"], "true", "old audio failure cannot mute the new product");
  reducedMotion = false;
  const projectile = stage.children[1];
  projectile.listeners.animationiteration({ target: projectile, animationName: "preview-throw-arc" });
  [...timers.values()].at(-1)();
  assert.equal(players[1].plays, 1, "subsequent collisions play the selected sound");
  sound.listeners.click();
  assert.equal(players[1].pauses, 1);
  assert.equal(sound.attributes["aria-pressed"], "false");
  sound.listeners.click();
  document.hidden = true;
  document.listeners.visibilitychange();
  assert.equal(players[1].pauses, 2);
  document.hidden = false;
  controls["[data-store-preview-close]"].listeners.click();
  assert.equal(players[1].pauses, 3);
  [...timers.values()].at(-1)();
  assert.equal(dialog.open, false);
  open(undefined, "character");
  assert.equal(sound.hidden, true, "characters do not expose a sound toggle");
});

test("checkout sends the server-authorized amount as a fixed CARD request without easy-pay options", async () => {
  const harness = await createCheckoutHarness();
  const consent = harness.elements["#checkout-consent"];
  const pay = harness.elements["#checkout-pay"];
  consent.checked = true;
  consent.listeners.change();
  await pay.listeners.click();

  assert.equal(harness.paymentRequests.length, 1);
  assert.equal(harness.paymentRequests[0].payMethod, "CARD");
  assert.equal(harness.paymentRequests[0].totalAmount, checkoutPrepared.amount, "authorized server amount is authoritative");
  assert.equal(Object.hasOwn(harness.paymentRequests[0], "easyPay"), false);
});

test("checkout blocks missing consent before authorization", async () => {
  const harness = await createCheckoutHarness();
  await harness.elements["#checkout-pay"].listeners.click();
  assert.match(harness.elements["#checkout-status"].textContent, /먼저 동의/);
  assert.equal(harness.elements["#checkout-consent"].focused, 1);
  assert.equal(harness.requests.filter(request => request.body.action === "authorize").length, 0);
  assert.equal(harness.paymentRequests.length, 0);
});

test("checkout rejects unsupported server payment methods before invoking PortOne", async () => {
  for (const payMethod of ["EASY_PAY", "TRANSFER", undefined]) {
    const harness = await createCheckoutHarness({
      fetchResponse: async (url, init, body) => {
        if (body.action === "prepare") return jsonResponse(checkoutPrepared);
        return jsonResponse({ ...checkoutAuthorized, pay_method: payMethod });
      },
    });
    harness.elements["#checkout-consent"].checked = true;
    await harness.elements["#checkout-pay"].listeners.click();
    assert.match(harness.elements["#checkout-status"].textContent, /결제를 시작하지 못했습니다/, String(payMethod));
    assert.equal(harness.paymentRequests.length, 0, String(payMethod));
  }
});

test("checkout rejects authorization details that differ from the prepared order", async () => {
  for (const [field, value] of [
    ["product_id", "other-fixture-product"],
    ["amount", 2200],
    ["policy_version", "different-policy"],
  ]) {
    const harness = await createCheckoutHarness({
      fetchResponse: async (url, init, body) => {
        if (body.action === "prepare") return jsonResponse(checkoutPrepared);
        return jsonResponse({ ...checkoutAuthorized, [field]: value });
      },
    });
    harness.elements["#checkout-consent"].checked = true;
    await harness.elements["#checkout-pay"].listeners.click();
    assert.match(harness.elements["#checkout-status"].textContent, /결제를 시작하지 못했습니다/, field);
    assert.equal(harness.paymentRequests.length, 0, field);
  }
});

test("checkout distinguishes authorization failures from payment SDK failures", async () => {
  const authorizationFailure = await createCheckoutHarness({
    fetchResponse: async (url, init, body) => {
      if (body.action === "prepare") return jsonResponse(checkoutPrepared);
      return jsonResponse({ error: "authorize_failed" }, 500);
    },
  });
  authorizationFailure.elements["#checkout-consent"].checked = true;
  await authorizationFailure.elements["#checkout-pay"].listeners.click();
  assert.match(authorizationFailure.elements["#checkout-status"].textContent, /결제를 시작하지 못했습니다/);

  const sdkFailure = await createCheckoutHarness({
    requestPayment: async () => { throw new Error("sdk_fixture_failure"); },
  });
  sdkFailure.elements["#checkout-consent"].checked = true;
  await sdkFailure.elements["#checkout-pay"].listeners.click();
  assert.match(sdkFailure.elements["#checkout-status"].textContent, /결제창을 열거나 진행하지 못했습니다/);
  assert.doesNotMatch(sdkFailure.elements["#checkout-status"].textContent, /sdk_fixture_failure/);
});

test("checkout locks controls and ignores duplicate clicks and consent changes while authorizing", async () => {
  let releaseAuthorization;
  const authorization = new Promise(resolve => { releaseAuthorization = resolve; });
  const harness = await createCheckoutHarness({
    fetchResponse: async (url, init, body) => {
      if (body.action === "prepare") return jsonResponse(checkoutPrepared);
      if (body.action === "authorize") return authorization;
      return jsonResponse({ error: "unexpected_request" }, 500);
    },
  });
  const consent = harness.elements["#checkout-consent"];
  const pay = harness.elements["#checkout-pay"];
  consent.checked = true;

  const firstClick = pay.listeners.click();
  await waitFor(() => harness.requests.some(request => request.body.action === "authorize"), "authorization did not start");
  assert.equal(pay.disabled, true);
  assert.equal(consent.disabled, true);
  consent.listeners.change();
  await pay.listeners.click();
  assert.equal(harness.requests.filter(request => request.body.action === "authorize").length, 1);
  assert.equal(pay.disabled, true, "change events cannot unlock an active request");

  releaseAuthorization(jsonResponse(checkoutAuthorized));
  await firstClick;
  assert.equal(harness.paymentRequests.length, 1);
});

test("checkout re-enables valid controls after an SDK cancellation response", async () => {
  const harness = await createCheckoutHarness();
  const consent = harness.elements["#checkout-consent"];
  const pay = harness.elements["#checkout-pay"];
  consent.checked = true;

  await pay.listeners.click();
  assert.equal(harness.elements["#checkout-status"].textContent, "결제가 취소되었습니다.");
  assert.equal(pay.disabled, false);
  assert.equal(consent.disabled, false);
});

test("checkout prevents retries after an accepted SDK result cannot be confirmed", async () => {
  for (const failure of ["payment ID mismatch", "completion failure"]) {
    const harness = await createCheckoutHarness({
      requestPayment: async () => ({
        paymentId: failure === "payment ID mismatch" ? "different-payment" : checkoutAuthorized.payment_id,
      }),
      fetchResponse: async (url, init, body) => {
        if (body.action === "prepare") return jsonResponse(checkoutPrepared);
        if (body.action === "authorize") return jsonResponse(checkoutAuthorized);
        return jsonResponse({ error: "complete_fixture_failure" }, 500);
      },
    });
    harness.elements["#checkout-consent"].checked = true;
    await harness.elements["#checkout-pay"].listeners.click();
    assert.match(harness.elements["#checkout-status"].textContent, /다시 결제하지 말고 SIDEY 상점에서 구매 상태를 확인/);
    assert.equal(harness.elements["#checkout-pay"].disabled, true, failure);
    assert.equal(harness.elements["#checkout-consent"].disabled, true, failure);
    await harness.elements["#checkout-pay"].listeners.click();
    assert.equal(harness.requests.filter(request => request.body.action === "authorize").length, 1, failure);
    assert.equal(harness.paymentRequests.length, 1, failure);
  }
});

test("checkout redirects only after successful server completion", async () => {
  const harness = await createCheckoutHarness({
    requestPayment: async () => ({ paymentId: checkoutAuthorized.payment_id }),
  });
  harness.elements["#checkout-consent"].checked = true;
  await harness.elements["#checkout-pay"].listeners.click();
  assert.deepEqual(harness.assigned, ["https://sidey-app.github.io/SIDEY/checkout-result/?result=success"]);
  assert.equal(harness.elements["#checkout-pay"].disabled, true);
});
test("checkout ignores injected API origins and sends tokens only to SIDEY production", async () => {
  const { runInNewContext } = await import("node:vm");
  for (const name of ["checkout", "checkout-result"]) {
    let source = readFileSync(new URL(`../public/assets/${name}.js`, import.meta.url), "utf8");
    source = source.replace(/^import .*;\r?\n/, "const commerceProducts = {};\n");
    source = source.replaceAll("import.meta.url", '"https://sidey-app.github.io/SIDEY/assets/checkout.js"');
    const requests = [];
    const element = () => ({ addEventListener() {}, dataset: {} });
    runInNewContext(source, {
      URL, URLSearchParams, Intl,
      document: { querySelector: element },
      window: {
        location: {
          search: "?api=https://attacker.supabase.co/functions/v1&result=complete&paymentId=payment-test",
          hash: `#token=${"a".repeat(43)}`,
          pathname: `/SIDEY/${name}/`, origin: "https://sidey-app.github.io",
        },
        history: { replaceState() {} },
      },
      console: { error() {} },
      fetch: async (url) => {
        requests.push(url);
        return { ok: false, status: 410, json: async () => ({ error: "checkout_expired" }) };
      },
    });
    assert.equal(requests.length, 1);
    assert.equal(requests[0], `https://whtejsviizgejauasqqt.supabase.co/functions/v1/${name === "checkout" ? "commerce-checkout" : "commerce-complete"}`);
  }
});

test("checkout CSP permits PortOne preparation and its hosted payment frame", () => {
  const page = readFileSync(new URL("../src/pages/checkout.astro", import.meta.url), "utf8");
  const policy = page.match(/content="(default-src[^"]+)"/)[1];
  const directives = Object.fromEntries(policy.split(";").map((part) => {
    const [name, ...values] = part.trim().split(/\s+/);
    return [name, values];
  }));
  for (const directive of ["connect-src", "frame-src"]) {
    assert.ok(directives[directive].includes("https://checkout-service.prod.iamport.co"));
  }
  assert.ok(directives["connect-src"].includes("https://whtejsviizgejauasqqt.supabase.co"));
  assert.ok(!directives["connect-src"].includes("https://*.supabase.co"));
});

test("generated checkout pages use external executable scripts under strict CSP", () => {
  for (const page of ["checkout", "checkout-result"]) {
    const html = read(`${page}/index.html`);
    const policy = html.match(/http-equiv="Content-Security-Policy" content="([^"]+)"/)?.[1];
    assert.ok(policy, `${page}: CSP is present`);
    assert.doesNotMatch(policy, /'unsafe-inline'/, `${page}: inline script execution stays disabled`);
    assert.match(html, /<script[^>]+src="\/SIDEY\/assets\/site-theme\.js"[^>]*><\/script>/);
    assert.match(html, /<script[^>]+src="\/SIDEY\/assets\/site-header\.js"[^>]*><\/script>/);
    assert.match(html, new RegExp(`<script[^>]+src="(?:/SIDEY/|\\.\\./)assets/${page}\\.js\\?v=[a-f0-9]{12}"[^>]*></script>`));
    for (const [, attributes, body] of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/g)) {
      assert.match(attributes, /\bsrc="[^"]+"/, `${page}: every executable script has an external source`);
      assert.equal(body.trim(), "", `${page}: executable script body is empty`);
    }
  }
});

test("legacy policy HTML routes redirect to locale-neutral policy gateways", () => {
  for (const path of ["privacy", "terms"]) {
    const html = read(`${path}.html`);
    assert.match(html, /name="robots" content="noindex"/);
    assert.match(html, new RegExp(`rel="canonical" href="https://sidey-app\\.github\\.io/SIDEY/${path}/"`));
    assert.ok(html.includes(`url=/SIDEY/${path}/`));
    assert.match(html, /destination\.search=window\.location\.search/);
    assert.match(html, /destination\.hash=window\.location\.hash/);
  }
});

test("localized terms preserve historical AGPL grants without presenting current source as open", () => {
  const expectations = {
    ko: ["현재 SIDEY 소스코드는 비공개 독점 소프트웨어", "이미 부여된", "철회"],
    en: ["Current SIDEY source code is private proprietary software", "rights already granted", "withdraw"],
    ja: ["現在のSIDEYソースコードは非公開のプロプライエタリソフトウェア", "すでに付与された", "撤回"],
    "zh-hant": ["目前 SIDEY 原始碼為非公開專有軟體", "不撤回", "先前授予"],
  };
  for (const [locale, phrases] of Object.entries(expectations)) {
    const html = read(`${locale}/terms/index.html`);
    for (const phrase of phrases) assert.ok(html.includes(phrase), `${locale}: ${phrase}`);
    assert.ok(html.includes("AGPL-3.0-only"), `${locale}: historical license identifier`);
  }
});

test("all locale support pages publish contact, diagnostics, and sensitive-data warnings", () => {
  for (const locale of locales) {
    const html = read(`${routeFor(locale)}/support/index.html`);
    const copy = bundles[locale].support;
    for (const text of [copy.githubBody, copy.emailBody, copy.protectBody, copy.response, ...copy.details]) {
      assert.ok(html.includes(escapeHTML(text)), `${locale}: complete support copy`);
    }
    assert.equal(copy.details.length, 6, `${locale}: required and optional issue fields`);
    assert.ok(html.includes('href="https://github.com/sidey-app/SIDEY/issues/new?template=01-bug-report.yml"'));
    assert.ok(html.includes('href="https://github.com/sidey-app/SIDEY/issues?q=is%3Aissue"'));
    assert.ok(html.includes('href="mailto:ryu200112@gmail.com"'));
    assert.ok(html.includes("uname -mprs"));
    assert.ok(html.includes("Environment]::OSVersion.VersionString"));
    assert.ok(html.includes("Environment]::Is64BitOperatingSystem"));
    assert.ok(html.includes("log show --last 30m"));
    assert.ok(html.includes("app.sidey.desktop"));
    assert.ok(html.includes("SIDEY-Diagnostics-$(date +%Y%m%d-%H%M%S).log"));
    assert.equal((html.match(/<pre\b[^>]*\bdir="ltr"/g) ?? []).length, 3, `${locale}: readable diagnostic commands`);
  }
});

test("localized pages and sitemap use the zh-Hant language tag with the lowercase route", () => {
  for (const path of ["", "store/", "terms/", "privacy/", "refund/", "support/", "whats-new/"]) {
    const html = read(`zh-hant/${path}index.html`);
    assert.match(html, /<html lang="zh-Hant"/);
    assert.ok(html.includes('hreflang="zh-Hant"'));
    assert.ok(html.includes(`/SIDEY/zh-hant/${path}`));
  }
  const sitemap = read("sitemap.xml");
  assert.ok(sitemap.includes('hreflang="zh-Hant" href="https://sidey-app.github.io/SIDEY/zh-hant/'));
  assert.doesNotMatch(sitemap, /\/SIDEY\/zh-Hant\//);
});

test("locale-neutral gates route language, region, script, aliases and preference lists", async () => {
  const { runInNewContext } = await import("node:vm");
  const cases = [
    ...locales.map(locale => [[locale], routeFor(locale)]),
    [["zh-TW"], "zh-hant"], [["zh-HK"], "zh-hant"], [["zh-MO"], "zh-hant"],
    [["zh-CN"], "zh-hans"], [["zh-SG"], "zh-hans"], [["zh"], "zh-hans"],
    [["zh-Hans-TW"], "zh-hans"], [["zh-Hant-CN"], "zh-hant"],
    [["pt"], "pt-pt"], [["pt-AO"], "pt-pt"], [["pt-BR"], "pt-br"],
    [["sr"], "sr-cyrl"], [["sr-RS"], "sr-cyrl"], [["sr-Latn-RS"], "sr-latn"],
    [["iw-IL"], "he"], [["iw"], "he"], [["he-IL"], "he"],
    [["nl-BE"], "nl-be"], [["nl-NL"], "nl"], [["de-AT"], "de"],
    [["ar", "fr-CA"], "fr"], [["ar"], "en"], [[], "en"],
  ];
  for (const path of ["", "store/", "privacy/", "refund/", "terms/", "support/", "whats-new/"]) {
    const html = read(`${path}index.html`);
    for (const locale of locales) {
      assert.ok(html.includes(`hreflang="${locale}"`));
      assert.ok(html.includes(`/SIDEY/${routeFor(locale)}/${path}`));
    }
    const source = html.match(/<script[^>]*>([\s\S]*?)<\/script>/)?.[1];
    assert.ok(source, `${path}: executable gateway`);
    for (const [languages, expected] of cases) {
      const destinations = [];
      runInNewContext(source, {
        URL,
        navigator: { languages, language: languages[0] },
        window: { location: { origin: "https://sidey-app.github.io", search: "?source=test", hash: "#section", replace: url => destinations.push(url) } },
      });
      assert.deepEqual(destinations, [`https://sidey-app.github.io/SIDEY/${expected}/${path}?source=test#section`], `${path}: ${languages}`);
    }
  }
});

test("Traditional Chinese release dates use zh-TW formatting", () => {
  const html = read("zh-hant/whats-new/windows/index.html");
  const releaseDate = html.match(/<time datetime="([^"]+)">([^<]+)<\/time>/);
  if (releaseDate) {
    const expected = new Intl.DateTimeFormat("zh-TW", { year: "numeric", month: "long", day: "numeric" }).format(new Date(releaseDate[1]));
    assert.equal(releaseDate[2], expected);
  }
});

test("localized pages use locale-default App Store storefronts consistently", () => {
  const storefronts = { ko: "kr", en: "us", ja: "jp", "zh-hant": "tw" };
  for (const code of locales) {
    const locale = routeFor(code);
    const storefront = storefronts[locale] ?? "us";
    const url = `https://apps.apple.com/${storefront}/app/sidey/id6808528060?mt=12`;
    const landing = read(`${locale}/index.html`);
    assert.ok(landing.includes(`href="${url}"`), `${locale}: landing link`);
    assert.ok(landing.includes(`data-macos-url="${url}"`), `${locale}: platform selector`);
    assert.ok(landing.includes(`"downloadUrl":"${url}"`), `${locale}: structured data`);
    assert.ok(landing.match(/data-app-store-link/g)?.length >= 3, `${locale}: adjustable links`);
    assert.ok(read(`${locale}/whats-new/index.html`).includes('id="v2.0.0"'), `${locale}: local macOS version history`);
    assert.ok(!read(`${locale}/whats-new/index.html`).includes("data-app-store-link"), `${locale}: release notes stay on website`);
  }
});

test("browser regions update App Store links and the platform-selector macOS target", async () => {
  const { runInNewContext } = await import("node:vm");
  const source = readFileSync(new URL("../public/assets/script.js", import.meta.url), "utf8");
  const regionLanguages = {
    gb: "en-GB", ca: "en-CA", au: "en-AU", sg: "en-SG", hk: "zh-Hant-HK", mo: "zh-Hant-MO",
    tw: "zh-Hant-TW", jp: "ja-JP", kr: "ko-KR", us: "en-US",
  };
  for (const [storefront, language] of Object.entries(regionLanguages)) {
    const original = "https://apps.apple.com/us/app/sidey/id6808528060?mt=12";
    const ordinaryLink = { href: original, dataset: {} };
    const primary = { href: original, dataset: { macosUrl: original } };
    const document = {
      querySelectorAll(selector) {
        if (selector === "[data-app-store-link]") return [ordinaryLink, primary];
        if (selector === "[data-macos-url]") return [primary];
        return [];
      },
    };
    runInNewContext(source, {
      URL,
      document,
      navigator: { languages: [language], language, platform: "Linux", userAgent: "Linux" },
      window: { location: { href: "https://sidey-app.github.io/SIDEY/en/" }, matchMedia: () => ({ matches: true }) },
    });
    const expected = `https://apps.apple.com/${storefront}/app/sidey/id6808528060?mt=12`;
    assert.equal(ordinaryLink.href, expected, language);
    assert.equal(primary.href, expected, `${language}: primary href`);
    assert.equal(primary.dataset.macosUrl, expected, `${language}: macOS selector target`);
  }
});

test("all download pages identify separate Windows installers without advertising an unpublished ARM64 download", () => {
  for (const locale of locales) {
    const html = read(`${routeFor(locale)}/index.html`);
    assert.match(html, /data-windows-label="[^"]+ \(x64\)"/);
    assert.match(html, /data-windows-arm64-label="[^"]+ \(ARM64\)"/);
    assert.match(html, /data-windows-arm64-url=""/);
    assert.match(html, /id="hero-windows-x64-download-action"[^>]+data-windows-architecture="x64"/);
    assert.match(html, /id="hero-windows-arm64-download-action"[^>]+href="#download"[^>]+data-windows-arm64-download[^>]+hidden/);
    assert.match(html, /<div data-windows-arm64-download hidden>/);
    assert.match(html, /id="windows-arm64-download-sha256"/);
  }
});

test("Windows download selection requires OS architecture hints and preserves explicit selection", async () => {
  const { runInNewContext } = await import("node:vm");
  const source = readFileSync(new URL("../public/assets/script.js", import.meta.url), "utf8");
  const cases = [
    { name: "x64", hints: { architecture: "x86", bitness: "64" }, expected: "x64" },
    { name: "ARM64", hints: { architecture: "arm", bitness: "64" }, expected: "arm64" },
    { name: "32-bit browser on 64-bit OS", hints: { architecture: "x86", bitness: "64", wow64: true }, expected: "x64" },
    { name: "32-bit Windows", hints: { architecture: "x86", bitness: "32" } },
    { name: "empty hints", hints: { architecture: "", bitness: "" } },
    { name: "unexpected hints", hints: { architecture: "riscv", bitness: "64" } },
    { name: "rejected hints", reject: true },
    { name: "no client hints, spoofed x64 UA" },
    { name: "no published ARM64", hints: { architecture: "arm", bitness: "64" }, armUnavailable: true },
    { name: "explicit choice beats pending hints", hints: { architecture: "arm", bitness: "64" }, manual: true, expected: "x64" },
    { name: "macOS", platform: "MacIntel", expected: "macos" },
    { name: "iPhone", platform: "iPhone" },
    { name: "Linux", platform: "Linux" },
  ];
  for (const scenario of cases) {
    const element = (dataset = {}) => ({
      dataset, hidden: false, href: "https://apps.apple.com/us/app/sidey/id6808528060?mt=12", textContent: "", listeners: {}, attributes: {},
      classList: { toggle() {} },
      addEventListener(type, callback) { this.listeners[type] = callback; },
      setAttribute(name, value) { this.attributes[name] = value; },
      getAttribute(name) { return this.attributes[name]; },
      getBoundingClientRect() { return {}; }, focus() {},
    });
    const primary = element({ macosUrl: "https://apps.apple.com/us/app/sidey/id6808528060?mt=12", windowsUrl: "https://example.com/x64.exe", windowsArm64Url: scenario.armUnavailable ? "" : "https://example.com/arm64.exe", macosLabel: "App Store", windowsLabel: "Windows (x64)", windowsArm64Label: "Windows (ARM64)", chooseLabel: "Choose platform" });
    const toggle = element();
    const menu = element();
    menu.hidden = true;
    const x64Link = element({ windowsArchitecture: "x64" });
    menu.querySelectorAll = () => [x64Link];
    const label = element();
    const logo = element({ windowsLogo: "windows.svg", macosLogo: "macos.svg" });
    const nodes = { "[data-auto-download]": primary, "[data-download-toggle]": toggle, "[data-download-menu]": menu, "[data-download-label]": label, "[data-download-logo]": logo };
    const selector = element();
    selector.querySelector = query => nodes[query];
    selector.contains = () => true;
    const navigator = { languages: ["en-US"], platform: scenario.platform ?? "Win32", userAgent: "Windows NT 10.0; Win64; x64" };
    let resolveHints;
    if (scenario.hints || scenario.reject) navigator.userAgentData = {
      platform: "Windows",
      getHighEntropyValues(keys) {
        assert.deepEqual(Array.from(keys), ["architecture", "bitness"]);
        return scenario.reject ? Promise.reject(new Error("denied")) : new Promise(resolve => { resolveHints = resolve; });
      },
    };
    runInNewContext(source, {
      URL, navigator,
      document: { querySelectorAll: query => query === "[data-download-selector]" ? [selector] : [], addEventListener() {} },
      window: { location: { href: "https://sidey-app.github.io/SIDEY/en/" }, matchMedia: () => ({ matches: true }), clearTimeout() {}, setTimeout() {}, requestAnimationFrame: callback => callback() },
    });
    await Promise.resolve();
    if (scenario.manual) x64Link.listeners.click();
    resolveHints?.(scenario.hints);
    // Flush promises created within the VM and their rejection handler.
    await new Promise(resolve => setImmediate(resolve));
    const expectedURL = scenario.expected === "x64" ? primary.dataset.windowsUrl : scenario.expected === "arm64" ? primary.dataset.windowsArm64Url : scenario.expected === "macos" ? primary.dataset.macosUrl : "#download";
    assert.equal(primary.href, expectedURL, scenario.name);
    assert.equal(label.textContent, scenario.expected === "x64" ? "Windows (x64)" : scenario.expected === "arm64" ? "Windows (ARM64)" : scenario.expected === "macos" ? "App Store" : "Choose platform", scenario.name);
    if (!scenario.expected) {
      let prevented = false;
      primary.listeners.click({ preventDefault() { prevented = true; } });
      assert.equal(prevented, true, scenario.name);
      assert.equal(menu.hidden, false, scenario.name);
      assert.equal(toggle.getAttribute("aria-expanded"), "true", scenario.name);
    }
  }
});

test("localized refund policies cover every paid customization category", () => {
  const expectations = {
    ko: ["유료 꾸미기 상품", "기본 햄스터", "기본 말풍선", "기본 던지기 장난감"],
    en: ["Paid SIDEY customization items", "default hamster", "default speech bubble", "default tossable toy"],
    ja: ["有料カスタマイズアイテム", "標準のハムスター", "標準の吹き出し", "標準の投げて遊ぶおもちゃ"],
    "zh-hant": ["付費自訂商品", "基本小倉鼠", "基本對話框", "基本投擲玩具"],
  };
  for (const [locale, phrases] of Object.entries(expectations)) {
    const html = read(`${locale}/refund/index.html`);
    for (const phrase of phrases) assert.ok(html.includes(phrase), `${locale}: ${phrase}`);
  }
});

test("Windows 2.2640.12 release links resolve to localized anchors and retain the signing warning", () => {
  const version = "2.2640.12";
  const note = readFileSync(new URL(`../../docs/releases/windows-v${version}.md`, import.meta.url), "utf8");
  for (const locale of ["ko", "en", "ja", "zh-hant"]) {
    assert.ok(note.includes(`https://sidey-app.github.io/SIDEY/${locale}/whats-new/windows/#v${version}`));
    const html = read(`${locale}/whats-new/windows/index.html`);
    assert.equal(html.split(`id="v${version}"`).length - 1, 1, `${locale}: unique release anchor`);
  }
  for (const locale of locales) {
    const copy = bundles[locale].releases.windows[version];
    assert.equal(copy.notes.length, 1, `${locale}: installation risk retained`);
    assert.match(copy.notes[0], /Windows Smart App Control/);
    assert.ok(read(`${routeFor(locale)}/whats-new/windows/index.html`).includes(escapeHTML(copy.notes[0])));
  }
});

test("both platform histories preserve only 2.0+ and render complete categorized translations", () => {
  for (const file of ["recent", "macos"]) {
    const entries = JSON.parse(readFileSync(new URL(`../src/data/release-notes/${file}.json`, import.meta.url)));
    for (const release of entries) {
      assert.ok(Number(release.version.split(".")[0]) >= 2);
      const platform = file === "macos" ? "macos" : "windows";
      for (const locale of locales) {
        const copy = bundles[locale].releases[platform][release.version];
        const route = routeFor(locale);
        const html = read(`${route}/whats-new/${file === "macos" ? "macos" : "windows"}/index.html`);
        assert.ok(html.includes(`id="v${release.version}"`));
        assert.ok(html.includes(`class="update-copy" lang="${locale}"`));
        for (const category of ["introduction", "new", "changed", "fixed", "notes"]) {
          assert.equal(copy[category].length, bundles.ko.releases[platform][release.version][category].length, `${release.version}/${locale}/${category}: translation coverage`);
          for (const text of copy[category]) {
            assert.doesNotMatch(text, /(?:\bPR\s*\d+|#\d+|@[a-zA-Z0-9-]+)/);
            assert.ok(html.includes(text.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll("'", "&#39;").replaceAll('"', "&quot;")), `${release.version}/${locale}: rendered translation`);
            if (locale !== "ko") assert.doesNotMatch(text, /[\uac00-\ud7af]/);
          }
        }
      }
    }
  }
  for (const code of locales) {
    const locale = routeFor(code);
    for (const platform of ["macos", "windows"]) {
      const html = read(`${locale}/whats-new/${platform}/index.html`);
      assert.doesNotMatch(html, /id="v1\./);
      assert.doesNotMatch(html, /class="release-link"|href="https:\/\/github\.com\/sidey-app\/SIDEY\/releases\/tag\//);
      for (const category of ["new", "changed", "fixed"]) assert.ok(html.includes(`data-release-category="${category}"`));
    }
  }
});

test("every public page has 23 corresponding language links and simplified navigation", () => {
  for (const code of locales) {
    const locale = routeFor(code);
    for (const path of ["", "store/", "store/characters/", "store/bubbles/", "store/throwables/", "terms/", "privacy/", "refund/", "support/", "whats-new/", "whats-new/macos/", "whats-new/windows/"]) {
      const html = read(`${locale}/${path}index.html`);
      const header = html.match(/<header[\s\S]*?<\/header>/)[0];
      const footer = html.match(/<footer[\s\S]*?<\/footer>/)[0];
      assert.ok(!header.includes("github.com/sidey-app/SIDEY"));
      assert.ok(!footer.match(/<div class="footer-links">([\s\S]*?)<\/div>/)[1].includes(`${locale}/support/`));
      for (const language of locales.map(routeFor)) assert.ok(footer.includes(`/SIDEY/${language}/${path}`), `${locale}/${path}: ${language} switch`);
    }
  }
});

test("Korean footer retains business disclosures and other languages retain policy and language links", () => {
  for (const locale of locales) {
    const footer = read(`${routeFor(locale)}/index.html`).match(/<footer[\s\S]*?<\/footer>/)[0];
    for (const policy of ["terms", "privacy", "refund"]) {
      assert.ok(footer.includes(escapeHTML(bundles[locale].footer[policy])));
      assert.ok(footer.includes(`/SIDEY/${routeFor(locale)}/${policy}/`));
    }
    if (locale === "ko") {
      for (const phrase of ["배송일자", "교환·환불", "사업자등록번호", "통신판매업 신고번호", "388-53-01259", "ryu200112@gmail.com"]) assert.ok(footer.includes(phrase), phrase);
      assert.match(footer, /footer-commerce-policy/);
      assert.match(footer, /footer-details-business/);
    } else {
      assert.doesNotMatch(footer, /footer-commerce-policy|footer-details-business|388-53-01259|ryu200112@gmail.com/);
    }
  }
});

test("support navigation and locale-neutral language metadata are explicit", () => {
  for (const code of locales) {
    const locale = routeFor(code);
    assert.match(read(`${locale}/support/index.html`), new RegExp(`<a href="/SIDEY/${locale}/support/" aria-current="page">`));
  }
  for (const path of ["", "store/", "privacy/", "refund/", "terms/", "support/", "whats-new/"]) {
    assert.match(read(`${path}index.html`), /<html lang="en"(?:\s[^>]*)?>/);
  }
});

test("public build excludes the former contribution asset previewer", () => {
  assert.equal(existsSync(new URL("../dist/contribute/asset-previewer/index.html", import.meta.url)), false);
  assert.doesNotMatch(read("sitemap.xml"), /contribute\/asset-previewer/);
});
