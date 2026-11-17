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

    public async Task<CodexConversation> StartConversationAsync(string workspaceDirectory, CancellationToken cancellationToken)
    {
        if (Authentication.State != CodexAuthenticationState.Authenticated) throw new InvalidOperationException("Authenticate with Codex before starting a conversation.");
        if (string.IsNullOrWhiteSpace(workspaceDirectory)) throw new ArgumentException("A workspace directory is required.", nameof(workspaceDirectory));
        string fullWorkspaceDirectory = Path.GetFullPath(workspaceDirectory);
        if (!Directory.Exists(fullWorkspaceDirectory)) throw new DirectoryNotFoundException("The configured workspace directory does not exist.");
        JObject response = await RequireTransport().RequestAsync("thread/start", CreateThreadStartParameters(fullWorkspaceDirectory), cancellationToken).ConfigureAwait(false);
        string? threadId = (string?)response.SelectToken("thread.id");
        if (string.IsNullOrWhiteSpace(threadId)) throw new InvalidOperationException("Codex did not return a thread ID.");
        CodexConversation newConversation = new CodexConversation(threadId!, fullWorkspaceDirectory);
        lock (lifecycleGate) { ThrowIfDisposed(); conversation = newConversation; }
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
            activeConversation.State = ConversationTurnState.Starting;
            activeConversation.ErrorMessage = null;
            activeConversation.AssistantText = string.Empty;
            JObject response;
            try { response = await RequireTransport().RequestAsync("turn/start", CreateTurnStartParameters(activeConversation.ThreadId, activeConversation.WorkspaceDirectory, text), cancellationToken).ConfigureAwait(false); }
            catch
            {
                activeConversation.State = ConversationTurnState.Failed;
                activeConversation.ErrorMessage = "Unable to start the Codex turn.";
                PublishConversation(new CodexConversationEventArgs(CodexConversationEventKind.TurnFailed, activeConversation.ThreadId, null, activeConversation.ErrorMessage));
                throw;
            }
            string? turnId = (string?)response.SelectToken("turn.id");
            if (string.IsNullOrWhiteSpace(turnId)) { activeConversation.State = ConversationTurnState.Failed; activeConversation.ErrorMessage = "Codex did not return a turn ID."; throw new InvalidOperationException(activeConversation.ErrorMessage); }
            activeConversation.ActiveTurnId = turnId;
            activeConversation.State = ConversationTurnState.Running;
            Publish("running", "Codex is responding…");
            PublishConversation(new CodexConversationEventArgs(CodexConversationEventKind.TurnStarted, activeConversation.ThreadId, turnId));
        }
        finally { turnGate.Release(); }
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        await turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CodexConversation? activeConversation = CurrentConversation;
            if (activeConversation == null || string.IsNullOrWhiteSpace(activeConversation.ActiveTurnId) || (activeConversation.State != ConversationTurnState.Running && activeConversation.State != ConversationTurnState.Cancelling)) return;
            if (activeConversation.State == ConversationTurnState.Cancelling) return;
            activeConversation.State = ConversationTurnState.Cancelling;
            await RequireTransport().RequestAsync("turn/interrupt", new JObject { ["threadId"] = activeConversation.ThreadId, ["turnId"] = activeConversation.ActiveTurnId }, cancellationToken).ConfigureAwait(false);
        }
        finally { turnGate.Release(); }
    }

    private static JObject CreateThreadStartParameters(string workspaceDirectory) => new JObject { ["cwd"] = workspaceDirectory, ["sandbox"] = "read-only", ["approvalPolicy"] = "untrusted", ["developerInstructions"] = "Read-only Visual Studio chat: inspect and analyze only. Never edit files, apply patches, or request elevated permissions." };
    private static JObject CreateTurnStartParameters(string threadId, string workspaceDirectory, string text) => new JObject { ["threadId"] = threadId, ["cwd"] = workspaceDirectory, ["input"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }), ["sandboxPolicy"] = new JObject { ["type"] = "readOnly", ["networkAccess"] = false }, ["approvalPolicy"] = "untrusted" };

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
                case "account/login/completed": HandleLoginCompleted(parameters); break;
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

    private void HandleToolActivity(JObject? parameters, bool completed)
    {
        string? threadId = (string?)parameters?["threadId"]; string? turnId = (string?)parameters?["turnId"]; CodexConversation? activeConversation = CurrentConversation;
        if (activeConversation == null || activeConversation.ThreadId != threadId || activeConversation.ActiveTurnId != turnId) return;
        string itemType = (string?)parameters?.SelectToken("item.type") ?? "tool"; Publish("activity", itemType); PublishConversation(new CodexConversationEventArgs(CodexConversationEventKind.ToolActivity, threadId, turnId, completed ? itemType + " completed" : itemType));
    }

    private void HandleTurnCompleted(JObject? parameters)
    {
        string? threadId = (string?)parameters?["threadId"]; string? turnId = (string?)parameters?.SelectToken("turn.id"); CodexConversation? activeConversation = CurrentConversation;
        if (activeConversation == null || activeConversation.ThreadId != threadId || activeConversation.ActiveTurnId != turnId) return;
        string? status = (string?)parameters?.SelectToken("turn.status"); string? error = (string?)parameters?.SelectToken("turn.error.message"); activeConversation.ActiveTurnId = null;
        if (string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase)) { activeConversation.State = ConversationTurnState.Interrupted; PublishConversation(new CodexConversationEventArgs(CodexConversationEventKind.TurnInterrupted, threadId, turnId, error)); }
        else if (!string.IsNullOrWhiteSpace(error) || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)) { activeConversation.State = ConversationTurnState.Failed; activeConversation.ErrorMessage = error ?? "Codex turn failed."; PublishConversation(new CodexConversationEventArgs(CodexConversationEventKind.TurnFailed, threadId, turnId, activeConversation.ErrorMessage)); }
        else { activeConversation.State = ConversationTurnState.Completed; Publish("completed", string.Empty); PublishConversation(new CodexConversationEventArgs(CodexConversationEventKind.TurnCompleted, threadId, turnId)); }
    }

    private void HandleLoginCompleted(JObject? parameters)
    {
        string? loginId = (string?)parameters?["loginId"]; if (pendingLogin != null && loginId != null && pendingLogin.LoginId != loginId) return;
        bool success = (bool?)parameters?["success"] ?? false; pendingLogin = null;
        if (success) SetAuthentication(new CodexAuthenticationInfo(CodexAuthenticationState.Authenticated)); else SetAuthentication(new CodexAuthenticationInfo(CodexAuthenticationState.Failed, errorMessage: "Codex sign-in did not complete."));
    }

    private static CodexAuthenticationInfo ParseAuthentication(JObject response)
    {
        JObject? account = response["account"] as JObject; bool requiresOpenAiAuth = (bool?)response["requiresOpenaiAuth"] ?? true;
        return account != null && string.Equals((string?)account["type"], "chatgpt", StringComparison.OrdinalIgnoreCase) && !requiresOpenAiAuth ? new CodexAuthenticationInfo(CodexAuthenticationState.Authenticated, (string?)account["planType"]) : new CodexAuthenticationInfo(CodexAuthenticationState.AuthenticationRequired);
    }
    private static CodexAuthenticationInfo ParseUpdatedAuthentication(JObject? parameters) => string.Equals((string?)parameters?["authMode"], "chatgpt", StringComparison.OrdinalIgnoreCase) ? new CodexAuthenticationInfo(CodexAuthenticationState.Authenticated, (string?)parameters?["planType"]) : new CodexAuthenticationInfo(CodexAuthenticationState.AuthenticationRequired);
    private void SetAuthentication(CodexAuthenticationInfo value) { lock (lifecycleGate) authentication = value; AuthenticationChanged?.Invoke(this, value); Publish(value.State == CodexAuthenticationState.Authenticated ? "authenticated" : "authenticationRequired", value.State == CodexAuthenticationState.Authenticated ? "Signed in with Codex." : "Sign in with your ChatGPT account."); }
    private void HandleDisconnection(string message)
    {
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
            if (disposed) return; disposed = true; transportToDispose = transport; processToDispose = ownsProcess ? process : null;
            if (transport != null) { transport.MessageReceived -= OnMessage; transport.Faulted -= OnTransportFaulted; }
            if (process != null) { process.Exited -= OnProcessExited; process.StandardErrorReceived -= OnStandardErrorReceived; }
            transport = null; process = null;
        }
        transportToDispose?.Dispose(); processToDispose?.Dispose(); loginGate.Dispose(); turnGate.Dispose();
    }
}
