# 메시징

## 영구 데이터와 실시간 신호

Postgres가 메시지의 source of truth다. 클라이언트는 UUID로 메시지를 낙관적으로
표시하되 저장 결과와 Realtime 알림을 같은 UUID로 조정한다. 확정 여부가 불분명한
실패도 같은 UUID로 확인하고 재시도해 중복 발송을 만들지 않는다. 메시지는 서버
보관 정책에 따라 생성 후 사흘이 지나면 삭제된다.

서버 rollout selector가 허용한 v2-capable build는 chat을 Firebase callable로 제출하고
Firebase RTDB의 최신 live event와 sequence hint를 구독한다. Callable도 Postgres에
같은 UUID를 먼저 저장하므로 Firebase delivery가 늦거나 다시 전달되어도 영구 메시지는
한 건이다. Bootstrap은 selector가 허용한 현재 login session에 최대 5분의 rollout lease를
발급하고, client는 만료 전에 selector 재등록과 Firebase token·listener 교체를 마쳐야
한다. Session이나 cohort의 Selector가 명시적으로 OFF이면 다음 bounded refresh에서
Supabase transport로 전환한다. 전역 emergency kill은 이미 발급한 lease와 무관하게
Firebase read와 callable write를 즉시 닫는다. Selector, bootstrap, lease 갱신 또는 권한
확인 실패를 이유로 더 약한 transport로 자동 downgrade하지 않으며, 만료되거나 전역에서
중단된 Firebase 연결은 전송과 수신을 모두 닫는다. 아직 v2를 선택하지 않은 build는
혼합 버전 지원 기간에 기존 Supabase 경로를 계속 사용한다.

Windows는 lease 만료보다 여유 있게 갱신을 시작하고 요청 시점을 분산한다. 초기화의
일시적인 실패에는 서버의 `Retry-After` 대기 시간을 지키며, 재시도 간격을 지수적으로
늘리고 분산한다. 이 대기는 방 전환이나 다른 전송 요청으로 우회하지 않는다. 갱신이
실패해도 아직 유효한 인증과 연결은 유지하며, 기존 권한으로 전송할 수 있다. 만료된
권한이나 새 권한 확인이 필요한 요청에는 기존 인증을 재사용하지 않는다. 구체적인
갱신 시점과 대기 정책은 [인증 제공자](../../windows/src/Sidey.Infrastructure/Authentication/FirebaseRealtimeCredentialProvider.cs)와
[실시간 리스너](../../windows/src/Sidey.Infrastructure/Realtime/FirebaseRealtimeListener.cs)에 정의한다.

Windows는 채팅의 `unauthenticated` 응답을 받은 인증 버전의 재사용을 차단하고 갱신을
요청한다. 같은 버전의 병렬 오류는 하나의 인증 복구로 합치며, 이전 버전의 늦은 오류는
새 인증을 무효화하지 않는다. 인증 복구가 메시지를 자동 재전송하지는 않는다. 실제
전송의 저장 여부가 불명확하면 기존 UUID로 확인한다. 권한 거절과 로그아웃에 대한
전송 차단은 유지하며, 중복 재연결 요청은 진행 중인 복구 작업을 공유한다.

Presence는 모든 버전에서 Supabase를 통해 연결·online·away 상태에 사용한다. v2를 선택한
client는 SIDEY 입력창의 typing, 캐릭터 pulse와 projectile 같은 저장하지 않는 event도
Firebase RTDB로 송수신한다. 기존 client의 Supabase Broadcast는 혼합 버전 지원을 위한
legacy 경로다. DB 변경 알림은 식별자만 전달하고 client가 RLS를 거쳐 row를 다시 읽는다.
연결이 복구되면 membership, presence와 최근 메시지를 다시 맞춘 뒤 online으로 전환한다.

혼합 버전 기간에는 client가 selector로 선택된 transport 한 곳에만 event를 발행한다.
v2 client는 chat·typing·pulse·projectile을 Firebase로만 발행·수신하고, legacy client는
같은 기능의 기존 Supabase 경로만 사용한다. 서버의 양방향 compatibility bridge가 두
plane 사이의 event를 전달하므로 업데이트 전후 client도 서로의 메시지와 상호작용을 볼
수 있다. Client가 양쪽에 동시에 발행해서 호환성을 만들지 않으며, bridge는 동일 event의
loop와 중복 표시를 막는다. Presence는 이 전환과 무관하게 계속 Supabase를 사용한다.

