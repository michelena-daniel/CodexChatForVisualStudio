# Codex for Visual Studio

Version **0.1.14** fixes workspace inspection for Visual Studio solution paths with a trailing separator. To check it, open a solution with a nested project and ask Chat to locate its entry point and cite the file. The workspace reader can list the solution and read the project source while Windows shell sandbox setup is pending.

## Install and open Codex Chat

1. Install the official Codex CLI on Windows. The protocol targets CLI **0.169.0**. The primary IDE is **Visual Studio 2026 Community 18.10.3** on x64. Plan and worktree preparation were manually validated through 0.1.12. The 0.1.13 implementation and review workflow still needs an IDE check. The extension looks for `codex.exe` on PATH and understands the Windows npm installation layout. Check the installed version with `codex --version`.
2. Close Visual Studio, open `CodexForVS.Extension.vsix`, and choose the Visual Studio 2026 instance. Reopen the IDE and load a saved solution.
3. Select **View → Other Windows → Codex Chat**. Check the displayed solution directory, then click **Connect**. The extension starts the official local Codex App Server. If the CLI already has a ChatGPT login, the window displays **Connected and authenticated**. Otherwise, click **Sign In** and complete the official flow in your browser.
4. Ask “What does this solution do? Point me to its entry point.” Press **Enter** or click **Send**. Use **Shift+Enter** for a new line. Select message text and press **Ctrl+C** to copy it.
5. Ask a follow-up directly in the same window. **Cancel** interrupts a running turn. After **Codex turn interrupted** appears, you can send again. **New Conversation** starts a fresh thread. Changing solutions also clears the current conversation.

## Plan a change

Between turns, select **Plan** in the **Mode** selector and send your question. Codex can inspect the solution and use the discussion so far to identify files, steps, and verification. The plan streams into the transcript, where its final text replaces the draft. Stay in Plan for revisions or switch back to **Chat** to resume ordinary discussion. Switching modes keeps the thread, history, model, and selected approval policy. Asking to implement a plan still runs a read-only turn.

