# Windows 아키텍처

SIDEY Windows는 C#/.NET과 WinUI 3으로 만든 네이티브 데스크톱 앱이에요. 이 문서는 화면 하나를 고칠 때 저장소, 네트워크, 오버레이까지 함께 건드리지 않도록 코드의 책임과 의존 방향을 정해요.

현재 제품 동작은 저장소 루트의 [`docs/product/`](../../docs/product/overview.md), 전체
책임 경계는 [`docs/architecture.md`](../../docs/architecture.md)를 따라요. 정확한 값은
source code와 manifest가 소유하고, 장기 선택의 이유는
[`docs/decisions/`](../../docs/decisions/README.md)에서 확인해요.

## 화면 코드에 모든 일을 넣지 않아요

메시지 전송 버튼의 클릭 처리에서 입력 검증, 서버 요청, 저장, 화면 갱신까지 모두 하면 처음에는 흐름이 한곳에 보여 간단해 보여요. 하지만 서버 요청 방식을 바꿀 때도 화면 코드를 읽어야 하고, 버튼 없이 같은 기능을 시험하기도 어려워져요. 읽는 사람이 한 기능을 이해하려고 알아야 할 맥락이 너무 많아지는 거예요.

SIDEY는 이 문제를 MVVM과 계층 분리로 다뤄요.

```text
XAML View → ViewModel → 작은 기능 인터페이스 → AppCoordinator
                                             ├─ Core
                                             ├─ Infrastructure
                                             ├─ Overlay
                                             └─ Platform.Windows
```

- View는 화면을 그리고 사용자 입력을 전달해요.
- ViewModel은 화면 상태와 명령을 관리해요.
- 기능 인터페이스는 ViewModel이 앱 기능을 요청하는 경계예요.
- `AppCoordinator`는 저장소, 실시간 연결, 오버레이와 Windows 서비스를 조합해요.

위에서 아래로 읽을 수 있는 코드는 가독성이 좋아요. 가독성이 좋다는 건 코드를 이해하기 위해 머릿속에 동시에 들고 있어야 할 정보가 적다는 뜻이에요.

## 프로젝트마다 바뀌는 이유가 달라요

응집도는 함께 바뀌는 코드를 가까이 두는 정도예요. 예를 들어 Windows 전역 단축키와 Win32 창 정책은 운영체제 동작이 바뀔 때 함께 고쳐질 가능성이 높으므로 `Sidey.Platform.Windows`에 둬요. 반대로 방 인원 제한 같은 제품 규칙은 Windows UI와 별개로 바뀌므로 `Sidey.Core`에 둬요.

| 프로젝트 | 맡는 책임 | 의존해도 되는 대상 |
| --- | --- | --- |
| `Sidey.Core` | 도메인 모델, 정책, 추상화 | .NET 기본 라이브러리 |
| `Sidey.Presentation` | ViewModel, 명령, UI가 사용할 서비스 인터페이스 | `Sidey.Core` |
| `Sidey.Infrastructure` | 인증, 백엔드, 설정, 연결, 저장, Realtime 구현 | `Sidey.Core` |
| `Sidey.Overlay` | 픽셀 렌더링, 배치, 입력 상호작용 | `Sidey.Core`, 필요한 Windows 경계 |
| `Sidey.Platform.Windows` | Win32, 창, 시작 프로그램, 배포와 모니터링 | `Sidey.Core` |
| `Sidey.App` | WinUI View와 객체 조립 | 위 프로젝트의 공개 경계 |

`Sidey.Core`와 `Sidey.Presentation`에는 WinUI나 Win32 형식이 들어가면 안 돼요. 아래 계층이 화면 형식을 알게 되면 UI를 바꾸는 일이 제품 규칙까지 번지고, 화면 없이 동작을 검증하기도 어려워져요.

## ViewModel은 필요한 기능만 알아요

결합도는 한 코드를 바꿨을 때 수정이 퍼지는 범위예요. 모든 ViewModel이 앱 전체 기능을 담은 인터페이스를 받으면 기록 화면도 오버레이 설정과 스토어 기능을 알게 돼요. 메서드 하나만 바꿔도 관련 없는 테스트 대역과 ViewModel까지 영향을 받을 수 있어요.

