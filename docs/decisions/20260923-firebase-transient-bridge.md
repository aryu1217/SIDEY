# Firebase transient bridge와 legacy 지원 종료

- Status: Accepted
- Decided: 2026-09-23 KST
- Supersedes: [Firebase v2 혼합 버전 rollout](202609212322-firebase-v2-mixed-version-rollout.md)

## Context

Postgres는 메시지와 room 권한의 영구 원본이어야 하고 Presence는 Supabase의 연결 상태
기능을 계속 사용하는 반면, typing과 캐릭터 상호작용은 저장하지 않는 짧은 event다.
Firebase v2 전환의 목적은 새 client의 chat·typing·pulse·projectile 실시간 전달을
Firebase로 옮겨 Supabase Broadcast 의존과 전송량을 줄이는 것이다.

이미 배포된 client는 Supabase Realtime만 이해하므로 새 client가 Firebase에만 연결되면
업데이트 전후 사용자가 서로의 메시지와 일시 event를 보지 못한다. 반대로 이를 피하려고
새 client까지 계속 Supabase에 발행하면 전환 목적을 달성할 수 없다. Client가 두 plane에
동시에 발행하는 방식도 부분 성공, 중복 표시와 재시도 판단을 client마다 떠안긴다.

## Decision

- Postgres를 영구 메시지와 계정·room 상태의 유일한 source of truth로 유지한다.
- Presence는 모든 client에서 Supabase Presence를 계속 사용하며 Firebase heartbeat를 만들지
  않는다.
- Selector가 Firebase v2를 선택한 client는 chat·typing·pulse·projectile을 Firebase로만
  발행·수신한다. Legacy client와 selector가 legacy를 선택한 client는 혼합 버전 지원 기간에
  기존 Supabase 경로만 사용한다. Client는 한 event를 두 plane에 동시에 발행하지 않는다.
- Firebase callable chat은 client message UUID를 idempotency key로 사용해 Postgres에 먼저
  저장한다. RTDB `/v2`는 server-owned access snapshot과 revision, 허용된 client transient
  slot, 최신 chat event와 sequence hint를 위한 파생 계층으로 사용한다.
- 서버 compatibility bridge는 혼합 버전 동안 Firebase event를 legacy Supabase plane으로,
  legacy event와 message 변경을 Firebase plane으로 전달한다. 동일 event를 식별해 bridge
  loop와 client의 중복 표시를 막으며, 어느 한쪽 전달 실패가 Postgres 원본을 바꾸지 않는다.
- 인증된 server selector가 protocol과 frozen client contract를 확인한 session만 Firebase
  v2를 선택한다. Bootstrap은 결정을 다시 검증하고 짧은 rollout lease를 Firebase token과
  RTDB·callable 권한에 묶는다. 권한·bootstrap·lease 갱신 실패를 legacy downgrade로
  우회하지 않고 fail-closed한다. 별도의 server-only emergency gate는 전체 v2 traffic을
  즉시 차단할 수 있다.
- Compatibility bridge와 legacy transient 지원은 서버 runtime switch로 제어한다. Client
  rollout 뒤 최소 7일을 관찰하지만 날짜 경과만으로 자동 종료하지 않는다. Adoption,
  old/new 상호운용, 오류와 비용을 확인하고 운영자가 명시적으로 승인한 뒤 switch를 끈다.
  지원 종료 뒤 legacy client는 해당 실시간 기능을 더 이상 보장받지 않지만 Postgres 원본과
  Supabase Presence는 유지한다.

## Consequences

혼합 버전 동안 서버는 두 event plane과 양방향 bridge를 운영하고 loop·중복·권한·backlog를
관측해야 한다. 대신 v2 client는 처음부터 실제 Firebase 경로를 사용하면서도 업데이트 전후
사용자가 상호작용할 수 있고, client의 이중 발행이나 재배포 없이 운영 switch로 legacy
지원을 종료할 수 있다.

Rollout 중에는 selector로 session이나 cohort를 legacy로 되돌릴 수 있고 전체 Firebase
사고는 emergency gate로 닫을 수 있다. Legacy 지원 종료 뒤에는 해당 rollback 경로를 다시
사용하기 전에 bridge와 legacy 지원 상태를 함께 복구해야 한다. Presence는 전환 대상이
아니므로 Supabase Realtime 연결 자체를 제거하지 않는다.
