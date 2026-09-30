# Release operation

이 문서는 반복 가능한 공개 release 순서를 요약한다. 실제 version, build와 artifact
이름은 machine-readable source에서 읽으며 이 문서에 고정하지 않는다. Release, store
submission, upload, website deployment와 production backend deployment는 각각 명시적인
사용자 승인이 필요하다.

현재 source와 과거 release workflow 파일은 비공개 `sidey-app/SIDEY-source`에서 관리한다. 공개
`sidey-app/SIDEY`는 website output, 공개 정책, release metadata, 설치 artifact와
계속 배포하는 과거 AGPL binary에 필요한 source archive를 받는 배포 저장소다. Public
저장소의 파일을 source-of-truth로 역수정하지 않는다.

Private source와 backend의 GitHub Actions는 꺼져 있다. `.github/workflows/` 파일은
과거 절차의 기록이며 CI, Windows release, website deployment, backend 다운로드 집계가
실행되지 않는다. 웹사이트는 공개 `SIDEY` 저장소의 수동 `SIDEY Website deployment`
워크플로로 게시한다. 네이티브 release와 backend의 게시 경로는 이 워크플로에 포함되지 않는다.
모든 게시 작업은 검증과 사용자 승인을 거쳐 수행한다.

### 공개 저장소에서 웹사이트 게시

공개 저장소의 `.github/workflows/website-deployment.yml`이 실행 진입점이다. `main`에서
수동 실행하며 private source의 `main`을 읽기 전용 SSH Deploy key로 가져온다.
`SIDEY-source`의 Actions를 활성화할 필요가 없다.

- 조직의 Settings → Member privileges에서 Deploy keys를 허용한다.
- 전용 SSH 공개 키를 `SIDEY-source`의 Settings → Deploy keys에 등록한다.
  **Allow write access는 선택하지 않는다.**
- 대응하는 비공개 키를 공개 `SIDEY` 저장소의 Actions secret
  `SIDEY_SOURCE_DEPLOY_KEY`에 저장한다. 개인 SSH 키를 재사용하거나 키를 Git에 넣지 않는다.
- 공개 `SIDEY`의 Settings → Pages → Source는 **GitHub Actions**로 설정한다.
- 승인된 게시에는 공개 저장소의 Actions → SIDEY Website deployment → Run workflow를
  사용하고 `main`을 선택한다.

워크플로는 website pnpm build/test, asset·publication 검사를 수행한다.
`scripts/pages/prepare_website_deployment.py`는 현재 공개 `gh-pages`의 Windows update
metadata를 웹사이트의 published release snapshot 및 실제 GitHub release asset digest와
대조한 뒤 원본 bytes를 보존한다. 미출시 native target인 `release/windows.json`으로
다운로드나 업데이트를 바꾸지 않는다. 공개 릴리즈와 snapshot 또는 metadata가 다르면
게시를 중단하고, 릴리즈별로 검증된 metadata를 먼저 준비해야 한다.

업로드 대상은 public tree guard를 통과한 정적 사이트뿐이다. Private checkout, SSH key,
개발용 source archive는 공개 artifact에 포함하지 않는다. Pages 배포 직전에 공개
`gh-pages` commit이 바뀌었으면 수동 실행을 다시 해야 한다. 이 웹사이트 게시 경로는
기존 `gh-pages` update metadata를 수정하지 않으며 네이티브 릴리즈를 생성하지 않는다.

## 1. 범위와 version 확인

1. Target platform과 실제 shipped diff를 확정한다.
2. [version audit](../../.agents/skills/version-audit/SKILL.md) 절차로 최소 version/build
   변경을 판단한다.
3. [`release/version.json`](../../release/version.json)의 공통 HeadVer head·ISO yearweek와
   해당 platform build를 확인한다. 주차가 바뀌면 shared 작업 브랜치에서
   `python3 scripts/sidey_version.py --advance-yearweek`로 UTC ISO yearweek와 두 플랫폼
   mirror를 함께 갱신한다. Windows 공식 개발 빌드는 깨끗하고 원격에 게시된
   Windows 작업 브랜치에서 `python3 scripts/allocate_windows_build.py --apply`로 번호를
   먼저 예약한다. 예약 후 원본과 생성 mirror를 함께 commit하고 정확한 HEAD를 검증한 뒤
   실행 파일을 만든다. 실패하거나 취소한 빌드의 번호는 다시 쓰지 않는다. macOS build는
   현행 App Store Connect 업로드 이력을 확인하여 증가시킨다. `scripts/sidey_version.py
   --check <platform>`으로 생성 mirror를 검사한다. macOS manifest는 App Store target
   build 계약이며 공개 게시 여부는 App Store Connect에서 별도로 확인한다.

