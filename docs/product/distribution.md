# 배포와 제공 채널

## 공개 상태의 source of truth

현재 제품 source와 배포 설정의 원본은 비공개 `sidey-app/SIDEY-source` 저장소다.
공개 `sidey-app/SIDEY` 저장소는 기존 website와 Windows update URL을 유지하는
배포 surface이며 현재 application source를 제공하지 않는다. 다만 계속 배포하는 과거
AGPL binary의 정확한 Corresponding Source archive와 license notice는 release별로 유지한다.

공통 HeadVer head·ISO yearweek와 플랫폼별 build number는
[`release/version.json`](../../release/version.json)이 소유한다. macOS의 App Store target
manifest와 Xcode version 설정, Windows의 공개 release manifest와 MSBuild version 설정은
이 원본에서 생성하고 검증한다. macOS metadata는 빌드 계약이며 App Store 심사·게시 완료를 뜻하지 않는다.
공개 설치와 업데이트 가능 여부는 App Store가 결정한다. 웹은 후보 version을 공개
version으로 표시하지 않고 App Store 제품 페이지로 연결한다.

Windows의 공개 version과 channel은 generated mirror인
[`release/windows.json`](../../release/windows.json)을 통해 기존 release 소비자에 제공한다.
사용자 표시와 update 비교는 같은 `{head}.{yearweek}.{windowsBuild}`를 사용하고,
binary FileVersion은 마지막 `0`을 붙인 MSIX-compatible version을 사용한다. 현재
배포는 계속 self-contained NSIS Setup EXE이며 MSIX package를 만들지 않는다. 과거
revision 기반 release의 tag와 업데이트 계약은 유지한다. 새 HeadVer release의 공개
update manifest는 구버전 client가 읽을 수 있는 기본 필드를 유지하며 증분 배포 metadata는
선택 필드로 추가한다. Windows target framework, minimum OS contract와
binary version은 [`windows/src/Sidey.App/Sidey.App.csproj`](../../windows/src/Sidey.App/Sidey.App.csproj)
및 관련 project files에 있다.

## macOS

macOS는 Mac App Store판만 개발·지원한다. 설치와 업데이트는 App Store가 담당하며
구매와 복원은 StoreKit 및 기존 서버 검증을 따른다. 직접 배포 DMG, Sparkle 업데이트와
Homebrew Cask는 지원하지 않는다. 기존 직접 배포판의 공개 GitHub Releases, 설치 자산과
연결 태그는 삭제했으며 재게시하지 않는다. 사용자 계정·메시지·구매 원본과 private source의
Git 이력은 보존한다. App Store 설치가 기존 직접 배포판의 Keychain 또는 sandbox storage를
자동으로 이전한다는 보장은 하지 않는다.

App Store archive 생성, submission, review와 게시 완료는 별도 단계다.

App Store client의 지원 환경은 Apple Silicon Mac의 macOS 26 이상이다. 최소 OS와
CPU architecture의 실행 가능한 계약은
[`Xcode project`](../../macos/SIDEY.xcodeproj/project.pbxproj)가 소유한다. 실제 설치
가능 여부는 App Store의 제공 상태에 따른다.

App Store listing은 한국어를 primary language로 유지하고 영어권 storefront용 영어,
일본어와 번체 중국어 localization을 함께 관리한다. Binary의 미지원 언어 영어 fallback과
App Store Connect의 primary language는 별개의 계약이다. Metadata와 IAP localization
동기화는 기본적으로 read-only diff이며, 명시적인 apply 옵션과 별도 release 승인이 모두
있을 때만 App Store Connect를 변경한다. Storefront availability는 기존 국가를 제거하지
않고 release별로 승인된 대상의 누락만 추가한다.

## Windows

