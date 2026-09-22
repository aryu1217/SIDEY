# Windows Firebase v2 구현 보고서

## 기준

- 확인일: 2026-09-22 (Asia/Seoul)
- Windows 작업 브랜치: `windows/firebase-v2-client`
- Windows 기준 SHA: `92088b671cebd073623701d15442b1ba3c8638e8`
- backend 기준 SHA: `82eb8af101aae5c5a489ce8c984bd32448802526`
- frozen protocol: `2`
- frozen contract SHA-256: `3c836b40cfc44437e9d069b84787cd3d8793026ce40de46d82b9ece79127b7e5`
- backend handoff: `sidey-backend/CLIENT_BACKEND_HANDOFF.md` (`BACKEND PUBLISHED — rollout OFF`)
- production RTDB: `https://sidey.asia-southeast1.firebasedatabase.app`

backend local checkout은 원격보다 뒤처져 있어 구현 판정에는 fetch한 `origin/main`의 위 SHA를 사용했다.
backend, Firebase Rules/Functions, Supabase production, macOS는 이 Windows 작업에서 수정하거나 배포하지 않았다.

## 최종 전송 구조

현재 frozen 계약은 모든 이벤트를 Firebase로 보내는 구조가 아니다.

- Supabase/Postgres: durable source of truth, 기존 인증, room/profile/commerce, 3일 chat history
- Supabase private Realtime: Presence와 혼합버전 기간의 typing/pulse/throw
- Firebase RTDB: active room의 server-owned latest chat event와 사용자 inbox hint
- Firebase callable: `sendRealtimeChat`
- Supabase selector RPC: 세션별 `legacy_supabase` 또는 `firebase_v2` 선택

`/v2/l/{room}/t|c|x`는 향후 전환용 예약 경로이며 현재 Rules가 client write를 거부한다. Windows도 해당
경로를 발행하거나 소비하지 않는다. 따라서 구형 client와 새 client는 7일 이상 같은 Supabase
Presence/transient plane에서 계속 통신한다.

## 구현 완료

- server-controlled rollout selector
  - Windows app version, protocol 2, frozen contract hash 등록
  - exact response 검증, monotonic TTL cache, kill switch, last-enabled refresh failure fail-close
  - 기본값은 v2-capable이며 `SIDEY_REALTIME_TRANSPORT=legacy`로 로컬 강제 rollback 가능
- Firebase bootstrap/Auth
  - Supabase bearer bootstrap, custom-token exchange, Secure Token refresh
  - exact 11-key bootstrap contract와 20자리 access revision 검증
  - 5분 rollout lease와 `refreshAfter`를 monotonic deadline으로 처리
  - 같은 UID의 Supabase session A/B 격리, rotated refresh token 원자 저장, logout/reset generation 폐기
- RTDB REST/SSE
  - canonical `.json?auth=` URL, HTTPS/host allowlist, 수동 307 검증, credential-safe 오류
  - incremental UTF-8/CRLF/multiline parser, `put`/`patch` snapshot, `cancel`/`auth_revoked` fail-close
- listener orchestration
  - `/v2/n/{uid}`와 active `/v2/l/{roomId}`만 유지
  - initial snapshot baseline, revision/sequence high-water, duplicate/reorder 무시, gap/history reconciliation
  - selector 갱신 후 bootstrap/token 갱신, old/new 최대 4-stream overlap 뒤 stale generation 폐기
  - inbox/room 중 한 SSE만 종료돼도 즉시 전체 stream set 재연결
- mixed-version transport
  - 기존 Supabase socket과 Firebase listener를 한 adapter에서 조정
  - Firebase가 OFF이면 legacy만 사용
  - Firebase가 ON이면 legacy Presence/transient는 유지하고 legacy chat notification은 중복 소비하지 않음
- callable chat
  - `{data:{b,i,r}}`와 `{result:{b,i,k?,n,t}}` strict contract
  - known non-commit와 commit-ambiguous 오류 분리, 자동 재전송 금지
  - ambiguous 결과는 같은 client UUID로 Postgres를 조회해 조정
