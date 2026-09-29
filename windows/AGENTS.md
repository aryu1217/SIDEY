# SIDEY Windows Instructions

These instructions apply to `windows/**`. Read the repository-root `AGENTS.md` first. The root instructions also route Windows-owned files outside this directory, including `scripts/windows/**` and Windows-specific GitHub workflows, back to this file.

## Ownership and scope

- Make Windows implementation changes on a task-owned `windows/*` branch and worktree. Do not modify macOS implementation files, and do not fold shared product, protocol, asset-source, website, or repository-policy changes into a Windows commit.
- If Windows work requires a shared change, record or prepare that change separately under the repository workflow. Do not duplicate a shared contract inside `windows/**` to avoid the split.
- Keep the Windows client native in C#/.NET, WinUI 3, and Win32 platform services. Do not introduce another application framework or move WinUI/Win32 types into `Sidey.Core` or `Sidey.Presentation`. Follow [`docs/architecture.md`](docs/architecture.md) for the dependency boundaries.
- Support the repository's declared x64 baseline of Windows 10 1809 (build 17763) or later. Treat `windows/global.json`, project files, and central package files as the exact SDK, target-framework, and dependency contracts; do not restate their versions in new prose unless the version itself is the subject of the change.
- Preserve the existing self-contained distribution: the launcher starts the WinUI host under `Runtime`, which carries the app-local .NET and Windows App SDK files produced by `dotnet publish`. The NSIS installer, publish-layout scripts, and artifact tests define this contract. Do not repair a package by manually copying runtime files around those stages.

## Build and validation

GitHub Actions is disabled. Start with the narrowest affected test, then run the required non-installing solution checks locally from `windows/` so the .NET CLI applies `windows/global.json` when the change can affect the application:

```powershell
Push-Location windows
dotnet restore SIDEY.Windows.slnx
dotnet format SIDEY.Windows.slnx --no-restore --verify-no-changes --verbosity minimal
dotnet build SIDEY.Windows.slnx --configuration Release --no-restore
dotnet test SIDEY.Windows.slnx --configuration Release --no-restore --no-build
Pop-Location
```

Run the affected asset, PowerShell, publish-layout, prerequisite, installer, or startup checks locally when those contracts change. After integration of an app-affecting Windows task, run `python3 scripts/skills/workflow.py verify-windows <task>` on a Windows host. That command verifies the exact current `main` source and invokes `scripts/windows/tests/Test-WindowsBuild.ps1` to build, publish, check the payload, and smoke-test the launcher and host before recording runtime completion. Read the script before executing it: it restores packages, publishes an application, and starts the built executables. Release, installation, GUI, elevation, network, or machine-state validation must remain within the user's authorized scope. Never integrate a Windows commit while a required check is failing or while the commit under review differs from the commit that produced the evidence.

## Specialist skills

Repository-local skills have one canonical source under the root `.agents/skills` directory. Use the narrowest applicable Windows skill:

- [`code-review`](../.agents/skills/code-review/SKILL.md) for a substantive Windows code or distribution review;
- [`write-tests`](../.agents/skills/write-tests/SKILL.md) for choosing and implementing a Windows test boundary;
- [`windows-powershell`](../.agents/skills/windows-powershell/SKILL.md) for Windows-owned PowerShell changes;
- [`write-docs`](../.agents/skills/write-docs/SKILL.md) for evidence-based Windows developer documentation.

These skills supply expert workflows; they do not replace the always-on repository and path rules in `AGENTS.md`. For files under `windows/docs/**`, also follow [`docs/AGENTS.md`](docs/AGENTS.md).