Windows 앱은 시작할 때와 사용자가 요청할 때 새 버전을 확인한다. 업데이트가 있으면 설정의 업데이트 영역에 최신 버전과 다운로드 동작을 표시하고, 시스템 알림에서 그 영역으로 이동할 수 있다. 기본으로 켜진 `업데이트 자동 다운로드 및 설치` 옵션은 새 버전의 업데이트 파일을 내려받아 검증한 뒤 업데이트를 설치하고 앱을 다시 시작한다. 데이터 통신 연결에서는 자동 다운로드와 설치를 진행하지 않는다. 이 옵션을 끄면 사용자가 설정에서 다운로드와 설치를 시작할 수 있다. 자동 업데이트와 설정에서 시작한 수동 업데이트는 Windows 관리자 권한 승인 뒤 약관 동의나 다음·완료 버튼 없이 설치와 앱 재시작을 진행한다. 준비된 파일은 앱을 재시작해도 재사용하며, 새 버전이 정상 시작한 뒤 정리한다. Windows 업데이트가 완료되면 시스템 알림을 표시하며 변경 내용 페이지를 자동으로 열지 않는다. 변경 내용은 설정의 `업데이트 내용` 링크나 업데이트 완료 알림을 눌러 직접 확인할 수 있다.

트레이 메뉴에서 업데이트를 확인해 최신 버전으로 확인되면 메인 창을 열지 않고
트레이 알림만 표시한다. 새 업데이트가 있으면 업데이트 확인이 끝난 뒤 설정의
업데이트 영역을 연다. 종료 중이거나 확인하던 창이 닫힌 경우에는 다시 열지 않는다.

증분 업데이트는 최신 버전의 전체 파일 목록과 설치된 파일의 해시를 비교해 동일한 파일을
재사용하고 변경되거나 없는 파일만 내려받는다. 중간 버전을 차례로 설치하지 않고 최신
버전으로 직접 업데이트하며, 런타임이 바뀌면 해당 파일도 교체한다. 최신 목록에 없는
파일은 새 설치에 포함하지 않는다. 별도 staging 위치에서 새 버전 전체를 구성하고
검증한 뒤 기존 설치 교체 절차로 적용하며, 설정과 로그인 데이터는 보존한다. 증분
배포가 없거나 기존 설치를 안전하게 재사용할 수 없으면 전체 Setup으로 전환한다.
증분 기능을 지원하지 않는 기존 앱은 계속 전체 Setup으로 업데이트한다.

공개 업데이트 metadata는 전체 Setup URL과 해시를 유지하고 증분 배포가 있는 경우에만
추가 manifest URL과 해시를 제공한다. 증분 manifest는 업데이트 실행 파일과 압축
payload의 해시·크기, 파일별 해시·크기와 다운로드 범위를 기록한다. Pages 준비 단계는
증분 세 파일이 모두 있을 때만 이를 게시하며, 이름·버전·해시·크기와 파일 경로·범위를
검증한다. 최초 설치와 웹사이트 다운로드에는 계속 전체 Setup을 제공한다.

Windows는 native app, launcher, assets, 필요한 installer helper와 .NET·Windows App SDK
runtime을 포함한 self-contained Setup EXE로 배포한다. 현재 설치 경로는 사용자 PC의
shared runtime을 prerequisite로 내려받거나 설치·등록하지 않는다. Installer는 포함된
payload를 검증하고 staging한 뒤 기존 설치를 보존할 수 있는 transaction boundary에서
교체한다. 실패하면 기존 설치를 유지하거나 복원하고 최종 상태를 확인할 수 없는 경우를
별도로 구분한다.

구버전이 사용자 쓰기 가능 위치에 설치되어 있고 진행 중인 설치 transaction이 없으면,
Installer는 Program Files의 `SIDEY` 폴더에 설치한다. 새 설치와 등록이
완료된 뒤에만 이전 위치에서 알려진 구버전 배포 파일과 내용이 일치하는 파일을 자동으로 삭제한다.
수정된 파일이나 관련 없는 파일은 남겨 두며, 모두 정리하지 못하면 이전 경로와 수동 삭제
방법을 설치 화면에 안내한다. 이때 구버전 제거 프로그램은 실행하지 않는다. 설정과 로그인
데이터는 프로그램 설치 위치와 별도로 보관한다.

