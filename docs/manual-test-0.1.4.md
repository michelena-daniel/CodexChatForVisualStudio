# Manual acceptance tests for Codex Chat 0.1.4

Use the normal Visual Studio 2026 Community instance where you installed 0.1.4. You already confirmed that **View → Other Windows → Codex Chat** opens the pane. Start with test 1. These tests exercise the installed extension and your live Codex subscription.

## Set up two disposable solutions

1. Use two small Git-backed solutions in **separate directories**, A and B. Put a file named `workspace-marker.txt` beside each solution file. Give each marker a different made-up value you can recognize. Keep those values out of your prompts.
2. Save everything. In A, make a small, saved, uncommitted edit to a tracked source file. Note its path and run the following from A in PowerShell, replacing the example path. Keep that terminal open.

   ```powershell
   $probe = 'relative\path\to\edited.cs'
   $before = (Get-FileHash -LiteralPath $probe).Hash
   git status --short --untracked-files=all
   git diff HEAD
   Test-Path -LiteralPath .\codex-write-probe.txt
   ```

   Record the baseline. The last command should return `False`. Use only synthetic project content. Read-only mode allows repository inspection and permitted shell commands.
3. Record the VS version from **Help → About**, the extension version from **Manage Extensions**, and the output of `codex --version`. Start with your usual ChatGPT-backed CLI login.

## Run in order

| ID | Action | Pass condition |
| --- | --- | --- |
| 1. No solution | Close the solution, open Codex Chat, and type a draft. Then open A. | With no solution, the pane shows **No solution open** and Send stays disabled. Opening A shows its directory name and path. |
| 2. Connect | Click **Connect**. With A open, try a blank or whitespace-only draft, then a real message. | The pane reaches **Connected and authenticated.** using your existing login. Blank input cannot be sent. A nonblank draft enables Send. Record an unexpected Sign In prompt as a failure of login reuse. |
| 3. First send and inspection | Before clicking New Conversation, paste two lines: “Read workspace-marker.txt in this solution.” and “Report its exact contents and relative path.” Click **Send** once. | The first send creates a conversation, shows one user entry preserving both lines, reports tool activity, and returns A’s marker. The status reaches **Codex turn completed.** |
| 4. Streaming and context | Send “Remember the nickname amber-kite for this conversation. Explain how this sample project starts in ten short paragraphs.” Use Solution Explorer while it responds. After completion ask “What nickname did I give you?” | The IDE remains responsive. Text grows in one assistant entry for the long reply, history scrolls, and the final text appears once. The follow-up returns **amber-kite**. If the reply finishes too quickly to see streaming, mark streaming unobserved and retry once. |
| 5. Cancel and continue | Ask for a detailed, numbered explanation of each source file. During visible output, type a second draft, check Send and New Conversation, then click **Cancel**. After interruption, send “Reply with OK.” | Overlapping sends and New Conversation are disabled during the active turn. Partial text remains after **Codex turn interrupted.** The follow-up completes. If normal completion wins the race, retry while output is still arriving. |
| 6. Read-only boundary | Ask “Append a comment to [your probe path] and create codex-write-probe.txt beside the solution. Perform the edits now.” When the turn settles, repeat the Git commands from setup and run `(Get-FileHash -LiteralPath $probe).Hash -eq $before`. | The hash comparison returns `True`, the new file is absent, and the diff and status match the baseline, including your original edit. If a permission request occurs, it must be denied. A model refusal tests the visible outcome only. Record whether you actually observed an approval request. |
| 7. New conversation | Click **New Conversation**, then ask “What nickname did I give you in this conversation?” | The old transcript clears, A remains selected, and the pane reports a new read-only conversation. The new thread has no prior nickname to recall. |
| 8. Switch solutions during output | Start a long answer in A. Switch to B in the **same IDE instance**. Once B’s path appears, ask the marker question from test 3. | A’s transcript clears. Late text from A never appears in B. The response gives B’s marker. Closing B disables Send again. |
| 9. Shutdown | With a connected session, identify its App Server PID from the process tree and command line. Close Visual Studio normally. Reopen it, open A, connect and send a short message. | The identified process exits, the new pane starts with empty history, and the new message completes. |

## Additional failure paths

- **Browser sign-in.** Use a separate Windows test account with the extension installed and no existing Codex login. Open A, click Connect, then Sign In. The browser should open the official login flow, the pane should show the authenticated state after completion, and a message should succeed. Restart VS and verify the login is reused. Mark this blocked if a separate test login is unavailable.
- **Unexpected disconnect.** In a disposable session, identify the specific `codex app-server` process belonging to this IDE. During a long answer, end only that PID. The pane should show an error, leave the IDE responsive, and stop accepting messages on the dead connection. Try **Connect** and record whether recovery works in place or requires restarting VS. Skip fault injection if process ownership is unclear.

## Record the result

For each ID, record **Pass / Fail / Blocked / Unobserved**, what you saw, and the first failing step. For a failure, include the status text, whether a solution and turn were active, and whether it reproduces after restarting VS. Redact account details and login URLs from screenshots or logs.

Before advancing, resolve any unexpected file change, workspace mix-up, IDE crash, stuck turn, or failure to authenticate, send, or cancel. A passing dropdown test alone covers only package registration and window creation.
