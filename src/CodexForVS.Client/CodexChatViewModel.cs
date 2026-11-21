using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
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
    private string activity = string.Empty;
    private ChatConnectionState connectionState = ChatConnectionState.Disconnected;
    private CodexAuthenticationInfo authentication;
    private CodexAuthenticationInfo? usageAccount;
    private CodexConversation? conversation;
    private ChatMessage? streamingAssistantMessage;
    private bool disposed;
    private bool startingConversation;
    private bool submittingTurn;
    private SessionMode mode = SessionMode.Chat;
    private readonly Dictionary<string, ChatMessage> planItems = new Dictionary<string, ChatMessage>();
    private string approvalPolicy;
    private const string DefaultModel = "CLI default";
    private string selectedModel = DefaultModel;
    private string? planForImplementation;
    private int workspaceGeneration;
    private string? solutionFile;
    public ObservableCollection<string> Models { get; } = new ObservableCollection<string> { DefaultModel };
    public string? SelectedModel
    {
        get => selectedModel;
        set
        {
            if (value == selectedModel || !CanChangeMode || value == null || !Models.Contains(value)) return;
            selectedModel = value;
            CurrentConversation = null;
            StatusMessage = "The next conversation will use " + value + ".";
            OnPropertyChanged();
        }
    }
    public FileReviewViewModel Review { get; }
    public AsyncCommand SetupSandboxCommand { get; }
    public bool NeedsSandboxSetup { get; private set; }
    private async Task SetupSandboxAsync()
    {
        try
        {
            if (Worktree.Session == null || !NeedsSandboxSetup) return;
            StatusMessage = "Starting the official Codex Windows sandbox setup.";
            await client.SetupWindowsSandboxAsync(Worktree.Session,lifetimeCancellation.Token);
        }
        catch (Exception) { ErrorMessage = "Codex could not start Windows sandbox setup."; }
    }

    public CodexChatViewModel(ICodexAppServerClient client, IWorkspaceContext workspaceContext, IUiDispatcher dispatcher, string initialPolicy = ReadOnlyApprovalPolicy.Never, IWorktreeService? worktreeService = null, IReviewEditor? editor = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.workspaceContext = workspaceContext ?? throw new ArgumentNullException(nameof(workspaceContext));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        authentication = client.Authentication;
        approvalPolicy = ReadOnlyApprovalPolicy.Validate(initialPolicy);
        Messages = new ObservableCollection<ChatMessage>();
        Usage = new CodexUsageViewModel(client, dispatcher);
        Worktree = new WorktreePreparationViewModel(worktreeService ?? new GitWorktreeService(), dispatcher);
        Review = new FileReviewViewModel(editor,dispatcher);
        Worktree.PropertyChanged += OnWorktreeChanged;
        Review.PropertyChanged += OnReviewChanged;
        SetupSandboxCommand = new AsyncCommand(SetupSandboxAsync,() => !disposed && Worktree.Session != null && NeedsSandboxSetup && !IsTurnActive);
        ConnectCommand = new AsyncCommand(ConnectAsync, () => ConnectionState != ChatConnectionState.Connecting && !disposed);
        SignInCommand = new AsyncCommand(SignInAsync, () => Authentication.State == CodexAuthenticationState.AuthenticationRequired && !disposed);
        SendMessageCommand = new AsyncCommand(SendMessageAsync, () => CanSend);
        CancelTurnCommand = new AsyncCommand(CancelTurnAsync, () => IsTurnActive);
        NewConversationCommand = new AsyncCommand(NewConversationAsync, () => !submittingTurn && CanStartConversation);
        client.EventReceived += OnClientEventReceived;
        client.AuthenticationChanged += OnAuthenticationChanged;
        client.UsageUpdated += OnUsageUpdated;
        client.ConversationEventReceived += OnConversationEventReceived;
        workspaceContext.WorkspaceChanged += OnWorkspaceChanged;
        UpdateWorkspace(workspaceContext.WorkspacePath);
    }

    public ObservableCollection<ChatMessage> Messages { get; }
    public CodexUsageViewModel Usage { get; }
    public WorktreePreparationViewModel Worktree { get; }
    public IReadOnlyList<SessionMode> Modes => Worktree.Session == null ? new[] { SessionMode.Chat, SessionMode.Plan } : new[] { SessionMode.Chat, SessionMode.Plan, SessionMode.Implement };
    public bool CanChangeMode => CanChangePolicy;
    public SessionMode Mode
    {
        get => mode;
        set
        {
            if (value == mode || !CanChangeMode || !Modes.Contains(value)) return;
            if (mode == SessionMode.Implement || value == SessionMode.Implement)
            {
                planForImplementation = value == SessionMode.Implement ? Messages.LastOrDefault(m => m.Role == "Plan")?.Content : null;
                CurrentConversation = null;
                StatusMessage = value == SessionMode.Implement ? "Implement will run in the prepared worktree." : "Next message starts a read-only solution thread.";
            }
            mode = value;
            OnPropertyChanged();
        }
    }
    public string[] ApprovalPolicies { get; } = { ReadOnlyApprovalPolicy.Never, ReadOnlyApprovalPolicy.Untrusted };
    public bool CanChangePolicy => !submittingTurn && !startingConversation && !IsTurnActive && ConnectionState != ChatConnectionState.Running;
    public string ApprovalPolicy
    {
        get => approvalPolicy;
        set
        {
            if (value == approvalPolicy || !CanChangePolicy) return;
            approvalPolicy = ReadOnlyApprovalPolicy.Validate(value);
            CurrentConversation = null;
            streamingAssistantMessage = null;
            Activity = string.Empty;
            StatusMessage = "Policy changed. Your next message starts a new conversation.";
            OnPropertyChanged();
        }
    }
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
    public string Activity { get => activity; private set { if (activity == value) return; activity = value; OnPropertyChanged(); } }
    public string? ErrorMessage { get => errorMessage; private set { if (errorMessage == value) return; errorMessage = value; OnPropertyChanged(); } }
    public ChatConnectionState ConnectionState { get => connectionState; private set { if (connectionState == value) return; connectionState = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsTurnActive)); RefreshCommands(); } }
    public CodexAuthenticationInfo Authentication { get => authentication; private set { authentication = value; OnPropertyChanged(); OnPropertyChanged(nameof(AuthenticationStatus)); RefreshCommands(); } }
    public string AuthenticationStatus => Authentication.State.ToString();
    public CodexConversation? CurrentConversation { get => conversation; private set { conversation = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsTurnActive)); RefreshCommands(); } }
    public bool IsTurnActive => CurrentConversation != null && (CurrentConversation.State == ConversationTurnState.Starting || CurrentConversation.State == ConversationTurnState.Running || CurrentConversation.State == ConversationTurnState.Cancelling);
    private bool HasWorkspace => !string.IsNullOrWhiteSpace(WorkspacePath) && Directory.Exists(WorkspacePath);
    private bool CanStartConversation => !Review.Busy && !startingConversation && !IsTurnActive && ConnectionState == ChatConnectionState.Ready && Authentication.State == CodexAuthenticationState.Authenticated && HasWorkspace && (Mode != SessionMode.Implement || Worktree.Session != null);
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
        catch (Exception error)
        {
            await dispatcher.InvokeAsync(() => { ConnectionState = ChatConnectionState.Error; ErrorMessage = error is CodexLaunchException ? error.Message : "Could not initialize Codex App Server or read its account state (" + error.GetType().Name + ")."; StatusMessage = "Connection failed."; }).ConfigureAwait(false);
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

    public Task NewConversationAsync() => StartConversationAsync(true);

    private async Task StartConversationAsync(bool clearHistory)
    {
        if (!CanStartConversation || (clearHistory && submittingTurn)) return;
        startingConversation = true;
        RefreshCommands();
        ErrorMessage = null;
        try
        {
            int generation = workspaceGeneration;
            WorktreeSession? isolated = Mode == SessionMode.Implement ? Worktree.Session : null;
            CodexConversation newConversation = await client.StartConversationAsync(isolated?.WorktreePath ?? WorkspacePath, lifetimeCancellation.Token, ApprovalPolicy, isolated, SelectedModel == DefaultModel ? null : SelectedModel).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                if (generation != workspaceGeneration || disposed) return;
                CurrentConversation = newConversation; streamingAssistantMessage = null; Activity = string.Empty;
                if (clearHistory) { Messages.Clear(); planForImplementation = null; }
                StatusMessage = isolated == null ? "New read-only conversation started." : "Implementation thread started in " + isolated.WorktreePath;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception error) { await dispatcher.InvokeAsync(() =>
        {
            if (error is SandboxSetupRequiredException) { NeedsSandboxSetup = true; OnPropertyChanged(nameof(NeedsSandboxSetup)); SetupSandboxCommand.RaiseCanExecuteChanged(); }
            ErrorMessage = error is InvalidOperationException || error is IOException ? error.Message : "Could not start a Codex conversation for this solution.";
            StatusMessage = "Conversation failed to start.";
        }).ConfigureAwait(false); }
        finally { await dispatcher.InvokeAsync(() => { startingConversation = false; RefreshCommands(); }).ConfigureAwait(false); }
    }

    public async Task SendMessageAsync()
    {
        if (!CanSend || submittingTurn) return;
        int generation = workspaceGeneration;
        string message = InputText;
        SessionMode turnMode = Mode;
        submittingTurn = true;
        Review.SetRunning(true);
        OnPropertyChanged(nameof(CanChangeMode));
        try
        {
            if (CurrentConversation == null) await StartConversationAsync(false);
            if (disposed || generation != workspaceGeneration || CurrentConversation == null) return;
            await dispatcher.InvokeAsync(() =>
            {
                if (InputText == message) InputText = string.Empty;
                Messages.Add(new ChatMessage("user", message));
                planItems.Clear();
                Activity = string.Empty;
                streamingAssistantMessage = null;
                ErrorMessage = null;
            });
            string request = turnMode == SessionMode.Implement && planForImplementation != null
                ? "Previously discussed plan:\n" + planForImplementation + "\n\nCurrent request:\n" + message : message;
            planForImplementation = null;
            await client.SendAsync(request, lifetimeCancellation.Token, turnMode).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                if (CurrentConversation?.State != ConversationTurnState.Running) return;
                ConnectionState = ChatConnectionState.Running;
                StatusMessage = "Codex is responding…";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        catch (InvalidOperationException) when (client.Authentication.State == CodexAuthenticationState.AuthenticationRequired)
        {
            await dispatcher.InvokeAsync(ApplyConnectionState).ConfigureAwait(false);
        }
        catch { await dispatcher.InvokeAsync(() => { ConnectionState = ChatConnectionState.Error; ErrorMessage = "Could not send the message to Codex."; StatusMessage = "Message failed."; }).ConfigureAwait(false); }
        finally { await dispatcher.InvokeAsync(() => { submittingTurn = false; Review.SetRunning(IsTurnActive); RefreshCommands(); }).ConfigureAwait(false); }
    }

    public async Task CancelTurnAsync()
    {
        if (!IsTurnActive) return;
        try
        {
            await client.CancelAsync(lifetimeCancellation.Token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                if (CurrentConversation?.State == ConversationTurnState.Cancelling) StatusMessage = "Cancelling Codex turn…";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        catch { await dispatcher.InvokeAsync(() => { ErrorMessage = "Could not cancel the active Codex turn."; }).ConfigureAwait(false); }
    }

    private void OnAuthenticationChanged(object? sender, CodexAuthenticationInfo value) => Dispatch(() =>
    {
        UpdateUsageAccount(value);
        Authentication = value;
        if (value.State == CodexAuthenticationState.Authenticated) { ConnectionState = ChatConnectionState.Ready; StatusMessage = "Connected and authenticated."; _ = LoadModelsAsync(); }
        else if (value.State == CodexAuthenticationState.AuthenticationRequired) { ConnectionState = ChatConnectionState.AuthenticationRequired; StatusMessage = "Sign in with your ChatGPT subscription to continue."; }
        else if (value.State == CodexAuthenticationState.Failed) { ConnectionState = ChatConnectionState.Error; ErrorMessage = value.ErrorMessage ?? "Codex authentication failed."; }
    });

    private void OnUsageUpdated(object? sender, CodexUsageSnapshot snapshot) => Dispatch(() => Usage.ApplyUpdate(snapshot));

    private void UpdateUsageAccount(CodexAuthenticationInfo account)
    {
        if (ReferenceEquals(usageAccount, account)) return;
        usageAccount = account;
        Usage.SetSession(account.State == CodexAuthenticationState.Authenticated);
    }

    private void OnClientEventReceived(object? sender, CodexEventArgs value) => Dispatch(() =>
    {
        if (value.Kind == "error") { usageAccount = null; Usage.SetSession(false); ConnectionState = ChatConnectionState.Error; ErrorMessage = value.Text; StatusMessage = "Disconnected from Codex."; }
        else if (value.Kind == "sandboxSetup")
        {
            StatusMessage = value.Text; ErrorMessage = null;
            NeedsSandboxSetup = !value.Text.StartsWith("Windows sandbox ready",StringComparison.Ordinal);
            OnPropertyChanged(nameof(NeedsSandboxSetup)); SetupSandboxCommand.RaiseCanExecuteChanged();
        }
        else if (value.Kind == "diagnostic") { StatusMessage = value.Text; if (CurrentConversation?.Worktree != null) Messages.Add(new ChatMessage("Activity",value.Text)); }
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
            case CodexConversationEventKind.PlanDelta:
            case CodexConversationEventKind.PlanCompleted:
                if (value.ItemId == null) break;
                streamingAssistantMessage = null;
                if (!planItems.TryGetValue(value.ItemId, out ChatMessage plan))
                {
                    plan = new ChatMessage("Plan", string.Empty);
                    planItems.Add(value.ItemId, plan);
                    Messages.Add(plan);
                }
                plan.Content = value.Kind == CodexConversationEventKind.PlanCompleted ? value.Text ?? string.Empty : plan.Content + value.Text;
                break;
            case CodexConversationEventKind.ToolActivity:
                if (CurrentConversation.Worktree != null && value.Text != null) Messages.Add(new ChatMessage("Activity",value.Text));
                streamingAssistantMessage = null;
                Activity = value.Text ?? string.Empty;
                break;
            case CodexConversationEventKind.TurnCompleted:
                ConnectionState = ChatConnectionState.Ready; StatusMessage = "Codex turn completed."; streamingAssistantMessage = null; break;
            case CodexConversationEventKind.TurnInterrupted:
                ConnectionState = ChatConnectionState.Ready; StatusMessage = "Codex turn interrupted."; streamingAssistantMessage = null; break;
            case CodexConversationEventKind.TurnFailed:
            case CodexConversationEventKind.Disconnected:
                ConnectionState = ChatConnectionState.Error; ErrorMessage = value.Text ?? "Codex turn failed."; StatusMessage = "Codex turn failed."; streamingAssistantMessage = null; break;
        }
        if (value.Kind == CodexConversationEventKind.TurnCompleted || value.Kind == CodexConversationEventKind.TurnInterrupted || value.Kind == CodexConversationEventKind.TurnFailed || value.Kind == CodexConversationEventKind.Disconnected)
        {
            Review.SetRunning(false);
            if (CurrentConversation.Worktree != null) _ = Review.RefreshAsync();
        }
        OnPropertyChanged(nameof(CurrentConversation));
        OnPropertyChanged(nameof(IsTurnActive));
        RefreshCommands();
    });

    private void OnWorkspaceChanged(object? sender, EventArgs eventArgs) => Dispatch(() => UpdateWorkspace(workspaceContext.WorkspacePath));

    private void UpdateWorkspace(string? newWorkspacePath)
    {
        string normalized = string.IsNullOrWhiteSpace(newWorkspacePath) ? string.Empty : Path.GetFullPath(newWorkspacePath);
        string? file = workspaceContext.SolutionFilePath;
        if (string.Equals(WorkspacePath,normalized,StringComparison.OrdinalIgnoreCase) && string.Equals(solutionFile,file,StringComparison.OrdinalIgnoreCase)) return;
        if (IsTurnActive || submittingTurn) _ = InterruptObsoleteTurnAsync();
        solutionFile = file;
        Worktree.SetSolution(file);
        Review.SetRunning(false);
        workspaceGeneration++;
        planForImplementation = null;
        WorkspacePath = normalized;
        Activity = string.Empty;
        CurrentConversation = null;
        streamingAssistantMessage = null;
        Messages.Clear();
        ErrorMessage = null;
        StatusMessage = HasWorkspace ? "Workspace changed. Start a new conversation." : "Open a solution to begin.";
        if (ConnectionState == ChatConnectionState.Running) ConnectionState = ChatConnectionState.Ready;
        RefreshCommands();
    }

    private async Task InterruptObsoleteTurnAsync()
    {
        try { await client.CancelAsync(lifetimeCancellation.Token).ConfigureAwait(false); }
        catch { Dispatch(() => ErrorMessage = "The previous turn could not be interrupted. Its workspace remains isolated."); }
    }

    private void OnWorktreeChanged(object? sender, PropertyChangedEventArgs args)
    {
        Review.SetSession(Worktree.Session);
        if (Worktree.Session == null && mode == SessionMode.Implement)
        {
            CurrentConversation = null; mode = SessionMode.Chat; planForImplementation = null; OnPropertyChanged(nameof(Mode));
        }
        OnPropertyChanged(nameof(Modes));
        RefreshCommands();
    }
    private void OnReviewChanged(object? sender, PropertyChangedEventArgs args) => RefreshCommands();
    private async Task LoadModelsAsync()
    {
        try
        {
            string[] models = await client.ListModelsAsync(lifetimeCancellation.Token).ConfigureAwait(false);
            Dispatch(() => { foreach (string name in models) if (!Models.Contains(name)) Models.Add(name); });
        }
        catch { Dispatch(() => StatusMessage = "Model list unavailable. The CLI default is available."); }
    }
    private void ApplyConnectionState()
    {
        Authentication = client.Authentication;
        UpdateUsageAccount(Authentication);
        if (Authentication.State == CodexAuthenticationState.Authenticated) { ConnectionState = ChatConnectionState.Ready; StatusMessage = "Connected and authenticated."; }
        else { ConnectionState = ChatConnectionState.AuthenticationRequired; StatusMessage = "Sign in with your ChatGPT subscription to continue."; }
    }
    private void Dispatch(Action action) { if (disposed) return; _ = dispatcher.InvokeAsync(() => { if (!disposed) action(); }); }
    private void RefreshCommands() { OnPropertyChanged(nameof(CanChangeMode)); OnPropertyChanged(nameof(CanChangePolicy)); ConnectCommand.RaiseCanExecuteChanged(); SignInCommand.RaiseCanExecuteChanged(); SendMessageCommand.RaiseCanExecuteChanged(); CancelTurnCommand.RaiseCanExecuteChanged(); NewConversationCommand.RaiseCanExecuteChanged(); }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetimeCancellation.Cancel();
        Worktree.PropertyChanged -= OnWorktreeChanged;
        Review.PropertyChanged -= OnReviewChanged;
        Worktree.Dispose(); Review.Dispose();
        client.UsageUpdated -= OnUsageUpdated;
        Usage.SetSession(false);
        client.EventReceived -= OnClientEventReceived;
        client.AuthenticationChanged -= OnAuthenticationChanged;
        client.ConversationEventReceived -= OnConversationEventReceived;
        workspaceContext.WorkspaceChanged -= OnWorkspaceChanged;
        lifetimeCancellation.Dispose();
    }
}
