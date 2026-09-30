# Windows architecture

SIDEY for Windows is a native desktop app built with C#/.NET and WinUI 3. Its code is split so that a change to one screen stays out of storage, networking, and overlay code unless the behavior itself crosses those boundaries.

Current product behavior follows [`docs/product/`](../../../docs/product/overview.md), and
repository-wide responsibility boundaries follow
[`docs/architecture.md`](../../../docs/architecture.md). Source code and manifests own exact
values; consult [`docs/decisions/`](../../../docs/decisions/README.md) for long-lived rationale.

## Do not put every responsibility in UI code

A send button click handler can contain input validation, the server request, persistence, and the UI update. That keeps the flow in one place at first, but it also ties server changes to UI code and makes the behavior hard to test without a button. Understanding one feature then requires context from several unrelated concerns.

SIDEY separates those concerns with MVVM and a layered dependency structure.

```text
XAML View → ViewModel → focused capability interface → AppCoordinator
                                                    ├─ Core
                                                    ├─ Infrastructure
                                                    ├─ Overlay
                                                    └─ Platform.Windows
```

- A View renders the UI and forwards user input.
- A ViewModel manages UI state and commands.
- A capability interface is the boundary through which a ViewModel requests application behavior.
- `AppCoordinator` composes storage, realtime connections, the overlay, and Windows services.

This top-to-bottom flow limits how much context a reader has to hold at once.

## Each project has a different reason to change

Cohesion measures whether code that changes for the same reason lives together. Windows global shortcuts and Win32 window policies both depend on operating-system behavior, so they belong in `Sidey.Platform.Windows`. The room member limit can change without any Windows UI work, so that rule belongs in `Sidey.Core`.

| Project | Responsibility | Allowed dependencies |
| --- | --- | --- |
| `Sidey.Core` | Domain models, policies, and abstractions | .NET base libraries |
| `Sidey.Presentation` | ViewModels, commands, and service interfaces used by the UI | `Sidey.Core` |
| `Sidey.Infrastructure` | Authentication, backend, configuration, connectivity, persistence, and Realtime implementations | `Sidey.Core` |
| `Sidey.Overlay` | Pixel rendering, layout, and input interaction | `Sidey.Core` and required Windows boundaries |
| `Sidey.Platform.Windows` | Win32, windows, startup, deployment, and monitoring | `Sidey.Core` |
| `Sidey.App` | WinUI Views and object composition | Public boundaries from the projects above |

WinUI and Win32 types must not enter `Sidey.Core` or `Sidey.Presentation`. When lower layers know about UI types, changing the UI affects product rules and makes behavior harder to test without a window.

## A ViewModel knows only the capabilities it needs

Coupling is the distance a change spreads through the codebase. When every ViewModel receives an interface for the entire app, the history screen ends up knowing about overlay settings and store behavior. Even a small interface change can then affect unrelated ViewModels and their test doubles.

Each ViewModel receives a focused interface for the work it performs.

- `OnboardingViewModel` uses `IOnboardingCoordinator`.
- `MainWindowViewModel` uses `IMainWindowCoordinator`.
- `HistoryViewModel` uses `IHistoryCoordinator`.
- `ComposerViewModel` reports send requests, input-state changes, and close requests through events.

`AppCoordinator` implements `IMainWindowCoordinator` and `IHistoryCoordinator`. ViewModels call the interface for their own capabilities without knowing which lower-level components provide them. A lower-level implementation can then change without forcing the same edit through every ViewModel.

Before adding a feature, check whether it fits an existing focused interface. Give unrelated behavior its own capability interface and compose it in `AppCoordinator`. An application-wide interface that merely repeats the product name should wait until a real consumer needs that boundary.

## Keep the boundary between a View and its code-behind

ViewModel commands and state describe what users can do and when they can do it. XAML binds to both.

Keep only behavior owned by the View itself in code-behind, such as:

- focus movement and window activation;
- UI animation;
- WinUI dialogs; and
- policies tied to the View lifetime, such as window size and position.