- grant barrier
  - `create_room_v2`, `join_room_v2`, `set_equipped_cosmetic_v2`
  - 반환된 `accessRevision`을 `minimumAccessRevision` bootstrap에 전달한 뒤 listener 재연결
- credential storage
  - Windows Credential Manager의 generation/chunk/root-last 원자 저장
  - Supabase session과 Firebase refresh session 분리 및 logout/account switch terminal fence/삭제
- failure reconciliation
  - selector refresh 실패 뒤 stale enabled cache 재사용 금지, 15초 bounded retry
  - 30초 lease의 이미 지난 `refreshAfter`를 남은 lease 절반의 monotonic deadline으로 보정
  - callable 성공은 추가 PostgREST 조회 실패로 뒤집지 않음
  - commit-ambiguous chat은 최대 31.75초 동일 UUID targeted lookup 후 판정하며 조회 자체가 실패하면 pending UUID 유지

## 검증

```powershell
dotnet restore SIDEY.Windows.slnx
dotnet format SIDEY.Windows.slnx --no-restore --verify-no-changes --verbosity minimal
dotnet build SIDEY.Windows.slnx --configuration Release --no-restore
dotnet test SIDEY.Windows.slnx --configuration Release --no-restore --no-build
```

- Release build: PASS, warning 0, error 0
- tests: PASS 1,044 / failed 0 / skipped 0
  - Core 234
  - Presentation 176
  - Platform.Windows 634
- Firebase selector/callable/bootstrap/protocol/REST/SSE/listener/hybrid adapter는 fake HTTP/SSE fixture로 검증
- `git diff --check`: PASS

## 운영 및 설치 파일 상태

- backend production publication: 완료
- production selector: `enabled=false`, `killSwitch=true`, cohort `0`
- Firebase emergency gate: false
- T0와 7일 관찰: 미시작
- production release/signing/store 배포: 수행하지 않음
- Authenticode: 테스트 설치 파일은 서명하지 않음
- final test installer:
  - `build/windows/firebase-v2-final4-3c836b40/installer/SIDEY-Windows-x64-v2.0.0-Setup.exe`
  - bytes: `72,818,264`
  - SHA-256: `a5ab9a925eba47442e40bf71de9d13793767a54decbbe5ea670ca593f3e98dd0`
  - self-contained payload: 668 files / 243,084,360 bytes
  - installer transaction: 47 assertions PASS
  - uninstall manifest: 661 files / 140 directories PASS

따라서 이 binary는 Firebase v2 기능이 통합된 client지만, 현재 production 서버가 OFF를 반환하므로 실제 사용자
세션은 legacy Supabase를 사용한다. backend를 별도 승인 절차로 ON하면 설치 파일 교체 없이 선택된 세션부터
Firebase chat/hint plane이 활성화된다. permission denial을 이유로 자동 legacy downgrade하지 않는다.

## 남은 원격 검증

- rollout OFF 상태에서는 인증된 production Firebase SSE/callable 성공 경로를 실행할 수 없다.
- 실제 T0 전 canary 계정에서 selector ON, 두 SSE 연결, token rotation overlap, old↔new mixed-client smoke를 수행해야 한다.
- backend의 3,000명 × 600초 부하는 사용자 결정으로 수행하지 않았으며 App Check도 현재 미강제다.
- frozen `create_room_v2(p_name text)`에는 client operation UUID가 없다. DB commit 뒤 RPC 응답 자체가
  유실되면 client는 `room_id`와 원문 invite code를 정확히 복원할 수 없다. `current_firebase_access_revision()`은
  grant revision 수렴에는 쓸 수 있지만 생성 결과 식별자는 복구하지 못한다. 이 경로의 완전한 무중복 복구에는
  backend idempotency operation UUID와 결과 재조회 계약이 추가로 필요하다.

이 항목들은 installer 생성이나 OFF 상태의 구형 client 호환을 막지 않지만 production rollout ON의 별도 gate다.
