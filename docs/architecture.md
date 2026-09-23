# SIDEY 아키텍처

이 문서는 SIDEY 저장소와 실행 시스템의 현재 책임 경계를 설명한다. 제품 동작은
[`product/`](product/overview.md), 선택 이유는 [`decisions/`](decisions/README.md),
반복 가능한 릴리스 절차는 [`operations/`](operations/release.md)에서 다룬다.

## 시스템 구성

- macOS 클라이언트는 SwiftUI·AppKit·SpriteKit으로 만든 Mac App Store 네이티브 앱이다.
  Sign in with Apple, 전용 Keychain, sandbox와 StoreKit 경계를 유지한다. 직접 배포판의
  개발·지원은 종료하며 과거 계정·구매 원본을 자동 병합하거나 삭제하지 않는다.
- Windows 클라이언트는 C#/.NET·WinUI 3·Win32로 만든 네이티브 앱이다. 일반 UI와
  투명 overlay surface를 분리하고, Core·Presentation·Infrastructure·Overlay·Platform
  계층의 의존 방향은 [Windows 아키텍처 문서](../windows/docs/architecture.md)가 설명한다.
- 공개 웹사이트는 제품·다운로드·정책·상점 정보를 제공하는 Astro 정적 사이트다.
  메시징 웹 클라이언트가 아니다.
- 비공개 `sidey-app/SIDEY-source` 저장소는 네이티브 클라이언트, 공개 웹의 원본,
  client-facing contract, 승인된 asset 원본과 commerce catalog를 소유한다.
- 공개 `sidey-app/SIDEY` 저장소는 공식 website의 생성 결과, 공개 정책 문서,
  release metadata와 설치 artifact, 계속 배포하는 과거 AGPL binary에 필요한 정확한
  Corresponding Source archive를 제공한다. 현재 제품 source의 원본이 아니다.
- Private source는 GitHub Free에서 운영하며 유료 private ruleset이나 Actions environment에
  의존하지 않는다. Repository-level secret과 variable로 공개 저장소 전용 GitHub App을
  사용한다. 공개 저장소의 branch 보호는 public ruleset으로 설정하고 유지한다.
