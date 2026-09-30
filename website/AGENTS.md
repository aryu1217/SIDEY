# SIDEY Public Website Instructions

These instructions apply to `website/**`. The site is public product, store, checkout and policy presentation, not an internal rollout dashboard or a web messenger.

## Sources and claims

- Read the relevant current behavior under `docs/product/` and the system boundaries in
  `docs/architecture.md`; consult `docs/decisions/` only for long-lived rationale. Verify
  catalog, price and platform data against `assets/v1/commerce-catalog.json`,
  `release/*.json`, the relevant tests and shipped behavior. Machine-readable sources win
  when prose differs.
- Do not claim that a download, store listing, purchase path, feature, signing state or compatibility guarantee is available without current evidence. Treat localized equivalents of “coming soon” or “in preparation” as evidence-backed status, not durable filler, and update them when the confirmed state changes.
- Keep internal environment, migration, rollout, channel, server-topology and test terminology out of merchandising copy. Do not expose secrets, checkout tokens or unpublished operational instructions.
- Never claim E2EE or universal overlay compatibility. Preserve verified platform restrictions and the privacy boundary: SIDEY does not collect screen contents, active-app lists, other applications' keystrokes, mouse coordinates, files, microphone audio or camera video.
- Preserve the user-facing price, purchase, refund, terms, privacy, seller and platform contracts across localized pages. Do not silently remove a limitation while simplifying technical wording. The footer commerce and seller-information sections appear only on Korean pages; translated policy pages remain available for every supported locale.

## Structure and presentation

- Keep localized public routes symmetric for the Windows consumer locales in `assets/v1/ui-localizations.json`, using lowercase route segments and canonical BCP 47 language tags. Locale-neutral routes remain language gates; tool-only routes remain unprefixed where specified by the product documents.
- Use semantic HTML and the existing Astro/Sass structure. Keep visible label/value groups explicitly laid out; do not rely on browser-default definition-list spacing.
- Verify desktop and narrow mobile layouts, keyboard focus, readable hierarchy and overflow. Long addresses, email links and translated copy must wrap without horizontal scrolling. Prefer copy and usable-width adjustments before forced line breaks.
- Keep page-specific typography scoped so store or policy changes do not resize the landing page. Do not introduce a client framework or new JavaScript for a copy-only layout change.

## Workflow and validation

Use [web verification](../.agents/skills/web-verification/SKILL.md) for public-copy or responsive-presentation work that requires claim, build, or rendered-layout evidence. Update the relevant current document under `docs/product/` when behavior or scope changes. Add a `docs/decisions/` record only when the choice meets that directory's long-lived-decision rules.

Run `pnpm --dir website test` for website changes and any narrower affected checks. Inspect generated pages or rendered desktop/mobile layouts when appearance changes, search final public copy for stale status and internal terminology, and run `git diff --check`. Building or editing the site does not authorize deployment.