Server or storage calls in code-behind are difficult to reuse from another screen and cannot be tested at the ViewModel boundary.

## Match folders and namespaces

If `StartupDiagnostics.cs` lives in the `Startup` folder, its namespace must be `Sidey.App.Startup`. The file location then tells readers where the type belongs, and `.editorconfig` checks the rule.

Keep one independently named primary type in each file. Each coordinator interface, for example, has its own file. Combining several interfaces makes the file's responsibility harder to judge when only one contract changes.

Keep XAML and its code-behind in the same `Views` folder and namespace. The XAML `x:Class` and the code-behind type name must always match.

## Use GlobalUsings only to reduce repetition

`GlobalUsings.cs` collects `using` directives that repeat across a project. The App, Infrastructure, Overlay, and Platform.Windows projects, along with platform tests, currently register the SIDEY namespaces they use often.

Global usings hide repeated references. Project references and constructor-injected interfaces still define the dependency boundaries. Follow these rules:

- Register only namespaces repeated across most of the project.
- Import a type used by only one file explicitly in that file.
- Before adding a global using, check whether it would hide an invalid layer dependency.
- Return to a local using if names conflict or the source of a type becomes unclear.

## Give asynchronous work an owner

Fire-and-forget code such as `_ = RunAsync()` can outlive shutdown or lose an exception. The component that starts a long-running operation keeps its task and cancellation token, cancels it during shutdown, and waits for it to finish.

At boundaries that cannot be awaited, such as UI events or native callbacks, the invoked method must own exception observation and cancellation. Work that uses an `IDisposable` or `IAsyncDisposable` resource must finish before the resource is disposed.

Do not create a bitmap, surface, or projectile buffer on every frame in the overlay rendering path. Even small allocations in a 30 FPS loop cause memory pressure and stutter during long-running sessions.

## Bind realtime read recovery to connection and room lifetimes

[`FirebaseRealtimeCredentialProvider`](../../src/Sidey.Infrastructure/Authentication/FirebaseRealtimeCredentialProvider.cs) shares credential acquisition for the same session and bounds the entire operation, including response bodies and persistence. Cancelling one caller ends only that caller's wait. Acquisition timeout enters retry cooldown; session reset cancels the credential generation and existing credential lifetime. A stalled error body retains the HTTP status and `Retry-After` already received.

[`SupabaseBackendGateway`](../../src/Sidey.Infrastructure/Backend/SupabaseBackendGateway.cs) retains a room in [`MessageRecoveryQueue`](../../src/Sidey.Infrastructure/Backend/MessageRecoveryQueue.cs) when a message identity or invalidation read fails transiently. It coalesces failures for the same room and serializes recovery reads across at most five joined rooms. Each read has a timeout; transient failures use exponential backoff, jitter, and `Retry-After`. A 401 refreshes the rejected session token once and retries only the GET. Repeated authentication rejection and permanent failures stop retrying.

Successful room leave or deletion, and changes to the room epoch, cancel that room's recovery. Subscription shutdown and sign-out cancel and await pending work. Recovery results carry the user ID captured at subscription start and the room epoch; `AppCoordinator` applies only results matching its current account and room. This also fences reads that react late to cancellation and results already in the output queue.

Firebase transient writes reuse the owning transport's `HttpClient` connection pool. Disposing a request-scoped RTDB wrapper keeps the pool alive; transport shutdown disposes it. SSE failures also retain server `Retry-After` for reconnect delays. `FirebaseRealtimeCredentialProviderTests`, `MessageRecoveryQueueTests`, `SupabaseRejectedSessionRecoveryTests`, and `FirebaseRtdbUrlBuilderTests` cover these boundaries.

## Tests verify boundaries

ViewModel tests check visible state and collaborator calls after a command runs. WinUI interaction tests cover bindings at runtime.

When a XAML file is itself a contract, a test may parse its XML structure and verify required properties such as `Command`. Such a test must not depend on formatting details such as whitespace, attribute order, or line breaks. Do not test C# method bodies as strings because ordinary refactoring would break those tests.