- Supabase schema·migration·RLS·Edge Functions, App Store 거래 검증, 서버 운영 도구는
  비공개 [`sidey-app/sidey-backend`](https://github.com/sidey-app/sidey-backend)가
  소유한다. 공개/비공개 저장소의 작업 경계와 catalog handoff는
  [`BACKEND.md`](BACKEND.md)를 따른다.
- 독립 로컬 운영 도구는 `sidey-admin` 저장소가 소유하고, 그 도구가 사용하는 서버
  조회·집계 계약은 backend 저장소가 소유한다.

## 소스 코드와 콘텐츠 권리

현재 SIDEY source는 [독점 소프트웨어 고지](../LICENSE)를 따르며 비공개 저장소에서
관리한다. 이 고지를 처음 포함한 Git revision보다 앞서 AGPL-3.0-only로 공개된
revision의 기존 이용 권한은 철회하지 않는다. 정확한 전환 경계, 제3자 구성요소,
브랜드와 asset 권리는 [라이선스 안내](../LICENSING.md)가 설명한다. 외부 코드·문서·번역·
asset 기여는 받지 않고, 공개 저장소는 재현 가능한 제품 bug 신고만 받는다.

## 데이터와 실시간 경계

Postgres가 영구 메시지와 계정·방 상태의 원본이다. Supabase Presence는 연결 및 활동
상태를 계속 담당한다. Firebase v2는 SIDEY 입력창의 typing, 캐릭터 pulse·projectile과
chat 전달 같은 실시간 event를 담당하며, Supabase Broadcast는 혼합 버전 동안 기존
client를 지원하는 legacy 경로로만 유지한다. 클라이언트는 서버가 확인한 membership,
rate limit, entitlement 및 equipped state를 표현하며 이를 로컬 상태만으로 부여하지
않는다.

Firebase Realtime Database의 `/v2` namespace는 서버가 만든 access snapshot, room
revision, typing·pulse·projectile과 최신 메시지 전달 event를 위한 파생 실시간 계층이다.
영구 메시지와 권한의 원본을 대신하지 않으며 client가 access 또는 chat record를 직접
쓰지 않는다. V2 client는 Rules가 허용한 자신의 compact transient slot에만 쓴다.
Firebase Functions는 인증된 Supabase session을 Firebase custom token으로 교환하고,
chat UUID를 Postgres transaction에 먼저 저장한 뒤 전달 event를 발행한다. 응답 유실처럼
commit 여부가 불명확한 전송은 같은 UUID를 조회해 조정하고 자동 재전송하지 않는다.

혼합 버전 기간에는 기존 client가 chat·typing·pulse·projectile의 Supabase Realtime 경로를
그대로 사용하고, selector가 허용한 v2 client는 같은 기능을 Firebase로만 발행·수신한다.
Client는 선택된 transport 한 곳에만 발행하며 양쪽에 동시에 쓰지 않는다. 서버의 양방향
compatibility bridge가 Firebase event를 legacy Supabase plane으로, legacy event와 message
변경을 Firebase plane으로 전달해 업데이트 전후 client의 상호운용을 유지한다. Presence는
두 버전 모두 Supabase에 남는다.

인증된 server rollout selector가 명시적으로 허용한 session만 Firebase v2를 사용한다.
Bootstrap도 현재 session의 capability, frozen contract와 전역 kill-switch를 다시 확인하고
최대 5분의 server-enforced rollout lease를 custom token과 RTDB·callable 권한에 묶는다.
Client는 lease 만료 전에 selector를 다시 확인하고 token과 listener를 교체한다. 개별
session이나 cohort의 명시적인 Selector OFF는 legacy 지원 기간에 이 bounded refresh 안에
legacy transport로 전환한다. 이와 별도로 server-only Firebase 전역 emergency kill gate는
RTDB read와 callable write를 즉시 fail-closed로 막아, 이미 발급한 lease가 남아 있어도
전체 v2 traffic을 중단한다. Selector·bootstrap·권한 확인 실패를 legacy downgrade로
우회하지 않으며, 갱신하지 못한 lease는 서버와 client 양쪽에서 fail-closed로 끝난다.

Legacy bridge는 서버 runtime switch로 제어한다. 7일은 client rollout 뒤의 최소 관찰
기간일 뿐 자동 cutover나 제거 시점이 아니다. Adoption, old/new 상호운용, 오류와 비용을
확인한 뒤 운영자가 명시적으로 지원 종료를 결정하면 bridge와 legacy transient 지원을
비활성화할 수 있다. 이 전환은 새 client 재배포를 요구하지 않으며 Postgres 원본과
Supabase Presence를 제거하지 않는다.

클라이언트와 공개 웹에 필요한 계약만 이 저장소에 둔다. 비공개 schema, secret,
운영 데이터 또는 backend 배포 절차를 공개 문서에 복제하지 않는다.

## 콘텐츠와 commerce 흐름

승인된 asset 구조와 플랫폼 지원 범위는
[`assets/v1/manifest.json`](../assets/v1/manifest.json), 판매 상품 metadata와 플랫폼
식별자는 [`assets/v1/commerce-catalog.json`](../assets/v1/commerce-catalog.json)이
소유한다. 생성 스크립트가 이 원본에서 웹과 네이티브 mirror를 만들고 검증한다.
backend가 상품 변경을 필요로 하면 검토된 private source commit의 snapshot과
provenance를 별도로 받아 서버용 매핑을 생성한다. Source catalog 변경만으로 backend가
배포되지는 않는다.

## 배포 산출물

Mac App Store target version/build는 [`release/macos.json`](../release/macos.json)과
[`macos/SIDEY.xcodeproj/project.pbxproj`](../macos/SIDEY.xcodeproj/project.pbxproj)가
일치해야 한다. 이 계약은 App Store 게시 완료를 증명하지 않으며, 웹 다운로드는
App Store 제품 페이지로 연결한다.

Windows 공개 version은 [`release/windows.json`](../release/windows.json)이 소유한다.
네이티브 project 설정, Windows update manifest와 웹 download metadata는 검증되는
mirror다. 검증된 installer, release metadata, website output과 해당 release에 필요한
historical license material만 공개 `SIDEY` 저장소로 게시한다. 지원이 종료된 macOS Direct
release, 설치 자산과 태그는 공개 저장소에서 삭제하며 서명된 feed는 private source의
과거 기록으로만 보존하고 갱신하거나 게시하지 않는다.

## 권위 순서

1. source code, project settings, manifest와 catalog 같은 machine-readable source
2. 현재 제품 및 아키텍처 문서
3. 장기 decision 문서의 선택 이유
4. 일반 구현 이력을 보존하는 Git commit과 pull request

Decision 문서는 현재 구현이나 현재 제품 명세를 덮어쓰지 않는다. 서로 충돌하면
machine-readable source를 먼저 확인하고 현재 문서를 고친다.