This release uses the native experimental collaboration mode of Codex CLI 0.169.0. The extension opts in during initialization and sends the mode with the thread’s existing model on `turn/start`. The CLI supplies the built-in planning instructions. To inspect this contract locally, run `codex app-server generate-json-schema --experimental --out <directory>`. [Official Codex App Server documentation](https://developers.openai.com/codex/app-server/)

**Quick check in Visual Studio 2026.** Open a disposable solution and discuss one specific change in Chat. Select Plan and ask “How would you implement what we just discussed?” Check for one plan that refers to that change. Ask “Can you simplify step two?” and then “Implement it.” Compare the solution files with their starting state, including any untracked files or existing edits.

## Prepare a Git worktree

Open a saved solution in a Git repository with at least one commit. In Codex Chat, click **Prepare worktree** beneath the solution path. This action needs Git for Windows (tested with 2.50.1) and works before connecting to Codex. The extension discovers the repository from the solution directory, resolves the full commit ID, and creates a detached worktree. Copy the displayed base commit or path to inspect it.

The prepared checkout contains the committed files. Staged changes, unstaged edits, untracked files, and unsaved editor buffers stay in the active solution. Git registers the linked worktree in its repository metadata and gives it a separate HEAD and index. Preparation runs locally with checkout hooks and external filters disabled. LFS files therefore contain their committed pointers and submodules are not initialized. [Git worktree documentation](https://git-scm.com/docs/git-worktree)

Each session has a directory and a sibling XML record under `%LOCALAPPDATA%\CodexForVS\sessions`. The record identifies the repository, base commit, worktree path, session ID, and state. Closing or switching solutions cancels preparation at a Git command boundary. A failed, cancelled, or interrupted session retains its record and any files or registration already created. After an abrupt IDE exit, the last recorded state may be Preparing or CheckingOut. Cleanup requires a separate explicit decision.

**Quick check in Visual Studio 2026.** Open a disposable Git-backed solution and edit a committed file without committing it. Click **Prepare worktree**. Compare the file at the displayed path with your open solution. The prepared copy should contain the committed version and the active file should still contain your edit. Run `git rev-parse HEAD` in the prepared directory to compare it with the displayed commit.

## Implement and review

1. Click **Prepare worktree**, then choose **Implement**. Select a model from the **Model** list or leave **CLI default** selected. Changing the model starts a new thread on the next message. Implement starts a separate thread in the displayed worktree and carries over the latest plan when you enter from the discussion.
2. Send the implementation request. If Codex reports that the Windows sandbox needs setup, click **Set up Windows sandbox**, complete the official Windows prompt, then retry. Each proposed PowerShell command opens a dialog showing the directory and full command. **Run once** authorizes that command inside the worktree sandbox. **Decline** returns control to Codex. The activity line and transcript show progress. **Cancel** interrupts the turn.
3. Expand **Review worktree changes** when the turn finishes. Select a file and click **Open diff** for Visual Studio’s comparison of the committed and proposed bytes. Binary comparisons show hexadecimal bytes. **Edit proposal** opens the review copy. Save edits and open its diff again.
4. Close the active solution’s editor for the selected file, then click **Accept file** or **Discard file**. Accept applies one addition, modification, deletion or rename. Discard removes that proposal from the pending list. Both decisions preserve the detached checkout for inspection. You can refresh after an interruption.

The review reads final working files and untracked additions outside Git’s ignore rules, and compares the worktree index with the base commit. If only the index has a change, the list labels it **Staged**. Git detects renames by similarity. An extensively rewritten move may appear as a deletion and an addition.

Acceptance checks the active branch and HEAD, staged changes, file contents, open editors, and the reviewed proposal. A conflict leaves that proposal for manual resolution. Accepting a file uses its saved bytes and leaves the active Git index alone. Each acceptance writes a recovery record in the session’s sibling `.xml.review` directory before publishing the file. If rollback needs attention, the error identifies the recovery record. The extension retains worktrees, baseline copies and review snapshots locally. Reopen the solution to prepare another session.

**Limits.** File review supports ordinary Windows files. Submodules, symbolic links, case-only renames, and executable-mode changes can require manual handling. Restoring packages or running tools that need network access, write outside the worktree, or need elevation can fail. Git worktrees share repository metadata. The Windows sandbox, restricted tool configuration, and command approval provide the execution boundary. Use trusted local Git and Codex installations. [Codex security](https://developers.openai.com/codex/security/) · [Visual Studio comparison service](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.interop.ivsdifferenceservice.opencomparisonwindow2?view=visualstudiosdk-2022)

**Quick check in Visual Studio 2026.** In a disposable committed solution, ask Implement to change two text files. Confirm that the active files still have their original contents. Open both diffs, accept one file, and discard the other. Run `git status --short` in the active solution. Only the accepted file should appear as a new unstaged change.

## Check Codex usage

After connecting or signing in, look beside the mode name in the footer. The usage button shows the Codex window with the lowest percentage remaining. Select it to see each reported window, its reset time in your local time zone, and the time of the last reading. Click **Refresh** to check again without sending a chat message. App Server notifications also update the display.

**Included usage limit reached** appears when the server reports it. If usage is unavailable, you can still chat. A failed refresh keeps the previous reading and timestamp. The panel does not change your billing settings or decide whether to send a message.

## Approval policy

The selector sets the App Server approval policy. Change it between turns. Your next message starts a fresh thread with that policy. Chat and Plan use a read-only sandbox. Implement sets the prepared worktree as its only workspace root and excludes temporary-directory write grants. All three modes disable sandbox network access and decline escalation.

| Setting | What happens on CLI 0.169.0 |
| --- | --- |
| **never** (default) | Codex can run sandbox-permitted commands without asking for approval. Use this for normal repository inspection. A command that needs elevation cannot proceed. |
| **untrusted** | In Chat and Plan, Codex can list directories and read files through the workspace inspection tool. Shell requests are declined. |

Codex can inspect regular files in the selected workspace even when Windows shell sandbox setup is pending. The extension disables native shell tools, hooks, plugins and apps and checks the thread’s MCP inventory before starting model work. Its command tool submits `command/exec` with an explicit sandbox. This avoids inheriting previously saved command-prefix approvals. Both policy choices require human confirmation for implementation shell commands. The sandbox restricts writes and command network access. Codex can still read accessible files and send relevant context to OpenAI. [Codex command rules](https://developers.openai.com/codex/rules/)

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

The Debug installer is `src\CodexForVS.Extension\bin\Debug\net48\CodexForVS.Extension.vsix`. The manifest also targets x64 VS 2022. The chat and Usage footer were validated in VS 2026 on 0.1.10.

- **Extension** hosts the in-process VSSDK/WPF tool window and reports the active solution directory.
- **Client** manages the Codex process, JSONL transport, authentication, threads, turns, and chat ViewModel.
- **Core** prepares and verifies detached worktrees, captures checkout baselines, discovers proposals, and applies reviewed whole-file changes with conflict and rollback checks.
- **Tests** exercise Client and Core with fakes and temporary local Git repositories. The suite requires Git for Windows and runs without a live Codex login.

## Quick acceptance check

In a disposable solution, ask Codex to remember a made-up nickname. In a second message, ask for the nickname without repeating it. Then ask for a long answer, click **Cancel** while it is running, wait for the interruption status, and send “Reply with RESUMED.” All three replies and the interruption should be visible in the same Visual Studio session.

If **Connect** fails, check `codex --version` in a fresh PowerShell and restart Visual Studio after installing the CLI or changing PATH. If sign-in is required, use **Sign In** with your eligible ChatGPT account. If **Send** is disabled, check that a saved solution is open and that the previous turn has finished. If a read is declined under **untrusted**, select **never** between turns and repeat the question in the new thread.
