# Firebase v2 혼합 버전 rollout

- Status: Superseded
- Decided: 2026-09-21 23:22 KST
- Superseded by: [Firebase transient bridge와 legacy 지원 종료](20260923-firebase-transient-bridge.md)

## Context

Firebase RTDB는 짧은 실시간 전달에 적합하지만 SIDEY의 영구 메시지와 room 권한 원장을
대체할 수 없다. 동시에 이미 배포된 macOS와 Windows client는 Supabase Realtime의
Presence와 Broadcast를 사용하므로 새 client가 일시 event를 곧바로 Firebase 전용으로
옮기면 업데이트 전후 사용자가 서로의 typing과 캐릭터 상호작용을 보지 못한다.

운영 전환에는 빠른 rollback도 필요하지만, Firebase bootstrap 또는 권한 동기화 실패를
일반 네트워크 장애처럼 취급해 legacy 경로로 우회하면 revoke와 membership 경계가
약해질 수 있다. 달력상 유예 기간만으로 오래된 경로를 제거하는 것도 실제 adoption과
운영 안정성을 증명하지 않는다.

## Decision

- Postgres를 영구 메시지와 계정·room 상태의 유일한 source of truth로 유지한다.
- Firebase RTDB `/v2`는 server-owned access snapshot, revision, 최신 chat event와 sequence
  hint를 전달하는 파생 계층으로 사용한다. Client의 access·chat 직접 쓰기는 허용하지 않는다.
- Firebase callable chat은 client message UUID를 idempotency key로 사용해 Postgres에 먼저
  저장하고, 그 결과를 RTDB로 전달한다.
- 혼합 버전 동안 presence와 typing·pulse·projectile은 Supabase 단일 plane에 유지한다.
  기존 client와 v2-capable client는 이 plane을 통해 양방향 호환된다.
- 인증된 server selector가 protocol과 frozen client contract를 확인한 session만 v2를
  선택한다. Bootstrap은 selector 결정을 다시 검증하고 최대 5분의 rollout lease를
  Firebase custom token, RTDB read와 callable write 권한에 함께 묶는다. Client는 만료 전에
  selector 재등록, bootstrap, Firebase Auth와 listener 세대 교체를 완료한다.
- 개별 session이나 cohort의 명시적인 Selector OFF는 bounded refresh 안에 Firebase 자원을
  먼저 retire한 뒤 legacy로 전환할 수 있다. 별도의 server-only Firebase 전역 emergency
  kill gate는 RTDB read와 callable write마다 확인하여 이미 발급한 lease가 남아 있어도
  즉시 fail-closed로 전체 v2 traffic을 중단한다. 비활성화는 Firebase gate를 먼저 닫고
  Supabase selector를 OFF로 만들며, 재활성화는 Supabase 상태를 먼저 검증하고 Firebase
  gate를 마지막에 연다. 각 단계는 정확한 readback 없이 성공으로 취급하지 않는다.
- Selector, bootstrap, lease 갱신 또는 권한 실패는 legacy downgrade의 근거가 되지 않으며,
  갱신하지 못한 lease는 server와 client에서 fail-closed로 끝난다.
- Client rollout 시작을 T0로 기록한 뒤 최소 7일을 관찰한다. 기간 경과만으로 기본 경로를
  바꾸거나 legacy schema, RPC, trigger 또는 Broadcast를 삭제하지 않는다. Adoption,
  오류·backlog·비용과 old/new smoke를 확인하고 별도 승인한 뒤 후속 변경으로 다룬다.

## Consequences

두 실시간 구현을 일정 기간 함께 유지해야 하므로 client router, server outbox와 운영
관측 비용이 늘어난다. 대신 구버전 사용자는 업데이트 없이 계속 대화할 수 있고, 새 chat의
영구 저장은 전송 계층 장애와 분리된다. Rollout 중 일부 session이나 cohort는 server
selector로 bounded하게 legacy로 되돌리고, 전체 사고는 Firebase 전역 emergency gate로
즉시 차단한다. 권한 실패를 downgrade로 숨기지는 않는다. 짧은 lease로 bootstrap 우회와
이미 열린 listener의 장기 잔존을 막는 대신, v2 client는 sleep/wake와 일시적 정책 조회
실패에서도 lease 만료 전에 갱신하거나 정확히 연결 해제 상태로 전환해야 한다.

Windows가 v2-capable transport를 구현하기 전까지는 legacy Supabase 경로를 사용한다.
이 결정은 Windows 구현을 자동 승인하거나 T0를 시작하지 않는다.
