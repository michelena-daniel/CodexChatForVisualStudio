# Codex for Visual Studio

An M0 scaffold for a local-first Visual Studio extension that will host the official Codex App Server in a dockable **Codex Chat** tool window. No App Server process, authentication, chat transport, Git worktree, or change-application behavior exists yet.

## Prerequisites

- Visual Studio 2026 Community 18.10+ with the **Visual Studio extension development** workload. Visual Studio 2022 17.14+ remains a supported secondary target.
- .NET Framework 4.8 targeting pack (the in-process WPF/VSSDK extension targets `net48`).
- .NET SDK 9.0.301 or a compatible later SDK, NuGet access, and Git.
- For M1 onward, the official Codex CLI and an eligible ChatGPT subscription. This project will use `codex app-server`; it will not ask for an OpenAI API key or reuse ChatGPT credentials.

The detected development machine has Visual Studio 2022 Community 17.14.7 and Visual Studio 2026 Community 18.10.3, .NET SDKs 9.0.301 and 10.0.401, Git 2.50.1, Node 22.18.0/npm 11.11.0, and Codex CLI 0.169.0. Neither installed Visual Studio instance currently reports the **Visual Studio extension development** workload or the Visual Studio SDK component.

In Visual Studio Installer, modify the **Visual Studio 2026 Community** instance and select **Visual Studio extension development** (`Microsoft.VisualStudio.Workload.VisualStudioExtension`). Its required components include the Visual Studio SDK (`Microsoft.VisualStudio.Component.VSSDK`), MSBuild, NuGet, the C# compiler/language services, and .NET Framework 4.8 SDK support. No additional workload is required for this M0 scaffold.

## Build and run

1. Open `CodexForVS.sln` in **Visual Studio 2026 Community 18.10.3**. Do not use the VS 2022 instance for this primary validation.
2. Restore NuGet packages and build the solution.
3. Set **CodexForVS.Extension** as the startup project and press `F5`. VSSDK starts the experimental instance belonging to the Visual Studio instance that is running the solution; therefore F5 from VS 2026 starts VS 2026 Experimental Instance, while F5 from VS 2022 starts VS 2022 Experimental Instance.
4. In the experimental instance, select **View > Other Windows > Codex Chat**. It shows the intentional M0 disconnected placeholder.

In VS 2026, **Extensions > Extension Development > Start Experimental Instance** and **Reset Experimental Instance** provide an explicit way to start or reset that same VS 2026 experimental profile. Use F5 to deploy the current Debug VSIX and attach the debugger.

From a Developer PowerShell, the equivalent validation commands are:

```powershell
dotnet restore CodexForVS.sln
dotnet test CodexForVS.sln --no-restore
msbuild CodexForVS.sln /restore /p:Configuration=Debug
```

Use Visual Studio’s F5 flow for deployment and debugging; M0 does not install a VSIX into the normal Visual Studio profile.

## Architecture

```
CodexForVS.Extension (net48, VSIX/WPF) --> CodexForVS.Client (netstandard2.0)
                                              --> CodexForVS.Core (netstandard2.0)
CodexForVS.Tests (net9.0) ------------------> Client/Core
```

- **Extension** contains only Visual Studio integration, a `ToolWindowPane`, and a small WPF/MVVM placeholder.
- **Client** is the future narrow boundary around the official, version-specific App Server protocol. It is deliberately empty in M0.
- **Core** will contain session, worktree, review, conflict, and safe-apply domain services with no Visual Studio dependency.
- **Tests** are independent of Visual Studio and will grow with Core and Client behavior.

The classic VSSDK/WPF tool window targets .NET Framework 4.8 and is compiled against the VS 2022 17.14 VSSDK. This is deliberate: traditional in-process VSSDK extensions require .NET Framework and are the established model for a native WPF `ToolWindowPane`. The manifest's `[17.0,)` installation target supports VS 2022 and VS 2026; VS 2026 evaluates the lower bound and retains 17.x API compatibility. This is verified compatibility, not a separate VS 2026 payload.

The installed VS 2026 is 18.10.3. Microsoft documents the first VSSDK specifically targeting VS 2026 for 18.12 and later, so an 18.x SDK payload cannot be claimed or tested on this machine yet. We will retain the 17.14 SDK until a concrete API requirement or a supported 18.12+ development environment justifies multi-targeting. The in-process model requires careful UI-thread usage and can affect IDE stability; those are known tradeoffs, not reasons to migrate before a requirement demands it.

## Planned security and isolation model

M2 will create each implementation session in an owned Git worktree outside the source repository at a recorded base commit. The active checkout will not be an execution target. M3 will enumerate tracked and untracked changes and require whole-file review plus explicit approval before a conflict-aware, transactional application attempt. No automatic staging, commits, pushes, cleanups, credential collection, or approval bypass is planned.

Git worktrees are not security boundaries. Before implementation support is added, the extension will document and constrain external-path access, network use, destructive commands, unsaved editor buffers, and the App Server approval flow.

## Current limitations

- The tool window is a disconnected visual placeholder only.
- There is no Codex authentication, chat, model selector, streaming, cancellation, command approval, worktree creation, diff discovery, or accept/discard action.
- The App Server protocol must be generated/verified against the installed CLI version when M1 begins (`codex app-server generate-json-schema`), rather than copied from this scaffold.
