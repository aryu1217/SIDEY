# SIDEY release metadata

`version.json` owns SIDEY's shared HeadVer head and ISO yearweek plus independent
Windows and macOS build numbers. `python3 scripts/sidey_version.py --write macos`
and `--write windows` update the platform release and native build mirrors. The matching
`--check` commands fail when a mirror is stale or a value violates its platform constraints.
At the ISO week boundary, run `python3 scripts/sidey_version.py --advance-yearweek` on a
shared task branch. It computes the UTC ISO week once and updates both platform mirrors
and the App Store localization release identity together.

`macos.json` is the generated Mac App Store target version/build mirror with the `appstore`
channel. It does not claim that a candidate has passed review or been published. The App
Store owns public availability and updates; the website links to its product page without
advertising a candidate version. `app-store-localizations.json` also resolves its release
identity from `version.json` instead of duplicating those values.

`windows.json` is the generated public Windows release mirror. Its version,
update version and release artifact version are the same HeadVer value. The public
update manifest omits the optional legacy revision fields so existing installed clients
can consume the new release. Historical revision-based releases keep their original tags.

Windows remains an unpackaged self-contained app distributed by NSIS. The version resolver
uses `{head}.{yearweek}.{windowsBuild}.0`, validates Microsoft Store's MSIX
component limits and exposes that value as Windows `FileVersion` and
`SideyMsixVersion`. Its first three components are the Windows update comparison version.
This does not create or publish an MSIX package.
Official Windows development builds reserve a unique number on the source Git remote
with `python3 scripts/allocate_windows_build.py --apply` before creating an executable.
Failed or canceled builds may leave gaps. Ad hoc local builds use the checked-in number
and do not consume a reservation.
The repeatable process is documented in
[`docs/operations/release.md`](../docs/operations/release.md).

Windows releases are published only by the manually dispatched `Windows Release` workflow
on `main`. It builds a draft, verifies its downloaded Setup EXE, publishes it and calls the
reusable Pages workflow. Pages verifies only the Windows installer artifact; macOS downloads
use the Mac App Store.

macOS supports only the App Store target. Archive creation, App Store Connect upload,
submission and publication are separate actions. Developer ID DMG releases, Sparkle feeds
and Homebrew Cask updates are retired. Historical Direct GitHub Releases, assets and tags were
removed and must not be republished. Purchase records and private source history remain intact.
Backend tests and deployment run in the private backend repository.
