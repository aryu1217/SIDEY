# 개발 후보 직접 검증

`dev-mac`은 Mac App Store판, `dev-window`는 Windows 설치 후보를 검증하는 원격 브랜치다.
플랫폼별 구현 커밋을 모으는 검증용 브랜치이며, main 통합·공개 릴리스·운영 배포를 뜻하지 않는다.

## macOS

`dev-mac`을 별도 worktree에 체크아웃하고 `macos/SIDEY.xcodeproj`의 `SIDEY`
scheme으로 빌드·실행한다. Direct scheme과 DMG/Sparkle 경로는 사용하지 않는다.
Sign in with Apple과 Keychain을 포함한 실제 로그인 검증에는 해당 App Store 앱의
개발 서명·프로비저닝이 필요하다. ad-hoc 테스트 빌드를 로그인 검증 완료로 간주하지 않는다.
자동 검사는 `./scripts/macos/tests/test_native.sh`로 수행한다.

`origin/dev-mac`에서 분기한 작업의 변경 경로·자동 검사·전체 diff를 검토하고 검증한
커밋만 `dev-mac`에 통합한다. 검증 도중 기준 브랜치가 바뀌면 최신 변경을 반영하고
영향받는 검사를 다시 수행한다. `scripts/skills/workflow.py finish`는 main 통합용이므로
이 단계에서 실행하지 않는다. 제품 문서 변경은 별도 `shared/*` 브랜치로 준비한다.

인계할 때는 정확한 `dev-mac` SHA, 자동 검사 결과와 `SIDEY` 빌드·실행 경로를
함께 전달한다. 사용자가 직접 테스트하고 통과를 확인하기 전에는 main에 통합하지 않는다.
main 통합 시에는 그동안의 Windows·웹 변경을 보존하고 병합 결과를 다시 검증한다.
App Store 심사 제출은 별도 사용자 승인으로 진행한다.

최근 기록 전송 성공·실패와 포커스 유지, 두 입력창의 초안 공유, 한글 조합·이모지·줄바꿈,
이전 페이지 추가와 스크롤 보존을 직접 확인한다. 조용히 모드에서 본문·typing이 즉시
숨겨지는지, 단일·더블클릭이 분리되는지, 단축키 충돌·반복 입력·해제가 처리되는지 확인한다.
다중 모니터 입력창 위치 저장·복원, 모니터 제거·해상도 변경과 이전 설정 호환도 확인한다.
기존 typing 지연·갱신·무입력 종료, 기절 만료 후 동작 복귀와 나무 정지 상태 보존을
회귀 확인한다. 사용자의 수동 확인 결과는 자동 검사 결과와 구분해 기록한다.

## Windows

Windows 호스트의 `dev-window` worktree에서 `windows/AGENTS.md`에 적힌
`dotnet restore`, `format`, `build`, `test` 순서를 실행한다. 앱 시작이나 게시
형식이 영향을 받았다면 `scripts/windows/tests/Test-WindowsBuild.ps1`로
게시 앱의 launcher/host 시작 smoke까지 확인한다. 실행 전후에 브랜치 HEAD와
작업 트리가 같은지 확인하고 명령·결과·정확한 SHA를 기록한다.

설치 후보가 필요하면 `dev-window`에 통합하기 전, 검증된 `windows/*` 작업
커밋에서
`python3 scripts/skills/workflow.py test-installer <task>`를 Windows 호스트에서
실행한다. 이 명령은 로컬 Setup을 Downloads에 복사하고 기존 SIDEY Setup을
제거한다. 평문 Setup이나 비밀 정보를 공개 이슈·로그·Release에 첨부하지 않는다.
다른 SHA에서 만든 설치기를 해당 브랜치의 검증 결과로 사용하지 않는다.
후보의 source version은 공개 manifest보다 앞설 수 있으며 공개 업데이트는 자동 게시하지 않는다.

새 설치와 기존 버전 업데이트, 시작 프로그램 OFF 보존, 일반/최대화 창, 다중 모니터와
작업 표시줄, 한글 조합·붙여넣기, 빠른 방 전환을 직접 확인한다.
두 기기로 500ms 미만 입력 취소·연속 입력·5초 무입력 typing 종료도 확인한다.
보안 화면·DRM·관리자 권한 창·모든 독점 전체화면 게임 위 표시를 보장하지 않는다.

## 결제 검증의 선행 조건

Windows 후보는 기존 24개 상품을 운영 Supabase와 PortOne으로 구매한다. 신규 상품은
추가하지 않는다. 후보 코드를 받는 것만으로 운영 서버·공개 checkout이 업데이트되지는 않는다.
비공개 backend의 Windows PortOne migration과 functions, 이 후보의 checkout 웹 코드가
함께 배포되어야 한다. 판매 활성화와 실제 결제·전액 환불 검증은 별도 운영 작업이다.
시크릿은 Supabase에만 보관하며 앱 빌드·소스·테스트 자료에 복사하지 않는다.

Google 전환은 기존 설치 위에 업데이트해 검증한다. Credential Manager나 앱 데이터를
지우지 않은 상태에서 로그인 안내가 먼저 표시되고, 연결 후 사용자 UUID·기존 방·기록이
유지되는지 확인한다. 취소·실패 후 재시도, 앱 재시작, 별도 PC의 같은 Google 로그인으로
복구되는지도 확인한다. 실제 사용자 계정을 초기화해서 전환 테스트를 대신하지 않는다.

운영 결제 테스트는 Google 로그인 후 미보유 상품 구매, 브라우저 결제, 앱 복귀와 재시작
후 소유권 유지까지 확인한다. 환불은 앱 버튼이 아니라 비공개 backend의 운영 환불 경로로
전액 취소와 소유권 회수를 확인한다. 판매 활성화는 계정별이 아닌 프로젝트 전체 설정이다.
LIVE 거래를 만든 뒤 테스트를 종료할 때는 판매만 잠그며, 기존 LIVE 거래의 웹훅·환불
검증을 위해 결제 환경을 TEST로 되돌리지 않는다. TEST 결제는 별도 staging에서 수행한다.
