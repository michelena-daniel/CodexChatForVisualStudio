## Product Goal

Build a local-first Visual Studio extension (VSIX) that embeds a dockable Codex chat experience.
Users authenticate through the official Codex App Server using their elegible ChatGPT subscription (not an OpenAI API key), plan and execute tasks from the chat,
and inspect proposed source changes inside Visual Studio before explicitly accepting, editing, or discarding them.

The primary design principle is developer ownership: never silently modify the user's active solution. Codex edits happen in an isolated Git worktree,
only reviewed changes are applied to the active working tree.

## Non goals for MVP

- Inline autocomplete/ghost text.
- Hosting models, proxying subcription tokens.
- Cloud services, databases, telemetry, or a paid backend.
- Claude integration (design provider boundaries for later, first we implement Codex only).
- Reimplementing Codex agent behavior.
- Automatic commiting, pushing, merging, or destructive Git cleanup.

## Required user experience

1 - Install VSIX. Open View > Other Windows > Codex Chat (exact menu location can follow SDK conventions).
2 - Detect the currently open solution and its Git repository. Show a useful state if no solution or Git Repo is available.
3 - Launch or connect to the official codex app-server executable. Use supported ChatGPT login and show authentication state without collecting password or tokens.
4 - Chat in a dockable tool window; support streaming responses, cancellation, and conversation history within the running session.
5 - Support explicit PLAN and IMPLEMENT workflows. Planning must not edit files on any circumstance.
6 - Support explicit current Codex model selection.
7 - Before implementation, create a disposable, isolated Git worktree at a known base commit. Point Codex at this worktree, not the user's active checkout.
8 - Show progress, commands, failures, and permission requests. Do not silently approve commands. Allow user to set the Codex approval mode.
9 - When execution ends, enumerate changed, added, deleted, and rename files, including untracked files; show a filist and a diff in visual studio. User will be able
to select any file with changes and see a diff like the one shown when VS experiences conflicts on merge.
10 - Much like the merge conflict view, each file diff will have, instead of the "Accept merge", a "Accept" or "Discard" button. User will be able to make edits before accepting
a diff. MVP can accept/discard whole files, later add hunks. Edits must be reviewable before acceptance.
11 - Apply only explicitly approved changes to the active checkout. Detect concurrent edits and conflicts, preserve all pre-existing user changes. Never silently overwrite or delete.
12 - On cancellation, crash, or restart, leave the active checkout intact and preserve enough metadata to recover or discard the isolated session.

## Architecture

Aim for a small, testable solution:

- CodexForVS.Extension: VSIX package, Visual Studio tool window, WPF UI/MVVM, solution/editor integration.
- CodexForVS.Client: official Codex App Server protocol transport, JSON-RPC, lifecycle, streaming notifications, approvals, cancellation, and authentication state.
- CodexForVS.Core: domain types and services for sessions, plans, worktree lifecycle, change enumeration, review, safe application, and conflicts. Avoid Visual Studio dependencies here.
- CodexForVS.Tests: xUnit tests for client and core using mocks, including failure and cancellation cases.

Keep dependency direction Extension -> Core/Client, and Core independent of VS SDK. Define narrow interfaces for external processes, filesystem, Git, and Codex protocol.
Do not add an HTTP API or server for the MVP.

## Critical technical rules

- Verify current official documentation before implementation. Check Visual Studio 2022/2026 VSIX SDK compatibility, tool-window API, supported .NET target frameworks,
 and Codex App Server protocol and authentication. Do not invent JSON-RPC method names or schemas. The exact protocol is version-sensitive.
 Prefer official docs and SDK samples; pin or document compatible versions.
 
- Use official codex app-server as the agent backend; do not invoke OpenAI model endpoints directly and do not extract/reuse ChatGPT credentials.
 Clearly distinguish subscription-based Codex usage from separately billed API usage.
 
- Prefer Visual Studio SDK + WPF/MVVM for a native dockable chat experience. Use async APIs and marshal UI updates to the VS UI thread as required.
 Never block the UI thread on process I/O or Git.

- For the Codex transport, implement newline-delimited JSON-RPC over stdio only if confirmed by the current protocol docs. Keep stdout strictly protocol data; send diagnostic logs to a separate channel.
 Handle partial reads, unsolicited notifications, errors, shutdown, process exits, and cancellation.
 
- Treat Codex and repository content as untrusted. Never automatically grant elevated permissions or execute arbitrary suggested commands outside the supported approval flow.

