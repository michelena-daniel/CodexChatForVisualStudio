using System;
using System.IO;
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
            await activeTransport.RequestAsync("initialize", new JObject { ["clientInfo"] = new JObject { ["name"] = "codex_for_visual_studio", ["title"] = "Codex for Visual Studio", ["version"] = "0.1.0" } }, cancellationToken).ConfigureAwait(false);
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

    public async Task<CodexConversation> StartConversationAsync(string workspaceDirectory, CancellationToken cancellationToken, string approvalPolicy = ReadOnlyApprovalPolicy.Never)
    {
        if (Authentication.State != CodexAuthenticationState.Authenticated) throw new InvalidOperationException("Authenticate with Codex before starting a conversation.");
        if (string.IsNullOrWhiteSpace(workspaceDirectory)) throw new ArgumentException("A workspace directory is required.", nameof(workspaceDirectory));
        string fullWorkspaceDirectory = Path.GetFullPath(workspaceDirectory);
        if (!Directory.Exists(fullWorkspaceDirectory)) throw new DirectoryNotFoundException("The configured workspace directory does not exist.");
        JObject response = await RequireTransport().RequestAsync("thread/start", CreateThreadStartParameters(fullWorkspaceDirectory, ReadOnlyApprovalPolicy.Validate(approvalPolicy)), cancellationToken).ConfigureAwait(false);
        string? threadId = (string?)response.SelectToken("thread.id");
        if (string.IsNullOrWhiteSpace(threadId)) throw new InvalidOperationException("Codex did not return a thread ID.");
        CodexConversation newConversation = new CodexConversation(threadId!, fullWorkspaceDirectory, approvalPolicy);
        lock (lifecycleGate) { ThrowIfDisposed(); turnStarted?.TrySetResult(null); conversation = newConversation; }
        PublishConversation(new CodexConversationEventArgs(CodexConversationEventKind.TurnStarted, threadId!, null, "Conversation started."));
        return newConversation;
    }

    public Task SendAsync(string text, CancellationToken cancellationToken) => SendMessageAsync(text, cancellationToken);

    public async Task SendMessageAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A message is required.", nameof(text));
        await turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CodexConversation activeConversation = RequireConversation();
            if (activeConversation.State == ConversationTurnState.Starting || activeConversation.State == ConversationTurnState.Running || activeConversation.State == ConversationTurnState.Cancelling) throw new InvalidOperationException("The current conversation already has an active turn.");
            // Other Codex clients share the CLI login. Check its current type before model work.
            JObject account = await RequireTransport().RequestAsync("account/read", new JObject(), cancellationToken).ConfigureAwait(false);
            CodexAuthenticationInfo currentAccount = ParseAuthentication(account);
            if (currentAccount.State != CodexAuthenticationState.Authenticated)
            {
                SetAuthentication(currentAccount);
                throw new InvalidOperationException("Sign in with ChatGPT before sending a message.");
            }
            activeConversation.State = ConversationTurnState.Starting;
            activeConversation.ErrorMessage = null;
            activeConversation.AssistantText = string.Empty;
            TaskCompletionSource<string?> ready = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (lifecycleGate) { ThrowIfDisposed(); turnStarted = ready; }
            try
            {
                JObject response = await RequireTransport().RequestAsync("turn/start", CreateTurnStartParameters(activeConversation, text), cancellationToken).ConfigureAwait(false);
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

    private static JObject CreateThreadStartParameters(string workspaceDirectory, string approvalPolicy) => new JObject { ["cwd"] = workspaceDirectory, ["modelProvider"] = "openai", ["sandbox"] = "read-only", ["approvalPolicy"] = approvalPolicy, ["developerInstructions"] = "You are in the open Visual Studio solution directory. For questions about this repository, use the available read-only shell tools to list files and inspect relevant source and documentation here before answering. Cite the files you inspected. If a read fails, explain the specific obstacle. Never edit files, apply patches, or request elevated permissions." };
    private static JObject CreateTurnStartParameters(CodexConversation conversation, string text) => new JObject { ["threadId"] = conversation.ThreadId, ["cwd"] = conversation.WorkspaceDirectory, ["input"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }), ["sandboxPolicy"] = new JObject { ["type"] = "readOnly", ["networkAccess"] = false }, ["approvalPolicy"] = conversation.ApprovalPolicy };

    private void AttachProcess(ICodexProcess value) { value.Exited += OnProcessExited; value.StandardErrorReceived += OnStandardErrorReceived; }
    private void AttachTransport(IJsonRpcTransport value) { value.MessageReceived += OnMessage; value.Faulted += OnTransportFaulted; }
    private void OnProcessExited(object? sender, EventArgs eventArgs) => HandleDisconnection("Codex App Server exited unexpectedly.");
    private void OnStandardErrorReceived(object? sender, string line) => Publish("diagnostic", "Codex diagnostic: " + line);
    private void OnTransportFaulted(object? sender, Exception error) => HandleDisconnection("Codex App Server connection ended.");

    private async void OnMessage(object? sender, JObject message)
    {
        try
        {
            string? method = (string?)message["method"];
            JObject? parameters = (JObject?)message["params"];
            if (method == null) return;
            if (message["id"] is JToken id) { await HandleServerRequestAsync(method, id).ConfigureAwait(false); return; }
            switch (method)
            {
                case "account/updated": SetAuthentication(ParseUpdatedAuthentication(parameters)); break;
                case "account/rateLimits/updated":
                    if (CodexUsageSnapshot.Parse(parameters) is CodexUsageSnapshot usage) UsageUpdated?.Invoke(this, usage);
                    break;
                case "account/login/completed": HandleLoginCompleted(parameters); break;
                case "turn/started": HandleTurnStarted(parameters); break;
                case "item/agentMessage/delta": HandleAssistantDelta(parameters); break;
                case "item/started": case "item/completed": HandleToolActivity(parameters, method == "item/completed"); break;
                case "turn/completed": HandleTurnCompleted(parameters); break;
            }
        }
        catch { Publish("error", "Unable to process a Codex event."); }
    }

    private async Task HandleServerRequestAsync(string method, JToken id)
    {
        if (!ApprovalResponseFactory.TryCreateDenial(method, out JObject denial)) { Publish("diagnostic", "Unsupported server request denied: " + method); return; }
        Publish("denied", "Request denied by M1 read-only policy.");
        try { await RequireTransport().RespondAsync(id, denial, CancellationToken.None).ConfigureAwait(false); } catch { Publish("error", "Unable to send the read-only denial response."); }
    }

    private void HandleAssistantDelta(JObject? parameters)
    {
        string? threadId = (string?)parameters?["threadId"]; string? turnId = (string?)parameters?["turnId"]; string delta = (string?)parameters?["delta"] ?? string.Empty;
        CodexConversation? activeConversation = CurrentConversation;
        if (activeConversation == null || activeConversation.ThreadId != threadId || activeConversation.ActiveTurnId != turnId) return;
        activeConversation.AssistantText += delta; Publish("delta", delta); PublishConversation(new CodexConversationEventArgs(CodexConversationEventKind.AssistantDelta, threadId, turnId, delta));
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
        transportToDispose?.Dispose(); processToDispose?.Dispose();
    }
}
