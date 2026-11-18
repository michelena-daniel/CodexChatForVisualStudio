# Codex for Visual Studio

Version 0.1.9 adds a dockable **Codex Chat** window to Visual Studio. Ask questions about the open solution, read streamed answers, ask follow-ups in the same conversation, and interrupt a response. M1 is read-only.

## Install and open Codex Chat

1. Install the official Codex CLI on Windows. This version was tested with CLI **0.169.0** and **Visual Studio 2026 Community 18.10.3** on x64. The extension looks for `codex.exe` on PATH and understands the Windows npm installation layout. Check the installed version with `codex --version`.
2. Close Visual Studio, open `CodexForVS.Extension.vsix`, and choose the Visual Studio 2026 instance. Reopen the IDE and load a saved solution.
3. Select **View → Other Windows → Codex Chat**. Check the displayed solution directory, then click **Connect**. The extension starts the official local Codex App Server. If the CLI already has a ChatGPT login, the window displays **Connected and authenticated**. Otherwise, click **Sign In** and complete the official flow in your browser.
4. Ask “What does this solution do? Point me to its entry point.” Press **Enter** or click **Send**. Use **Shift+Enter** for a new line. Select message text and press **Ctrl+C** to copy it.
5. Ask a follow-up directly in the same window. **Cancel** interrupts a running turn. After **Codex turn interrupted** appears, you can send again. **New Conversation** starts a fresh thread. Changing solutions also clears the current conversation.

## Approval policy

The selector applies to the solution-bound Codex thread. Both options use a read-only sandbox, disable sandbox network access for turns, and decline elevation and file-change approval requests. The policy is saved in the Visual Studio profile. Change it between turns. Your next message starts a fresh conversation with the selected policy.

| Setting | What happens on CLI 0.169.0 |
| --- | --- |
| **never** (default) | Codex can run sandbox-permitted commands without asking for approval. Use this for normal repository inspection. A command that needs elevation cannot proceed. |
| **untrusted** | Codex can run commands the CLI classifies as trusted. Other commands can require approval, which M1 declines. Even a repository read can therefore be blocked. |

Tool activity appears above the transcript. The solution directory sets the working directory. Read-only sandboxing permits command execution and does not guarantee complete filesystem isolation or confidentiality. Codex sends the conversation and relevant project content to OpenAI to produce answers. M1 has no implementation workflow, worktrees, diff review, or change application.

## Authentication and billing

The extension uses JSON-RPC over local stdio to the official Codex CLI. Browser sign-in and credential storage belong to the CLI. The extension selects the built-in OpenAI model provider, omits `OPENAI_API_KEY` and `CODEX_API_KEY` from the child process environment, and checks for a ChatGPT account before each model turn. An API-key login cannot start a chat turn. There is no direct model API client, API-key entry, purchase action, or separately billed extension backend. [Official Codex authentication](https://developers.openai.com/codex/auth/)

**ChatGPT sign-in alone does not guarantee zero additional charges.** On eligible personal plans, Codex uses the included allowance first. Once exhausted, it can spend an existing credit balance. Automatic reload can purchase more credits. These are account settings. In **ChatGPT → Settings → Usage**, check your remaining allowance, existing credits, and automatic reload. To stay within the subscription, keep automatic reload off and wait for the allowance to reset when exhausted. Managed workspaces may have their own budgets. CLI 0.169.0 exposes no included-allowance-only switch on a thread or turn. [Codex plan usage](https://help.openai.com/en/articles/11369540-using-codex-with-your-chatgpt-plan) · [Credit use and automatic reload](https://help.openai.com/en/articles/12642688-using-credits-for-flexible-usage-in-chatgpt-personal-plans)

## Build and test

On Windows, install the **Visual Studio extension development** workload in the VS 2026 instance, including the Visual Studio SDK and .NET Framework 4.8 targeting pack. Use the .NET 9 SDK selected by `global.json` (9.0.301 or compatible 9.x feature band). From the repository root run

```powershell
dotnet restore CodexForVS.sln
dotnet build CodexForVS.sln --no-restore -v minimal
dotnet test CodexForVS.sln --no-restore -v minimal --blame-hang-timeout 30s
```

The Debug installer is `src\CodexForVS.Extension\bin\Debug\net48\CodexForVS.Extension.vsix`. The manifest also targets x64 VS 2022. This release's live acceptance was performed on VS 2026.

- **Extension** hosts the in-process VSSDK/WPF tool window and reports the active solution directory.
- **Client** manages the Codex process, JSONL transport, authentication, threads, turns, and chat ViewModel.
- **Core** defines shared conversation and policy types without a Visual Studio dependency.
- **Tests** exercise Client and Core with fakes. The unit suite runs without a live Codex login.

## Quick acceptance check

In a disposable solution, ask Codex to remember a made-up nickname. In a second message, ask for the nickname without repeating it. Then ask for a long answer, click **Cancel** while it is running, wait for the interruption status, and send “Reply with RESUMED.” All three replies and the interruption should be visible in the same Visual Studio session.

If **Connect** fails, check `codex --version` in a fresh PowerShell and restart Visual Studio after installing the CLI or changing PATH. If sign-in is required, use **Sign In** with your eligible ChatGPT account. If **Send** is disabled, check that a saved solution is open and that the previous turn has finished. If a read is declined under **untrusted**, select **never** between turns and repeat the question in the new thread.