- Isolate all implementation writes, including generated files and command side effects, in a Git worktree. Use a safe sandbox/approval configuration consistent with this requirement.
 Git worktrees are not security sandboxes: document and mitigate potential access to paths outside the worktree, network access, and destructive commands.
 
- Create worktrees outside the repository's tracked source tree, with collision-resistant names. Record repository root, base commit, worktree path, session ID, and lifecycle state. Clean up only after confirmation and only owned paths;
 never delete user data.
 
- Worktree diffs must account for tracked changes, staged changes, untracked additions, deletions, renames, and binary files.
 Do not rely only on git diff to find newly created files.
 
- The active checkout may have uncommitted changes. Snapshot/record relevant file states before an implementation begins; validate them again before applying. A Git worktree starts from a commit, not uncommitted editor buffers.
 Explain this distinction to the user and handle unsaved editor documents explicitly.
 
- Never blindly run git apply, checkout, reset, or file-copy operations against the active checkout. Use a verified, conflict-aware apply strategy, preview the result, and require explicit user approval.
 If the target has changed, stop and show a conflict rather than overwriting it.
 
- Acceptance should be transactional as far as practical: preflight all selected files, stage writes in temporary files, preserve rollback material, and report partial failures accurately.
 Do not automatically stage/commit accepted files.
 
- Preserve file encoding, newline conventions, and binary content. Do not assume all files are UTF-8 text.

- Keep secrets out of logs, persisted session metadata, and UI transcripts where possible.

- Avoid unsupported claims about Visual Studio compatibility or subscription limits; document prerequisites and limitations.

## Development practices

- C# with nullable reference types enabled, async/await, dependency injection where useful, clear naming, small cohesive classes, and structured logging. 
Use explicit naming for variables instead of var when applicable.

- Prefer standard libraries and minimal dependencies. No unnecessary abstractions, no premature microservices, and no giant service classes.

- Use xUnit and Moq or simple fakes. Test protocol framing, reconnect/process failure, approval mapping, worktree creation and cleanup, diff discovery, conflict detection, cancellation, and safe apply.

- Keep UI logic out of code-behind where practical. Provide clear empty, loading, disconnected, authentication-required, running, review-ready, and error states.

- Every meaningful feature should include tests and an explanation of how to validate it manually in Visual Studio.

- Do not modify unrelated files. Do not silently change architecture or expand scope. Ask when a decision materially affects safety, billing, or user data.

## Suggested milestones and acceptance criteria

M0 — Discovery and skeleton
- Inspect environment, installed Visual Studio/SDK, Git, .NET, Node, Codex executable.
- Verify official documentation and protocol examples.
- Create solution/projects and a dockable placeholder tool window.
- Document build/run prerequisites and version limitations.
- Done: VSIX builds and tool window opens in an experimental Visual Studio instance, or clearly document why environment cannot validate it.

M1 — Codex chat
- Process host and protocol client, authentication status, thread lifecycle, streaming messages, cancellation, and errors.
- Done: A user can chat with their existing Codex login from the tool window; no API key or extra model API billing is required within plan allowances.

M2 — Plan and implement in isolation
- Plan-only workflow; safe worktree creation and Codex execution targeting that worktree; command approvals and progress.
- Model selection from UI available to user.
- Done: Implementing a change leaves the active checkout untouched.

M3 — Review and whole-file approval
- Discover all changed files and show native diff previews.
- Whole-file accept/discard and manual-edit path; conflict checks and safe application.
- Done: User can accept one changed file while rejecting another, with no silent overwrites.

M4 — Reliability and polish
- Session recovery, conflicts, cleanup, diagnostics, settings, documentation, robust tests.
- Done: Restart/cancel/failure paths do not damage the user's checkout.

Later
- Hunk-level acceptance, solution/editor context attachments, richer plan UI, multiple sessions, provider abstraction, packaging and marketplace publishing.

Workflow for Codex contributors
1. Read this file and the current repository before changes.
2. State assumptions and risks. Verify unknown APIs against official documentation or installed SDK sources.
3. Propose a short plan and implement only the current milestone.
4. Run builds/tests available in the environment; never claim tests passed without executing them.
5. Summarize files changed, design choices, test results, unresolved issues, and the next small step.
6. Do not attempt all milestones in one turn. Favor reviewable increments.

Initial task
Begin with M0 only. Verify technical feasibility and supported APIs, scaffold the solution, create a dockable placeholder tool window, and write a concise README.md
 explaining prerequisites, architecture, and how to launch the experimental Visual Studio instance.
Do not implement Codex messaging or worktree acceptance yet.