그래서 각 ViewModel은 가장 작은 인터페이스를 사용해요.

- `OnboardingViewModel`은 `IOnboardingCoordinator`를 사용해요.
- `MainWindowViewModel`은 `IMainWindowCoordinator`를 사용해요.
- `HistoryViewModel`은 `IHistoryCoordinator`를 사용해요.
- `ComposerViewModel`은 전송, 입력 상태 변경, 닫기 요청을 이벤트로 알려요.

`AppCoordinator`는 `IMainWindowCoordinator`와 `IHistoryCoordinator`를 구현해요. ViewModel은 여러 구현체를 직접 알지 않고 자기 기능에 필요한 인터페이스를 통해 기능을 사용해요. 그래서 아래쪽 구현이 바뀌어도 ViewModel까지 함께 고칠 일이 줄어들어요.

새 기능을 추가할 때는 먼저 기존의 작은 인터페이스에 자연스럽게 속하는지 확인해요. 관련 없는 기능이라면 새 기능 인터페이스를 만들고 `AppCoordinator`에서 조합해요. 제품명만 반복하는 앱 전체 인터페이스는 실제 사용자가 필요할 때까지 만들지 않아요.

## View와 code-behind의 경계를 지켜요

사용자가 실행하는 동작과 실행 가능 조건은 ViewModel의 명령과 상태로 표현해요. XAML은 그 명령과 상태에 바인딩해요.

code-behind에는 다음처럼 View 자체가 소유해야 하는 동작만 둬요.

- 포커스 이동과 창 활성화
- 화면 애니메이션
- WinUI 대화 상자
- 창 크기와 위치처럼 View 수명에 묶인 정책

code-behind에서 서버나 저장소를 직접 호출하면 같은 동작을 다른 화면에서 재사용하거나 ViewModel 단위로 시험하기 어려워져요.

## 폴더와 네임스페이스를 맞춰요

`StartupDiagnostics.cs`가 `Startup` 폴더에 있다면 네임스페이스도 `Sidey.App.Startup`이어야 해요. 파일 위치만 보고 책임을 예상할 수 있어 탐색 시간이 줄어들어요. `.editorconfig`가 이 규칙을 검사해요.

한 파일에는 독립적으로 이름 붙일 가치가 있는 주 형식 하나를 둬요. 예를 들어 코디네이터 인터페이스는 각각 별도 파일에 둬요. 여러 인터페이스가 한 파일에 모이면 한 계약만 바꿔도 파일 전체의 책임을 다시 살펴야 하기 때문이에요.

XAML과 code-behind는 같은 `Views` 폴더와 네임스페이스에 둬요. XAML의 `x:Class`와 code-behind의 형식 이름은 항상 일치해야 해요.

## GlobalUsings는 경계가 아니에요

`GlobalUsings.cs`는 프로젝트 안에서 반복되는 `using`을 한곳에 모아 파일의 잡음을 줄여요. 현재 App, Infrastructure, Overlay, Platform.Windows 프로젝트와 플랫폼 테스트가 각자 자주 쓰는 SIDEY 네임스페이스를 등록해요.

하지만 전역 using은 참조를 숨겨 줄 뿐, 의존 방향을 만들거나 보호하지 않아요. 실제 경계는 프로젝트 참조와 생성자에 주입한 인터페이스가 결정해요. 다음 기준을 지켜요.

- 프로젝트 대부분에서 반복되는 네임스페이스만 등록해요.
- 특정 파일에서만 쓰는 형식은 그 파일에서 명시적으로 가져와요.
- 새 전역 using을 추가하기 전에 잘못된 계층 의존을 감추는지 확인해요.
- 이름 충돌이나 코드의 출처가 불분명해지면 지역 using으로 되돌려요.

## 비동기 작업에는 소유자가 있어야 해요