Commerce 또는 backend contract가 바뀌는 release라면 private source catalog의 검토된 commit과
backend snapshot provenance를 먼저 확인한다. Backend migration과 배포는 비공개
backend 저장소의 절차로 수행하며 이 저장소에서 대신 실행하지 않는다.

## 2. 검증과 release note

1. 변경 경로에 해당하는 integration check와 platform test를 정확한 candidate commit에
   실행한다.
2. 공통 version source, platform build 설정, release manifest, update source와 website metadata의 일치는
   `python3 scripts/skills/verify_release_consistency.py`로 검사한다.
3. Windows GitHub Release note는 [`docs/releases/`](../releases/)에
   [release-notes skill](../../.agents/skills/release-notes/SKILL.md)의 commit/PR evidence로
   작성하고 link와 attribution을 검증한다. App Store release note는 정확한 제출 build와
   App Store Connect 기록을 기준으로 작성하며, 같은 marketing version의 과거 Direct
   GitHub Release note를 재사용하거나 덮어쓰지 않는다.
4. 서명, 공증, installer 실행, store purchase 또는 실제 장시간 test처럼 수행하지 않은
   검사를 통과했다고 기록하지 않는다.

Candidate source가 검증 뒤 바뀌면 영향을 받는 검사를 다시 실행한다.

## 3. Artifact 생성과 검사

### Mac App Store

[`scripts/macos/archive_app_store.sh`](../../scripts/macos/archive_app_store.sh)로 App Store
archive를 만든다. App Store target의 bundle identity, sandbox entitlement, signing과
StoreKit 환경을 확인한다. 서명 정보와 App Store Connect 자격 증명은 저장소나 CI log에
넣지 않는다. Archive 검사 후 별도 승인된 App Store Connect upload·submission을 수행하고
심사·게시 상태를 확인한다. 직접 배포 DMG, Sparkle feed와 Homebrew Cask를 생성하거나
갱신하지 않는다. 삭제된 Direct release, 자산과 태그를 복원하거나 재게시하지 않는다.
사용자 계정·메시지·구매 원본은 삭제하지 않는다.

App Store metadata와 IAP localization은 저장소의 App Store Connect 동기화 도구로 먼저
read-only snapshot과 diff를 만든다. App 이름·부제·설명·keyword·release note·지원 및
개인정보 URL·screenshot, 활성 IAP의 표시명·설명과 대상 storefront availability를 같은
candidate 기준으로 검토한다. 원격 변경은 도구의 명시적인 apply 옵션만으로 충분하지
않으며 release 승인도 별도로 확인해야 한다. 기존 storefront를 제거하는 diff는 적용하지
않는다. API key는 CI secret 또는 local Keychain에서만 읽고 저장소 파일이나 log에 쓰지
않는다.

Metadata와 binary를 같은 review에 제출할 때도 upload, submission과 manual release는
각각 별도 승인 단계다. 승인 후에는 목표 storefront마다 설명·screenshot·지원 link,
통화와 활성 IAP 조회를 다시 확인한 뒤에만 수동 공개한다.

### Windows

기존 [SIDEY Windows release workflow](../../.github/workflows/windows-release.yml)는
참고용으로 남겨 두며 실행하지 않는다. 현재 자동 게시 경로는 없으므로 새 Windows
릴리스에는 정확한 source의 실기 검사, installer 생성·검증, 공개 자산 재다운로드와
SHA-256 대조를 포함한 별도 수동 절차를 확정해야 한다. Local build나 artifact 존재만으로
공개 release를 대체하지 않는다.

AGPL-covered historical binary를 계속 공개하는 release에는 binary를 만든 정확한 commit의
Corresponding Source archive, AGPL license와 필요한 notice가 함께 있어야 한다. Archive가
현재 private revision을 포함하지 않는지 확인하고 source commit, filename, size와 SHA-256을
release migration 기록에 고정한다. 이 조건을 충족하지 못하면 해당 binary를 공개 저장소로
이관하지 않는다.

## 4. 게시 후 확인

1. App Store 게시 상태 또는 Windows 공개 release artifact와 기대 hash를 다시 확인한다.
2. macOS 링크가 App Store를 가리키는지, Windows update manifest와 website가
   같은 공개 release를 가리키는지 확인한다.
3. App Store Connect 또는 GitHub의 platform release note가 공개 결과와 일치하는지 확인한다.
4. 별도 승인된 경우에만 store submission, website deployment 또는 backend deployment를
   수행하고 각각의 결과를 해당 system에서 검증한다.

과거 특정 release의 시행착오와 일회성 checklist는 private source의 Git/PR 이력에
맡긴다. 삭제된 macOS Direct GitHub Release를 이 목적으로 복원하지 않는다.