현재 self-contained 경로는 payload 준비, 실행 중인 process 종료, payload 적용과 Windows
등록 실패를 구분한다. Installer source에는 향후 framework-dependent 배포로 전환할 때
사용할 network·download·package·signature·shared-runtime dependency 오류 범주와 문구도
유지한다. 이 문구의 존재가 현재 설치 경로에서 shared runtime을 내려받는다는 뜻은 아니다.

설치·복구·제거 오류는 선택한 설치 언어로 무엇이 실패했는지와 기존 설치 상태를 먼저
설명하고, 빈 문단 뒤에 사용자가 취할 행동을 안내한다. 화면에는 Microsoft·Windows native
code와 구분되는 `0x51DE....` 형식의 8자리 hexadecimal 오류 코드와 진단 데이터 위치를
표시한다. Native code, 내부 예외, 실행 명령과 대상 경로는 진단 데이터에만 기록하며 진단
데이터는 메모장으로 열 수 있다. 문제가 계속되면 창의 screenshot과 진단 데이터를 첨부해
GitHub issue를 남기도록 안내한다. WindowsApps, SIDEY 설치 파일 또는 shared Microsoft
runtime의 수동 삭제를 해결책으로 안내하지 않으며, 특정 error code만 보고 별개의 runtime
제품이나 고정 patch version을 일반 해결책으로 제시하지 않는다.

Installer의 compiled helper가 payload transaction과 오류 정규화를 담당하며 사용자 PC에서
PowerShell script, `ExecutionPolicy Bypass` 또는 `taskkill.exe`를 호출하지 않는다. 새 공개
artifact, update metadata와 release note는 Windows release manifest와 같은 version을
사용한다. 공개 저장소에는 검증된 installer, 사용자용 release metadata와 해당 binary에
필요한 historical license material만 게시한다.

## 공개 웹과 release note

공식 website는 macOS 설치를 App Store로 연결한다. Windows는 공개 release artifact를
확인하고 실제 hash에서 download metadata를 생성한다. Private source에서 생성·검증한
정적 website 결과, 공개 정책, release metadata와 binary만 공개 `SIDEY`에 게시한다.
공개 제품·다운로드·상점·지원·정책·새로운 기능 페이지는
[`UI localization metadata`](../../assets/v1/ui-localizations.json)의 Windows 지원 언어를 제공한다.
하단의 언어 선택은 현재 페이지의 해당 언어판으로
이동한다. 상단 메뉴에는 GitHub 링크를, 하단 메뉴에는 지원 링크를 표시하지 않는다.
결제와 결제 결과 페이지는 기존 공통 도구 URL과 한국어 화면을 유지한다.
배송·환불 요약과 사업자 정보는 한국어 페이지 하단에만 표시한다. 지원 페이지는
GitHub 버그 신고 양식에 필요한 정보와 진단 데이터 확인 방법, 이메일 문의를 안내한다.
메인 다운로드에는 게시된 macOS 버전과 Windows 버전을 표시하며, 두 플랫폼의
릴리즈 정보 링크는 웹사이트의 해당 언어 새로운 기능 페이지로 연결한다.
새로운 기능 페이지는 macOS와 Windows의 2.0 이상 공개 릴리스만 표시한다. 각 플랫폼의
게시된 사용자 변경 내용을 언어별로 작성해 `새로운 기능`, `개선 사항`, `버그 수정`으로
구분하며, GitHub 릴리스 본문을 그대로 표시하지 않는다. macOS의 변경 내용도
웹사이트에서 직접 제공한다. 웹 릴리스 노트에는 PR 번호와 작성자를 표시하지 않는다.
`docs/releases/`는 사용자에게 보이는 각 platform release 결과를 기록하지만 현재
version의 source는 아니다. Store upload, public release, website deployment와 backend
deployment는 서로 별도 작업이며 각각 명시적인 승인과 evidence가 필요하다.

반복 가능한 준비·검증·게시 순서는 [release operation](../operations/release.md)을 따른다.