macOS v2 client는 권한이 확인된 참여 방(최대 5개)의 Firebase live listener를 유지한다.
비활성 방의 최신 snapshot은 전환 준비에만 쓰고 일시 이벤트를 화면에 재생하지 않는다.
방 전환 시 준비된 listener를 재사용하며 Presence 변경과 Postgres 최근 메시지 조회가
모두 성공해야 전환을 확정한다. 멤버십 또는 Firebase credential이 바뀌면 해당 listener를
정리하거나 새 권한으로 다시 구독한다.

Legacy bridge는 운영 runtime switch로 제어한다. Client rollout 뒤 최소 7일의 관찰 기간이
지나도 자동으로 끄지 않으며, adoption·old/new 상호운용·오류와 비용을 확인한 뒤 운영자가
명시적으로 지원 종료를 결정할 때만 비활성화한다. Bridge를 끄면 legacy client의 해당
실시간 기능 지원은 끝나지만 Postgres의 영구 데이터와 Supabase Presence는 유지된다.

## 작성과 표시

Windows는 연결 복구 중에도 메시지 작성과 방별 초안을 유지한다. 전송은 인증과 권한,
활성 방의 연결 준비가 확인된 뒤에 가능하며, 복구 중인 상태를 입력창에 표시한다.
준비되지 않아 서버 요청 전에 거절된 전송은 초안을 보존하고 최근 기록 조회를
생략한다. 전송 직전 연결이 끊기는 경우에도 transport의 최종 검사는 유지한다.

메시지 입력은 최대 200자·3줄을 허용한다. Enter는 전송,
Shift+Enter는 줄바꿈이다. Typing은 SIDEY 입력창의 lease 동안만 표시하고 실제 메시지
말풍선이 있으면 본문을 우선한다.

Windows typing lease는 실제 입력 내용 수정으로 시작하고 갱신한다. 입력창을 열거나
보관한 draft를 복원하는 것만으로 typing을 보내지 않으며, 입력을 멈추면 만료한다.
로컬 표시는 즉시 반영하고 원격 시작·갱신은 지연·간격 제한으로 묶어 전송한다. 추가
편집 없이 주기적으로 갱신하거나 실패한 typing을 자동 재시도하지 않는다. 전송, 입력창
닫기와 방 전환은 lease를 끝내며, 시작·종료 신호는 같은 순서 보장 경로로 전송한다.

Overlay는 발신자별 최신 메시지를 최대 두 개 표시하고 각각 일정 시간이 지나면
없앤다. 과거 기록 조회나 reconnect snapshot은 지나간 말풍선을 다시 재생하지 않는다.
조용히 모드는 메시지 본문 말풍선을 숨기고 통신 상태와 그룹별 미확인 수는 유지한다.
macOS에서는 이미 표시 중인 본문과 본인·친구의 typing 점도 즉시 숨기며, 해제할 때는
아직 유효한 typing만 다시 표시한다.

Windows의 조용히 모드도 메시지 본문 말풍선과 본인·상대방의 typing 표시를 모두
숨긴다. 메시지 수신, 최근 기록, 그룹별 미확인 수와 typing 송수신은 유지하며 모드를
해제하면 현재 유효한 상태만 다시 표시한다.

최근 기록은 server retention 범위에서 페이지로 읽는다. Pending과 failed 전송은 원래
방의 outbox에 남으며 다른 방의 draft를 바꾸지 않는다.

Windows 최근 기록 창은 앱 메모리에 있는 최신 페이지와 실시간 메시지를 먼저 표시한다.
같은 방을 캐시가 유효한 동안 다시 열면 첫 페이지 재조회를 생략하며, 실시간 메시지는
캐시 유효시간을 연장하지 않고 반영한다. 만료된 캐시는 서버 갱신을 기다리는 동안 표시하고,
갱신에 실패하면 기존 기록과 재시도 동작을 유지한다. 계정, 방 접근 권한이나 연결 상태가
바뀌거나 최근 메시지 삭제가 확인되면 캐시를 무효화한다. 서버 보존 기간이 지난 메시지는
표시하지 않으며 캐시는 디스크에 저장하지 않는다. 유효시간과 보관 한도는
[최근 기록 ViewModel](../../windows/src/Sidey.Presentation/ViewModels/HistoryWindowViewModel.cs)이 정의한다.

macOS와 Windows의 최근 기록은 최신 메시지가 아래에 오도록 표시한다. 처음 열거나 본인이
전송하면
하단으로 이동하고, 이전 기록을 읽는 중에는 새 메시지가 와도 읽던 위치를 유지한다.
상단에서 이전 페이지를 추가로 불러올 때도 읽던 위치를 보존한다.
Windows에서 과거 메시지를 읽는 동안 최근 메시지로 이동하는 버튼을 표시하고,
누르면 부드럽게 맨 아래로 이동한다.

