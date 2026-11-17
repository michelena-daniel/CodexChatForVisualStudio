using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CodexForVS.Core;

namespace CodexForVS.Client;

/// <summary>Presentation state for one read-only Codex conversation. It intentionally has no WPF or Visual Studio dependency.</summary>
public sealed class CodexChatViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ICodexAppServerClient client;
    private readonly IWorkspaceContext workspaceContext;
    private readonly IUiDispatcher dispatcher;
    private readonly CancellationTokenSource lifetimeCancellation = new CancellationTokenSource();
    private string inputText = string.Empty;
    private string workspacePath = string.Empty;
    private string statusMessage = "Disconnected.";
    private string? errorMessage;
    private ChatConnectionState connectionState = ChatConnectionState.Disconnected;
    private CodexAuthenticationInfo authentication;
    private CodexConversation? conversation;
    private ChatMessage? streamingAssistantMessage;
    private bool disposed;

    public CodexChatViewModel(ICodexAppServerClient client, IWorkspaceContext workspaceContext, IUiDispatcher dispatcher)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.workspaceContext = workspaceContext ?? throw new ArgumentNullException(nameof(workspaceContext));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        authentication = client.Authentication;
        Messages = new ObservableCollection<ChatMessage>();
        ConnectCommand = new AsyncCommand(ConnectAsync, () => ConnectionState != ChatConnectionState.Connecting && !disposed);
        SignInCommand = new AsyncCommand(SignInAsync, () => Authentication.State == CodexAuthenticationState.AuthenticationRequired && !disposed);
        SendMessageCommand = new AsyncCommand(SendMessageAsync, () => CanSend);
        CancelTurnCommand = new AsyncCommand(CancelTurnAsync, () => IsTurnActive);
        NewConversationCommand = new AsyncCommand(NewConversationAsync, () => CanStartConversation);
        client.EventReceived += OnClientEventReceived;
        client.AuthenticationChanged += OnAuthenticationChanged;
        client.ConversationEventReceived += OnConversationEventReceived;
        workspaceContext.WorkspaceChanged += OnWorkspaceChanged;
        UpdateWorkspace(workspaceContext.WorkspacePath);
    }

    public ObservableCollection<ChatMessage> Messages { get; }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand SignInCommand { get; }
    public AsyncCommand SendMessageCommand { get; }
    public AsyncCommand CancelTurnCommand { get; }
    public AsyncCommand NewConversationCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? AuthenticationUrlAvailable;

    public string InputText { get => inputText; set { if (inputText == value) return; inputText = value; OnPropertyChanged(); RefreshCommands(); } }
    public string WorkspacePath { get => workspacePath; private set { if (workspacePath == value) return; workspacePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(WorkspaceName)); } }
    public string WorkspaceName => string.IsNullOrWhiteSpace(WorkspacePath) ? "No solution open" : Path.GetFileName(WorkspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    public string StatusMessage { get => statusMessage; private set { if (statusMessage == value) return; statusMessage = value; OnPropertyChanged(); } }
    public string? ErrorMessage { get => errorMessage; private set { if (errorMessage == value) return; errorMessage = value; OnPropertyChanged(); } }
    public ChatConnectionState ConnectionState { get => connectionState; private set { if (connectionState == value) return; connectionState = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsTurnActive)); RefreshCommands(); } }
    public CodexAuthenticationInfo Authentication { get => authentication; private set { authentication = value; OnPropertyChanged(); OnPropertyChanged(nameof(AuthenticationStatus)); RefreshCommands(); } }
    public string AuthenticationStatus => Authentication.State.ToString();
    public CodexConversation? CurrentConversation { get => conversation; private set { conversation = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsTurnActive)); RefreshCommands(); } }
    public bool IsTurnActive => CurrentConversation != null && (CurrentConversation.State == ConversationTurnState.Starting || CurrentConversation.State == ConversationTurnState.Running || CurrentConversation.State == ConversationTurnState.Cancelling);
    private bool HasWorkspace => !string.IsNullOrWhiteSpace(WorkspacePath) && Directory.Exists(WorkspacePath);
    private bool CanStartConversation => ConnectionState == ChatConnectionState.Ready && Authentication.State == CodexAuthenticationState.Authenticated && HasWorkspace && !IsTurnActive;
    private bool CanSend => CanStartConversation && !string.IsNullOrWhiteSpace(InputText);

    public async Task ConnectAsync()
    {
        if (disposed) return;
        ErrorMessage = null;
        ConnectionState = ChatConnectionState.Connecting;
        StatusMessage = "Connecting to Codex App Server…";
        try
        {
            await client.StartAsync(lifetimeCancellation.Token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(ApplyConnectionState).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        catch
        {
            await dispatcher.InvokeAsync(() => { ConnectionState = ChatConnectionState.Error; ErrorMessage = "Could not connect to Codex App Server. Check that the Codex CLI is installed and signed in."; StatusMessage = "Connection failed."; }).ConfigureAwait(false);
        }
    }

    public async Task SignInAsync()
    {
        ErrorMessage = null;
        try
        {
            CodexLoginRequest login = await client.SignInAsync(lifetimeCancellation.Token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => { StatusMessage = "Complete ChatGPT sign-in in your browser."; AuthenticationUrlAvailable?.Invoke(this, login.AuthenticationUrl); }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        catch { await dispatcher.InvokeAsync(() => { ErrorMessage = "Could not start ChatGPT sign-in."; StatusMessage = "Sign-in failed."; }).ConfigureAwait(false); }
    }

    public async Task NewConversationAsync()
    {
        if (!CanStartConversation) return;
        ErrorMessage = null;
        try
        {
            CodexConversation newConversation = await client.StartConversationAsync(WorkspacePath, lifetimeCancellation.Token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => { CurrentConversation = newConversation; streamingAssistantMessage = null; Messages.Clear(); StatusMessage = "New read-only conversation started."; }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        catch { await dispatcher.InvokeAsync(() => { ErrorMessage = "Could not start a Codex conversation for this solution."; StatusMessage = "Conversation failed to start."; }).ConfigureAwait(false); }
    }

    public async Task SendMessageAsync()
    {
        if (!CanSend) return;
        if (CurrentConversation == null) await NewConversationAsync().ConfigureAwait(false);
        if (CurrentConversation == null) return;
        string message = InputText;
        InputText = string.Empty;
        Messages.Add(new ChatMessage("user", message));
        streamingAssistantMessage = new ChatMessage("assistant", string.Empty);
        Messages.Add(streamingAssistantMessage);
        ErrorMessage = null;
        try
        {
            await client.SendAsync(message, lifetimeCancellation.Token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => { ConnectionState = ChatConnectionState.Running; StatusMessage = "Codex is responding…"; }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        catch { await dispatcher.InvokeAsync(() => { ConnectionState = ChatConnectionState.Error; ErrorMessage = "Could not send the message to Codex."; StatusMessage = "Message failed."; }).ConfigureAwait(false); }
    }

    public async Task CancelTurnAsync()
    {
        if (!IsTurnActive) return;
        try { await client.CancelAsync(lifetimeCancellation.Token).ConfigureAwait(false); await dispatcher.InvokeAsync(() => StatusMessage = "Cancelling Codex turn…").ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        catch { await dispatcher.InvokeAsync(() => { ErrorMessage = "Could not cancel the active Codex turn."; }).ConfigureAwait(false); }
    }

    private void OnAuthenticationChanged(object? sender, CodexAuthenticationInfo value) => Dispatch(() =>
    {
        Authentication = value;
        if (value.State == CodexAuthenticationState.Authenticated) { ConnectionState = ChatConnectionState.Ready; StatusMessage = "Connected and authenticated."; }
        else if (value.State == CodexAuthenticationState.AuthenticationRequired) { ConnectionState = ChatConnectionState.AuthenticationRequired; StatusMessage = "Sign in with your ChatGPT subscription to continue."; }
        else if (value.State == CodexAuthenticationState.Failed) { ConnectionState = ChatConnectionState.Error; ErrorMessage = value.ErrorMessage ?? "Codex authentication failed."; }
    });

    private void OnClientEventReceived(object? sender, CodexEventArgs value) => Dispatch(() =>
    {
        if (value.Kind == "error") { ConnectionState = ChatConnectionState.Error; ErrorMessage = value.Text; StatusMessage = "Disconnected from Codex."; }
        else if (value.Kind == "diagnostic") StatusMessage = value.Text;
    });

    private void OnConversationEventReceived(object? sender, CodexConversationEventArgs value) => Dispatch(() =>
    {
        if (CurrentConversation == null || CurrentConversation.ThreadId != value.ThreadId) return;
        switch (value.Kind)
        {
            case CodexConversationEventKind.AssistantDelta:
                if (streamingAssistantMessage == null) { streamingAssistantMessage = new ChatMessage("assistant", string.Empty); Messages.Add(streamingAssistantMessage); }
                streamingAssistantMessage.Content += value.Text ?? string.Empty;
                break;
            case CodexConversationEventKind.ToolActivity:
                Messages.Add(new ChatMessage("tool", value.Text ?? "Codex tool activity"));
                break;
            case CodexConversationEventKind.TurnCompleted:
                ConnectionState = ChatConnectionState.Ready; StatusMessage = "Codex turn completed."; streamingAssistantMessage = null; break;
            case CodexConversationEventKind.TurnInterrupted:
                ConnectionState = ChatConnectionState.Ready; StatusMessage = "Codex turn interrupted."; streamingAssistantMessage = null; break;
            case CodexConversationEventKind.TurnFailed:
            case CodexConversationEventKind.Disconnected:
                ConnectionState = ChatConnectionState.Error; ErrorMessage = value.Text ?? "Codex turn failed."; StatusMessage = "Codex turn failed."; streamingAssistantMessage = null; break;
        }
        OnPropertyChanged(nameof(CurrentConversation));
        OnPropertyChanged(nameof(IsTurnActive));
        RefreshCommands();
    });

    private void OnWorkspaceChanged(object? sender, EventArgs eventArgs) => Dispatch(() => UpdateWorkspace(workspaceContext.WorkspacePath));

    private void UpdateWorkspace(string? newWorkspacePath)
    {
        string normalized = string.IsNullOrWhiteSpace(newWorkspacePath) ? string.Empty : Path.GetFullPath(newWorkspacePath);
        if (string.Equals(WorkspacePath, normalized, StringComparison.OrdinalIgnoreCase)) return;
        WorkspacePath = normalized;
        CurrentConversation = null;
        streamingAssistantMessage = null;
        Messages.Clear();
        ErrorMessage = null;
        StatusMessage = HasWorkspace ? "Workspace changed. Start a new conversation." : "Open a solution to begin.";
        if (ConnectionState == ChatConnectionState.Running) ConnectionState = ChatConnectionState.Ready;
        RefreshCommands();
    }

    private void ApplyConnectionState()
    {
        Authentication = client.Authentication;
        if (Authentication.State == CodexAuthenticationState.Authenticated) { ConnectionState = ChatConnectionState.Ready; StatusMessage = "Connected and authenticated."; }
        else { ConnectionState = ChatConnectionState.AuthenticationRequired; StatusMessage = "Sign in with your ChatGPT subscription to continue."; }
    }
    private void Dispatch(Action action) { if (disposed) return; _ = dispatcher.InvokeAsync(() => { if (!disposed) action(); }); }
    private void RefreshCommands() { ConnectCommand.RaiseCanExecuteChanged(); SignInCommand.RaiseCanExecuteChanged(); SendMessageCommand.RaiseCanExecuteChanged(); CancelTurnCommand.RaiseCanExecuteChanged(); NewConversationCommand.RaiseCanExecuteChanged(); }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetimeCancellation.Cancel();
        client.EventReceived -= OnClientEventReceived;
        client.AuthenticationChanged -= OnAuthenticationChanged;
        client.ConversationEventReceived -= OnConversationEventReceived;
        workspaceContext.WorkspaceChanged -= OnWorkspaceChanged;
        lifetimeCancellation.Dispose();
    }
}
