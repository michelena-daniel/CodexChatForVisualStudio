using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodexForVS.Core;
using Newtonsoft.Json.Linq;

namespace CodexForVS.Client;

public sealed class CodexAppServerClient : ICodexAppServerClient
{
    private readonly object lifecycleGate = new object();
    private readonly SemaphoreSlim loginGate = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim turnGate = new SemaphoreSlim(1, 1);
    private ICodexProcess? process;
    private readonly bool ownsProcess;
    private IJsonRpcTransport? transport;
    private readonly WorktreeCommand worktreeCommand = new WorktreeCommand();
    private bool started;
    private bool disposed;
    private CodexAuthenticationInfo authentication = new CodexAuthenticationInfo(CodexAuthenticationState.Unknown);
    private CodexConversation? conversation;
    private TaskCompletionSource<string?>? turnStarted;
    private CodexLoginRequest? pendingLogin;

    public CodexAppServerClient()
    {
        process = new CodexAppServerProcess();
        ownsProcess = true;
        AttachProcess(process);
    }

    internal CodexAppServerClient(IJsonRpcTransport transport)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        AttachTransport(transport);
    }

    internal CodexAppServerClient(ICodexProcess process)
    {
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        AttachProcess(process);
    }

    public CodexAuthenticationInfo Authentication { get { lock (lifecycleGate) return authentication; } }
    public CodexConversation? CurrentConversation { get { lock (lifecycleGate) return conversation; } }
    public event EventHandler<CodexEventArgs>? EventReceived;
    public event EventHandler<CodexAuthenticationInfo>? AuthenticationChanged;
    public event EventHandler<CodexUsageSnapshot>? UsageUpdated;

    public async Task<CodexUsageSnapshot> ReadUsageAsync(CancellationToken cancellationToken)
    {
        if (Authentication.State != CodexAuthenticationState.Authenticated) throw new InvalidOperationException("Sign in to read Codex usage.");
        JObject result = await RequireTransport().RequestAsync("account/rateLimits/read",
            new JObject { ["excludeResetCreditDetails"] = true }, cancellationToken).ConfigureAwait(false);
        return CodexUsageSnapshot.Parse(result) ?? throw new InvalidDataException("Codex usage unavailable.");
    }
    public event EventHandler<CodexConversationEventArgs>? ConversationEventReceived;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IJsonRpcTransport activeTransport;
        lock (lifecycleGate)
        {
            ThrowIfDisposed();
            if (started) return;
            activeTransport = transport ?? (process ?? throw new InvalidOperationException("No Codex process is available.")).Start();
            if (transport == null) { transport = activeTransport; AttachTransport(activeTransport); }
            started = true;
        }
        try
        {
            await activeTransport.RequestAsync("initialize", new JObject
            {
                ["clientInfo"] = new JObject { ["name"] = "codex_for_visual_studio", ["title"] = "Codex for Visual Studio", ["version"] = "0.1.14" },
                ["capabilities"] = new JObject { ["experimentalApi"] = true }
            }, cancellationToken).ConfigureAwait(false);
            await activeTransport.NotifyAsync("initialized", new JObject(), cancellationToken).ConfigureAwait(false);
            JObject account = await activeTransport.RequestAsync("account/read", new JObject(), cancellationToken).ConfigureAwait(false);
            SetAuthentication(ParseAuthentication(account));
        }
        catch { lock (lifecycleGate) started = false; throw; }
    }

    public async Task<CodexLoginRequest> SignInAsync(CancellationToken cancellationToken)
    {
        await loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Authentication.State == CodexAuthenticationState.Authenticated) throw new InvalidOperationException("Codex is already authenticated.");
            if (pendingLogin != null) return pendingLogin;
            SetAuthentication(new CodexAuthenticationInfo(CodexAuthenticationState.SigningIn));
            JObject response;
            try { response = await RequireTransport().RequestAsync("account/login/start", new JObject { ["type"] = "chatgpt" }, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { SetAuthentication(new CodexAuthenticationInfo(CodexAuthenticationState.Cancelled)); throw; }
            catch { SetAuthentication(new CodexAuthenticationInfo(CodexAuthenticationState.Failed, errorMessage: "Unable to start Codex sign-in.")); throw; }
            string? loginId = (string?)response["loginId"];
            string? authUrl = (string?)response["authUrl"];
            if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(authUrl))
            {
                SetAuthentication(new CodexAuthenticationInfo(CodexAuthenticationState.Failed, errorMessage: "Codex returned an invalid browser login response."));
                throw new InvalidOperationException("Codex did not return a login ID and browser URL.");
            }
            pendingLogin = new CodexLoginRequest(loginId!, authUrl!);
            return pendingLogin;
        }
        finally { loginGate.Release(); }
    }

    public async Task SetupWindowsSandboxAsync(WorktreeSession session,CancellationToken token)
    {
        await session.Review.VerifyAsync().ConfigureAwait(false);
        await RequireTransport().RequestAsync("windowsSandbox/setupStart",new JObject { ["mode"] = "elevated", ["cwd"] = session.WorktreePath },token).ConfigureAwait(false);
    }

    public async Task<string[]> ListModelsAsync(CancellationToken token)
    {
        List<string> names = new List<string>();
        HashSet<string> cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            JObject page = await RequireTransport().RequestAsync("model/list", new JObject { ["modelProvider"] = "openai", ["cursor"] = cursor }, token).ConfigureAwait(false);
            foreach (JObject entry in (page["data"] as JArray ?? throw new InvalidDataException("Missing Codex model list.")).OfType<JObject>())
                if ((bool?)entry["hidden"] != true && (string?)entry["model"] is string model && !string.IsNullOrWhiteSpace(model)) names.Add(model);
            cursor = (string?)page["nextCursor"];
        } while (cursor != null && cursors.Add(cursor));
        return names.Distinct(StringComparer.Ordinal).ToArray();
    }



    public Func<string,Task<bool>>? ConfirmManagedCommandAsync { get; set; }

    public async Task<CodexConversation> StartConversationAsync(string workspaceDirectory, CancellationToken cancellationToken, string approvalPolicy = ReadOnlyApprovalPolicy.Never, WorktreeSession? worktree = null, string? model = null)
    {
        if (Authentication.State != CodexAuthenticationState.Authenticated) throw new InvalidOperationException("Authenticate with Codex before starting a conversation.");
        ConversationTurnState? previous = CurrentConversation?.State;
        if (previous == ConversationTurnState.Starting || previous == ConversationTurnState.Running || previous == ConversationTurnState.Cancelling)
            throw new InvalidOperationException("The previous turn is still active. Wait for it to finish or cancel it.");
        if (string.IsNullOrWhiteSpace(workspaceDirectory)) throw new ArgumentException("A workspace directory is required.", nameof(workspaceDirectory));
        string fullWorkspaceDirectory = Path.GetFullPath(workspaceDirectory);
        if (!Directory.Exists(fullWorkspaceDirectory)) throw new DirectoryNotFoundException("The configured workspace directory does not exist.");
        JObject parameters = CreateThreadStartParameters(fullWorkspaceDirectory, ReadOnlyApprovalPolicy.Validate(approvalPolicy));
        if (!string.IsNullOrWhiteSpace(model)) { parameters["model"] = model; parameters["allowProviderModelFallback"] = false; }
        if (worktree != null)
        {
            if ((worktree.State != WorktreeState.Ready && worktree.State != WorktreeState.ReviewReady) || !string.Equals(worktree.WorktreePath,fullWorkspaceDirectory,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Implementation requires the prepared worktree.");
            JObject readiness = await RequireTransport().RequestAsync("windowsSandbox/readiness",new JObject(),cancellationToken).ConfigureAwait(false);
            if ((string?)readiness["status"] != "ready") throw new SandboxSetupRequiredException();
            await worktree.Review.VerifyAsync().ConfigureAwait(false);

            parameters["sandbox"] = "workspace-write";
            parameters["runtimeWorkspaceRoots"] = new JArray(fullWorkspaceDirectory);
            parameters["approvalsReviewer"] = "user";

            parameters["developerInstructions"] = "Implement the user\u0027s request only in the detached worktree. Use worktree_command to inspect files and run commands with developer confirmation. Leave the final files for review. Do not stage, commit, alter Git metadata, contact external services or request elevation.";
        }
        await CodexThreadTools.ConfigureAsync(RequireTransport(),parameters,worktree != null,cancellationToken).ConfigureAwait(false);
        JObject response = await RequireTransport().RequestAsync("thread/start", parameters, cancellationToken).ConfigureAwait(false);
        string? threadId = (string?)response.SelectToken("thread.id");
        if (string.IsNullOrWhiteSpace(threadId)) throw new InvalidOperationException("Codex did not return a thread ID.");
        if (model != null && (string?)response["model"] != model) throw new InvalidOperationException("Codex could not use the selected model.");
        if (worktree != null)
        {
            string root = worktree.WorktreePath;
            bool SamePath(string? candidate) => !string.IsNullOrEmpty(candidate) && string.Equals(Path.GetFullPath(candidate!),root,StringComparison.OrdinalIgnoreCase);
            if (!SamePath((string?)response["cwd"]) || (string?)response.SelectToken("sandbox.type") != "workspaceWrite" ||
                (bool?)response.SelectToken("sandbox.networkAccess") == true || (string?)response["approvalPolicy"] != approvalPolicy ||
                (bool?)response.SelectToken("sandbox.excludeSlashTmp") != true || (bool?)response.SelectToken("sandbox.excludeTmpdirEnvVar") != true ||
                (string?)response["approvalsReviewer"] != "user" ||
                !(response["runtimeWorkspaceRoots"] is JArray roots) || roots.Count != 1 || !SamePath((string?)roots[0]) ||
                !(response.SelectToken("sandbox.writableRoots") is JArray writable) || writable.Any(path => !SamePath((string?)path)))
                throw new InvalidOperationException("Codex did not confirm the isolated worktree sandbox.");

        }
        await CodexThreadTools.VerifyAsync(RequireTransport(),threadId!,cancellationToken).ConfigureAwait(false);
        CodexConversation newConversation = new CodexConversation(threadId!, fullWorkspaceDirectory, approvalPolicy)
        {
            Worktree = worktree,
            Model = (string?)response["model"],
            ReasoningEffort = (string?)response["reasoningEffort"]
        };
        lock (lifecycleGate) { ThrowIfDisposed(); turnStarted?.TrySetResult(null); conversation = newConversation; }
        PublishConversation(new CodexConversationEventArgs(CodexConversationEventKind.TurnStarted, threadId!, null, "Conversation started."));
        return newConversation;
    }

    public Task SendMessageAsync(string text, CancellationToken cancellationToken) => SendAsync(text, cancellationToken);

    public async Task SendAsync(string text, CancellationToken cancellationToken, SessionMode mode = SessionMode.Chat)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A message is required.", nameof(text));
        if (mode != SessionMode.Plan && mode != SessionMode.Chat && mode != SessionMode.Implement) throw new ArgumentOutOfRangeException(nameof(mode));
        await turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CodexConversation activeConversation = RequireConversation();
            if ((mode == SessionMode.Implement) != (activeConversation.Worktree != null)) throw new ArgumentOutOfRangeException(nameof(mode),"Start a dedicated worktree conversation for Implement.");

            if (activeConversation.State == ConversationTurnState.Starting || activeConversation.State == ConversationTurnState.Running || activeConversation.State == ConversationTurnState.Cancelling) throw new InvalidOperationException("The current conversation already has an active turn.");
            // Other Codex clients share the CLI login. Check its current type before model work.
            JObject account = await RequireTransport().RequestAsync("account/read", new JObject(), cancellationToken).ConfigureAwait(false);
            CodexAuthenticationInfo currentAccount = ParseAuthentication(account);
            if (currentAccount.State != CodexAuthenticationState.Authenticated)
            {
                SetAuthentication(currentAccount);
                throw new InvalidOperationException("Sign in with ChatGPT before sending a message.");
            }
            if (activeConversation.Worktree != null) await activeConversation.Worktree.Review.BeforeImplementationAsync(cancellationToken).ConfigureAwait(false);
            activeConversation.State = ConversationTurnState.Starting;
            activeConversation.ErrorMessage = null;
            activeConversation.AssistantText = string.Empty;
            TaskCompletionSource<string?> ready = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (lifecycleGate) { ThrowIfDisposed(); turnStarted = ready; }
            try
            {
                JObject response = await RequireTransport().RequestAsync("turn/start", CreateTurnStartParameters(activeConversation, text, mode), cancellationToken).ConfigureAwait(false);
                activeConversation.LastMode = mode;
                string? turnId = (string?)response.SelectToken("turn.id");
                if (string.IsNullOrWhiteSpace(turnId)) throw new InvalidOperationException("Codex did not return a turn ID.");
                // The request acknowledgement can precede the server installing the active turn.
                // Hold turnGate until interrupt can target it, even if Cancel is clicked immediately.
                using (cancellationToken.Register(() => ready.TrySetCanceled()))
                {
                    if (await ready.Task.ConfigureAwait(false) != turnId) throw new InvalidOperationException("The Codex turn did not start.");
                }
            }
            catch
            {
                activeConversation.State = ConversationTurnState.Failed;
                activeConversation.ErrorMessage = "Unable to start the Codex turn.";
                PublishConversation(new CodexConversationEventArgs(CodexConversationEventKind.TurnFailed, activeConversation.ThreadId, null, activeConversation.ErrorMessage));
                throw;
            }
            finally { lock (lifecycleGate) turnStarted = null; }
        }
        finally { turnGate.Release(); }
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        await turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CodexConversation activeConversation;
            string? turnId;
            lock (lifecycleGate)
            {
                if (conversation == null || conversation.State != ConversationTurnState.Running || conversation.ActiveTurnId == null) return;
                activeConversation = conversation;
                turnId = conversation.ActiveTurnId;
                conversation.State = ConversationTurnState.Cancelling;
            }
            worktreeCommand.StopAll();
            try { await RequireTransport().RequestAsync("turn/interrupt", new JObject { ["threadId"] = activeConversation.ThreadId, ["turnId"] = turnId }, cancellationToken).ConfigureAwait(false); }
            catch
            {
                lock (lifecycleGate)
                {
                    if (activeConversation.ActiveTurnId != turnId) return; // Completion won the race.
                    if (activeConversation.State == ConversationTurnState.Cancelling) activeConversation.State = ConversationTurnState.Running;
                }
                throw;
            }
        }
        finally { turnGate.Release(); }
    }

    private static JObject CreateThreadStartParameters(string workspaceDirectory, string approvalPolicy) => new JObject { ["cwd"] = workspaceDirectory, ["modelProvider"] = "openai", ["sandbox"] = "read-only", ["approvalPolicy"] = approvalPolicy, ["developerInstructions"] = "You are in the open Visual Studio solution directory. For questions about this repository, use inspect_workspace to list directories and read relevant source files here. Use workspace_command for other read-only inspection as needed before answering. Cite the files you inspected. If a read fails, explain the specific obstacle. Never edit files, apply patches, or request elevated permissions. In Plan mode, use the conversation so far to propose or refine a plan. Identify relevant files, implementation steps, and how to verify the result. Ask any clarifying questions directly in chat." };
    private static JObject CreateTurnStartParameters(CodexConversation conversation, string text, SessionMode mode)
    {
        JObject parameters = new JObject
        {
            ["threadId"] = conversation.ThreadId,
            ["cwd"] = conversation.WorkspaceDirectory,
            ["input"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
            ["sandboxPolicy"] = new JObject { ["type"] = "readOnly", ["networkAccess"] = false },
            ["approvalPolicy"] = conversation.ApprovalPolicy
        };
        if (mode == SessionMode.Implement)
        {
            string root = conversation.Worktree!.WorktreePath;
            parameters["runtimeWorkspaceRoots"] = new JArray(root);
            parameters["sandboxPolicy"] = WorktreeCommand.Sandbox(root);
            parameters["approvalsReviewer"] = "user";
        }
        // A collaboration mode persists on the server. Explicitly restore default on leaving Plan.
        if (mode == SessionMode.Plan || conversation.LastMode == SessionMode.Plan)
        {
            if (string.IsNullOrWhiteSpace(conversation.Model)) throw new InvalidOperationException("Codex did not report the thread model required for Plan mode.");
            parameters["collaborationMode"] = new JObject
            {
                ["mode"] = mode == SessionMode.Plan ? "plan" : "default",
                ["settings"] = new JObject
                {
                    ["model"] = conversation.Model,
                    ["reasoning_effort"] = conversation.ReasoningEffort,
                    ["developer_instructions"] = null // Use the CLI's built-in instructions for this mode.
                }
            };
        }
        return parameters;
    }

    private void AttachProcess(ICodexProcess value) { value.Exited += OnProcessExited; value.StandardErrorReceived += OnStandardErrorReceived; }
    private void AttachTransport(IJsonRpcTransport value) { value.MessageReceived += OnMessage; value.Faulted += OnTransportFaulted; }
    private void OnProcessExited(object? sender, EventArgs eventArgs) => HandleDisconnection("Codex App Server exited unexpectedly.");
    private void OnStandardErrorReceived(object? sender, string line) { /* CLI stderr can contain environment values. Keep it outside the transcript. */ }
    private void OnTransportFaulted(object? sender, Exception error) => HandleDisconnection("Codex App Server connection ended.");

    private async void OnMessage(object? sender, JObject message)
    {
        try
        {
            string? method = (string?)message["method"];
            JObject? parameters = message["params"] as JObject;
            if (method == null) return;
            if (message["id"] is JToken id) { await HandleServerRequestAsync(method, id, parameters).ConfigureAwait(false); return; }
            switch (method)
            {
                case "windowsSandbox/setupCompleted": Publish("sandboxSetup",(bool?)parameters?["success"] == true ? "Windows sandbox ready. Retry Implement." : "Windows sandbox setup did not complete. Retry setup."); break;
                case "account/updated": SetAuthentication(ParseUpdatedAuthentication(parameters)); break;
                case "account/rateLimits/updated":
                    if (CodexUsageSnapshot.Parse(parameters) is CodexUsageSnapshot usage) UsageUpdated?.Invoke(this, usage);
                    break;
                case "account/login/completed": HandleLoginCompleted(parameters); break;
                case "turn/started": HandleTurnStarted(parameters); break;
                case "item/agentMessage/delta": HandleAssistantDelta(parameters); break;
                case "item/plan/delta": HandlePlanItem(parameters, false); break;
                case "item/completed" when (string?)parameters?.SelectToken("item.type") == "plan": HandlePlanItem(parameters, true); break;
                case "item/started": case "item/completed": HandleToolActivity(parameters, method == "item/completed"); break;
                case "turn/completed": HandleTurnCompleted(parameters); break;
            }
        }
        catch { Publish("error", "Unable to process a Codex event."); }
    }

    private async Task HandleServerRequestAsync(string method, JToken id, JObject? parameters)
    {
        IJsonRpcTransport connection = RequireTransport();
        CodexConversation? active = CurrentConversation;
        bool MatchesActiveTurn() => active != null && !disposed && ReferenceEquals(active,CurrentConversation) &&
            active.State == ConversationTurnState.Running && active.ThreadId == (string?)parameters?["threadId"] &&
            active.ActiveTurnId != null && active.ActiveTurnId == (string?)parameters?["turnId"];
        if (method == "item/tool/call")
        {
            JObject result = WorktreeCommand.Result(false,"Tool unavailable or turn ended.");
            if (MatchesActiveTurn() && (string?)parameters?["tool"] == "inspect_workspace" &&
                (string?)parameters?.SelectToken("arguments.path") is string path)
            {
                try
                {
                    int offset = (int?)parameters?.SelectToken("arguments.offset") ?? 0;
                    string output = await Task.Run(() => WorkspaceInspection.Read(active!.WorkspaceDirectory,path,offset)).ConfigureAwait(false);
                    result = MatchesActiveTurn() ? WorktreeCommand.Result(true,output) : WorktreeCommand.Result(false,"Turn ended.");
                }
                catch (FileNotFoundException) { result = MissingWorkspaceEntry(); }
                catch (DirectoryNotFoundException) { result = MissingWorkspaceEntry(); }
                catch (UnauthorizedAccessException) { result = WorktreeCommand.Result(false,"Windows denied read access to this workspace entry."); }
                catch (InvalidDataException) { result = WorktreeCommand.Result(false,"This tool requires a text file and a nonnegative offset."); }
                catch (ArgumentException) { result = WorktreeCommand.Result(false,"Use . for the workspace root or a path inside that directory."); }
                catch (IOException) { result = WorktreeCommand.Result(false,"Could not safely open this workspace entry. Check the listed path; links, Git metadata and nested repositories are restricted."); }
                catch { result = WorktreeCommand.Result(false,"The workspace inspection failed."); }
            }
            else if (MatchesActiveTurn() && (string?)parameters?["tool"] == (active!.Worktree == null ? "workspace_command" : "worktree_command") &&
                parameters?.SelectToken("arguments.command") is JValue value && value.Type == JTokenType.String)
            {
                string command = (string)value!;
                PublishConversation(new CodexConversationEventArgs(CodexConversationEventKind.ToolActivity,active!.ThreadId,active.ActiveTurnId,active.Worktree == null ? "Reading the workspace." : "Worktree command awaiting your decision."));
                try { result = await worktreeCommand.RunAsync(connection,active,command,ConfirmManagedCommandAsync,MatchesActiveTurn).ConfigureAwait(false); }
                catch { result = WorktreeCommand.Result(false,"The sandboxed command could not run. Use inspect_workspace with path . to list the workspace."); }
            }
            await connection.RespondAsync(id,result,CancellationToken.None).ConfigureAwait(false);
            return;
        }
        if (!ApprovalResponseFactory.TryCreateDenial(method,out JObject denial))
        {
            await connection.RejectAsync(id,CancellationToken.None).ConfigureAwait(false);
            Publish("diagnostic","Unsupported request declined: " + method);
            return;
        }
        Publish("diagnostic",method + " declined.");
        try { await RequireTransport().RespondAsync(id, denial, CancellationToken.None).ConfigureAwait(false); } catch { Publish("error", "Unable to send the read-only denial response."); }
    }

    private static JObject MissingWorkspaceEntry() => WorktreeCommand.Result(false,
        "No entry exists at that workspace path. List . with inspect_workspace, then follow the returned directory names.");

    private void HandleAssistantDelta(JObject? parameters)
    {
        string? threadId = (string?)parameters?["threadId"]; string? turnId = (string?)parameters?["turnId"]; string delta = (string?)parameters?["delta"] ?? string.Empty;
        CodexConversation? activeConversation = CurrentConversation;
        if (activeConversation == null || activeConversation.ThreadId != threadId || activeConversation.ActiveTurnId != turnId) return;
        activeConversation.AssistantText += delta; Publish("delta", delta); PublishConversation(new CodexConversationEventArgs(CodexConversationEventKind.AssistantDelta, threadId, turnId, delta));
    }

    private void HandlePlanItem(JObject? parameters, bool completed)
    {
        string? threadId = (string?)parameters?["threadId"];
        string? turnId = (string?)parameters?["turnId"];
        string? itemId = completed ? (string?)parameters?.SelectToken("item.id") : (string?)parameters?["itemId"];
        string? text = completed ? (string?)parameters?.SelectToken("item.text") : (string?)parameters?["delta"];
        CodexConversation? active = CurrentConversation;
        if (active == null || active.ThreadId != threadId || turnId == null || active.ActiveTurnId != turnId ||
            string.IsNullOrEmpty(itemId) || text == null) return;
        PublishConversation(new CodexConversationEventArgs(completed ? CodexConversationEventKind.PlanCompleted :
            CodexConversationEventKind.PlanDelta, active.ThreadId, turnId, text, itemId));
    }

    private void HandleTurnStarted(JObject? parameters)
    {
        string? turnId = (string?)parameters?.SelectToken("turn.id");
        string? threadId = (string?)parameters?["threadId"];
        lock (lifecycleGate)
        {
            if (turnStarted == null || conversation == null || conversation.State != ConversationTurnState.Starting ||
                conversation.ThreadId != threadId || string.IsNullOrWhiteSpace(turnId)) return;
            conversation.ActiveTurnId = turnId;
            conversation.State = ConversationTurnState.Running;
            turnStarted.TrySetResult(turnId);
        }
        Publish("running", "Codex is responding…");
        PublishConversation(new CodexConversationEventArgs(CodexConversationEventKind.TurnStarted, threadId!, turnId));
    }

    private void HandleToolActivity(JObject? parameters, bool completed)
    {
        string? threadId = (string?)parameters?["threadId"]; string? turnId = (string?)parameters?["turnId"]; CodexConversation? activeConversation = CurrentConversation;
        if (activeConversation == null || activeConversation.ThreadId != threadId || activeConversation.ActiveTurnId != turnId) return;
        string? itemType = (string?)parameters?.SelectToken("item.type");
        if (itemType != "commandExecution" && itemType != "mcpToolCall" && itemType != "dynamicToolCall" && itemType != "webSearch" && itemType != "fileChange") return;
        string label = itemType switch
        {
            "commandExecution" => "Workspace command",
            "webSearch" => "Web search",
            "fileChange" => "File change",
            _ => "Tool call"
        };
        if (activeConversation.Worktree != null && itemType == "commandExecution") label += " " + (string?)parameters?.SelectToken("item.command");
        string outcome = completed ? (string?)parameters?.SelectToken("item.status") switch
        {
            "declined" => "blocked by policy",
            "failed" => "failed",
            _ => "finished"
        } : "in progress";
        PublishConversation(new CodexConversationEventArgs(CodexConversationEventKind.ToolActivity, threadId, turnId, label + " " + outcome + "."));
    }

    private void HandleTurnCompleted(JObject? parameters)
    {
        string? threadId = (string?)parameters?["threadId"];
        string? turnId = (string?)parameters?.SelectToken("turn.id");
        string? status = (string?)parameters?.SelectToken("turn.status");
        string? error = (string?)parameters?.SelectToken("turn.error.message");
        CodexConversationEventKind kind;
        lock (lifecycleGate)
        {
            if (conversation == null || threadId != conversation.ThreadId || turnId == null || turnId != conversation.ActiveTurnId) return;
            conversation.ActiveTurnId = null;
            if (status == "interrupted") { conversation.State = ConversationTurnState.Interrupted; kind = CodexConversationEventKind.TurnInterrupted; }
            else if (status == "failed" || !string.IsNullOrWhiteSpace(error))
            {
                conversation.State = ConversationTurnState.Failed;
                conversation.ErrorMessage = error = error ?? "Codex turn failed.";
                kind = CodexConversationEventKind.TurnFailed;
            }
            else { conversation.State = ConversationTurnState.Completed; kind = CodexConversationEventKind.TurnCompleted; }
        }
        worktreeCommand.StopAll();
        if (kind == CodexConversationEventKind.TurnCompleted) Publish("completed", string.Empty);
        PublishConversation(new CodexConversationEventArgs(kind, threadId, turnId, error));
    }

    private void HandleLoginCompleted(JObject? parameters)
    {
        string? loginId = (string?)parameters?["loginId"]; if (pendingLogin == null || pendingLogin.LoginId != loginId) return;
        bool success = (bool?)parameters?["success"] ?? false; pendingLogin = null;
        // Only account/read and account/updated identify the authenticated account type.
        if (!success) SetAuthentication(new CodexAuthenticationInfo(CodexAuthenticationState.Failed, errorMessage: "Codex sign-in did not complete."));
    }

    private static CodexAuthenticationInfo ParseAuthentication(JObject response)
    {
        JObject? account = response["account"] as JObject;
        return account != null && string.Equals((string?)account["type"], "chatgpt", StringComparison.OrdinalIgnoreCase) ? new CodexAuthenticationInfo(CodexAuthenticationState.Authenticated, (string?)account["planType"]) : new CodexAuthenticationInfo(CodexAuthenticationState.AuthenticationRequired);
    }
    private static CodexAuthenticationInfo ParseUpdatedAuthentication(JObject? parameters) => string.Equals((string?)parameters?["authMode"], "chatgpt", StringComparison.OrdinalIgnoreCase) ? new CodexAuthenticationInfo(CodexAuthenticationState.Authenticated, (string?)parameters?["planType"]) : new CodexAuthenticationInfo(CodexAuthenticationState.AuthenticationRequired);
    private void SetAuthentication(CodexAuthenticationInfo value) { lock (lifecycleGate) authentication = value; AuthenticationChanged?.Invoke(this, value); Publish(value.State == CodexAuthenticationState.Authenticated ? "authenticated" : "authenticationRequired", value.State == CodexAuthenticationState.Authenticated ? "Signed in with Codex." : "Sign in with your ChatGPT account."); }
    private void HandleDisconnection(string message)
    {
        lock (lifecycleGate) turnStarted?.TrySetResult(null);
        CodexConversation? activeConversation = CurrentConversation;
        if (activeConversation != null && activeConversation.ActiveTurnId != null) { string turnId = activeConversation.ActiveTurnId; activeConversation.ActiveTurnId = null; activeConversation.State = ConversationTurnState.Failed; activeConversation.ErrorMessage = message; PublishConversation(new CodexConversationEventArgs(CodexConversationEventKind.Disconnected, activeConversation.ThreadId, turnId, message)); }
        Publish("error", message);
    }
    private CodexConversation RequireConversation() => CurrentConversation ?? throw new InvalidOperationException("Start a workspace-bound conversation before sending a message.");
    private IJsonRpcTransport RequireTransport() { lock (lifecycleGate) { ThrowIfDisposed(); return transport ?? throw new InvalidOperationException("Codex is not connected."); } }
    private void Publish(string kind, string text) => EventReceived?.Invoke(this, new CodexEventArgs(kind, text));
    private void PublishConversation(CodexConversationEventArgs args) => ConversationEventReceived?.Invoke(this, args);
    private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(CodexAppServerClient)); }

    public void Dispose()
    {
        IJsonRpcTransport? transportToDispose; ICodexProcess? processToDispose;
        lock (lifecycleGate)
        {
            if (disposed) return; disposed = true; turnStarted?.TrySetResult(null); transportToDispose = transport; processToDispose = ownsProcess ? process : null;
            if (transport != null) { transport.MessageReceived -= OnMessage; transport.Faulted -= OnTransportFaulted; }
            if (process != null) { process.Exited -= OnProcessExited; process.StandardErrorReceived -= OnStandardErrorReceived; }
            transport = null; process = null;
        }
        worktreeCommand.StopAll();
        transportToDispose?.Dispose(); processToDispose?.Dispose();
    }
}