백그라운드 작업을 `_ = RunAsync()`처럼 시작하고 잊으면 종료 뒤에도 작업이 남거나 예외가 관찰되지 않을 수 있어요. 장시간 작업은 소유자가 작업과 취소 토큰을 보관하고, 종료할 때 취소한 뒤 완료를 기다려요.

UI 이벤트나 네이티브 콜백처럼 `await`할 수 없는 경계에서는 호출된 메서드가 예외 관찰과 취소를 책임져야 해요. `IDisposable` 또는 `IAsyncDisposable` 자원을 사용하는 작업은 자원이 해제되기 전에 끝나야 해요.

오버레이 렌더링 경로에서는 프레임마다 비트맵, 표면, 투사체 버퍼를 만들지 않아요. 30 FPS 루프의 작은 할당도 오래 실행하면 메모리 압력과 끊김으로 이어져요.

## 실시간 조회 복구는 연결과 방 수명에 묶어요

[`FirebaseRealtimeCredentialProvider`](../src/Sidey.Infrastructure/Authentication/FirebaseRealtimeCredentialProvider.cs)는 같은 세션의 인증 획득을 공유하고, 응답 본문과 저장 완료까지 전체 작업에 시간 제한을 걸어요. 한 호출자의 취소는 그 호출자의 대기만 끝내요. 전체 시간 초과는 재시도 대기로 전환하고, 세션 초기화는 인증 세대와 기존 자격 증명의 수명을 취소해요. 오류 본문이 멈추더라도 이미 받은 HTTP 상태와 `Retry-After`는 유지해요.

[`SupabaseBackendGateway`](../src/Sidey.Infrastructure/Backend/SupabaseBackendGateway.cs)는 메시지 식별자 또는 무효화 이벤트 뒤 조회가 일시적으로 실패하면 [`MessageRecoveryQueue`](../src/Sidey.Infrastructure/Backend/MessageRecoveryQueue.cs)에 방을 남겨요. 같은 방의 실패는 하나로 합치고, 가입한 최대 다섯 방의 복구 조회를 한 번에 하나씩 실행해요. 각 조회에는 시간 제한이 있고, 일시적 오류에는 지수 백오프, jitter, `Retry-After`를 적용해요. 401은 거절된 토큰에 대해 세션을 한 번 갱신하고 GET만 다시 시도해요. 갱신 후에도 인증이 거절되거나 영구 오류가 나오면 반복하지 않아요.

방 탈퇴·삭제가 성공하거나 방 epoch가 바뀌면 해당 복구를 취소해요. 구독 종료와 로그아웃도 대기 작업을 취소하고 완료를 기다려요. 복구 결과에는 구독을 시작한 사용자 ID와 방 epoch를 싣고, `AppCoordinator`는 현재 계정과 방에 맞는 결과만 반영해요. 취소에 늦게 반응하는 조회나 출력 큐에 이미 들어간 결과도 이 경계에서 걸러요.

Firebase transient 쓰기는 소유 transport의 `HttpClient` 연결 풀을 재사용해요. 요청별 RTDB wrapper를 해제해도 풀은 유지하고, 소유 transport 종료 때 해제해요. SSE 연결 실패도 서버의 `Retry-After`를 보존해 재접속 대기에 적용해요. 이 경계는 `FirebaseRealtimeCredentialProviderTests`, `MessageRecoveryQueueTests`, `SupabaseRejectedSessionRecoveryTests`, `FirebaseRtdbUrlBuilderTests`에서 확인해요.

## 테스트는 경계를 확인해요

ViewModel 테스트는 명령 실행 뒤 보이는 상태와 협력자 호출을 확인해요. 런타임 바인딩은 WinUI 상호작용 테스트로 확인하는 게 가장 정확해요.

XAML 파일 자체가 계약인 경우에는 XML로 구조를 읽어 `Command` 같은 필수 속성을 확인할 수 있어요. 이때 공백, 속성 순서, 줄바꿈 같은 표현 방식에는 의존하지 않아요. C# 메서드 본문을 문자열로 검사하는 테스트는 리팩터링만으로도 깨지므로 작성하지 않아요.