macOS 최근 기록 창의 하단 고정 입력란과 overlay 입력창은 같은 방의 draft를 공유하며
동일한 전송·길이·줄바꿈 규칙을 따른다. 최근 기록에서 전송한 뒤에도 창과 입력 포커스를
유지하고 실패는 기록 창에 표시한다. 방 전환 중에는 두 입력창 모두 전송할 수 없다.
초안 동기화 자체는 typing 편집이 아니며, 입력창을 닫거나 포커스를 잃으면 해당 입력의
typing을 종료한다.

Windows 최근 기록창 하단에서도 활성 방에 메시지를 보낼 수 있다. 별도 입력창과
같은 길이 제한과 단축 입력 규칙을 사용하며 한글 조합을 확정하는 Enter는 전송하지
않는다. 전송 후에도 기록창은 열린 상태를 유지한다. 초안은 입력창별·방별로 보관하고,
실패한 전송은 원래 입력의 빈 초안만 복원하며 이후 작성한 내용은 덮어쓰지 않는다.
Windows 최근 기록에는 전송 중 문구를 표시하지 않고, 실패한 항목은 목록에서 숨긴다.
실제 전송 실패는 해당 입력창에 오류와 복원된 초안으로 알린다. 같은 메시지 ID가
서버 기록에 확인되면 실패로 표시하지 않는다.
포커스를 잃거나 방을 바꾸면 해당 입력의 typing을 종료한다.

Windows 입력창 단축키를 다시 누르면 입력창을 닫는다. 별도 입력창 왼쪽에는 회색 점을
3×3으로 배열한 손잡이가 있으며,
누른 채 드래그하면 입력창이 포인터를 따라 움직이고 놓은 위치를 로컬 설정에
저장한다. 재실행 시 저장한 모니터와 위치를 복원하며 모니터·배율·작업 영역 변경 시
화면 안으로 보정한다. 별도의 X 닫기 버튼은 없으며 Escape 또는 입력창 밖을 클릭해
닫을 수 있다.

Windows에서 자신의 캐릭터를 한 번 클릭하면 최대 100ms 뒤 입력창이 애니메이션과 함께 열린다.
그 안에 두 번 클릭하면 입력창을 열지 않고 기존 캐릭터 상호작용만 실행한다.
시스템이 더 긴 간격의 두 번 클릭을 허용하면 입력창이 먼저 나타나 두 번째
클릭이 캐릭터에 전달되지 않을 수 있다. 전달되면 이전 표시 상태로 복원한다.

## 보안 경계

Room membership, message rate와 transient event 권한은 서버가 확인한다. 로그에는
message body, token, 평문 invite code와 사용자·방·메시지 식별자 원문을 남기지 않는다.

## 실제 편집 기반 타이핑

Windows와 macOS App Store판은 실제 텍스트 편집(IME 조합·삭제·붙여넣기 포함)에
본인 typing을 즉시 표시하고 첫 원격 start를 첫 편집 500ms 뒤에 예약한다. 후속 편집은
최초 예약을 미루지 않는다. 전송·취소·전체 삭제·입력창 닫기·방 전환·탈퇴는 예약을
취소하고 해당 방의 상태를 종료한다. 시작을 발행하지 않은 짧은 입력은 stop도 발행하지 않는다.

갱신은 마지막 발행 이후 실제 편집이 있을 때만 최소 2초 간격으로 보낸다. 마지막 실제
편집 5초 뒤 종료하며 포커스·커서 이동, draft 복원, 타이머와 네트워크 완료는 편집이 아니다.
전송은 직렬화하고 내부 작업 세대로 오래된 예약을 취소한다. 실패한 일시 이벤트는 자동
재시도하거나 누적하지 않으며 다음 실제 편집에서 다시 시도할 수 있다. typing 실패만으로
방의 연결 상태를 바꾸지 않는다. 메시지 저장·재전송·복구 계약은 유지한다.

Typing start/stop의 domain 의미와 방 realtime epoch는 유지한다. v2를 선택한 client는
Firebase의 compact typing slot만 사용하고 legacy client는 기존 Supabase
typing_start/typing_stop 경로를 사용하며, 혼합 버전 동안 서버 bridge가 둘 사이를
중계한다. 정상 stop과 별도로 수신 6초 TTL이 유실된 상태를 정리한다. 기존 4초 TTL
수신자는 업데이트 전까지 약 0.5초 일찍 typing을 숨길 수 있다. 종료 시 타이머와 비동기
작업을 정리하며 전송할 수 없는 stop은 수신 TTL로 만료된다.
