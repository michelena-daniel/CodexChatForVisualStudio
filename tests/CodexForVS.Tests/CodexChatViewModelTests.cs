using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodexForVS.Client;
using CodexForVS.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexForVS.Tests;

public sealed class CodexChatViewModelTests
{
    [Fact]
    public async Task SwitchingToPlanKeepsDiscussionAndRendersEachPlanItemOnce()
    {
        FakeClient client = new FakeClient(CodexAuthenticationState.Authenticated);
        using CodexChatViewModel model = Create(new FakeWorkspaceContext(Workspace), client);
        await model.ConnectAsync();
        model.InputText = "Let's discuss greetings.";
        await model.SendMessageAsync();
        model.Mode = SessionMode.Plan;
        Assert.Equal(SessionMode.Chat, model.Mode);
        client.CompleteCurrentTurn("completed");

        CodexConversation discussion = model.CurrentConversation!;
        model.Mode = SessionMode.Plan;
        model.InputText = "How would you implement it?";
        await model.SendMessageAsync();
        client.EmitPlan(CodexConversationEventKind.PlanDelta, "p", "draft");
        client.EmitPlan(CodexConversationEventKind.PlanDelta, "p", " text");
        ChatMessage plan = model.Messages[2];
        Assert.Equal("draft text", plan.Content);
        client.EmitPlan(CodexConversationEventKind.PlanCompleted, "p", "Authoritative plan");
        client.EmitPlan(CodexConversationEventKind.PlanCompleted, "p", "Authoritative plan");
        Assert.Equal("Authoritative plan", plan.Content);
        Assert.Equal(3, model.Messages.Count);
        client.CompleteCurrentTurn("completed");
        model.InputText = "Simplify step two.";
        await model.SendMessageAsync();
        client.EmitPlan(CodexConversationEventKind.PlanCompleted, "revision", "Simplified plan");
        Assert.Equal("Simplified plan", model.Messages[4].Content);
        Assert.Equal(new[] { SessionMode.Chat, SessionMode.Plan, SessionMode.Plan }, client.SentModes);
        Assert.Equal("Let's discuss greetings.", model.Messages[0].Content);
        Assert.Same(discussion, model.CurrentConversation);
        Assert.Equal(1, client.StartConversationCalls);
    }

    private static CodexUsageSnapshot UsageReading(int primaryUsed, int weeklyUsed) =>
        CodexUsageSnapshot.Parse(new JObject { ["rateLimits"] = new JObject {
            ["primary"] = new JObject { ["usedPercent"] = primaryUsed, ["windowDurationMins"] = 300 },
            ["secondary"] = new JObject { ["usedPercent"] = weeklyUsed, ["windowDurationMins"] = 10080 }
        } })!;

    [Fact]
    public async Task UsageShowsTightestWindowAndRetainsLastReadingWhenRefreshFails()
    {
        FakeClient client = new FakeClient(CodexAuthenticationState.Authenticated) { Reading = Task.FromResult(UsageReading(72, 26)) };
        using CodexChatViewModel model = Create(new FakeWorkspaceContext(Workspace), client);
        Assert.Equal("Usage unavailable", model.Usage.Summary);
        await model.ConnectAsync();
        Assert.Equal(1, client.UsageReads);
        Assert.Equal("Usage · 5h · 28% left", model.Usage.Summary);
        Assert.Equal(2, model.Usage.Windows.Count);
        client.Push(UsageReading(30, 99));
        Assert.Equal("Usage · Weekly · 1% left", model.Usage.Summary);
        string updated = model.Usage.Status;
        client.Reading = Task.FromException<CodexUsageSnapshot>(new IOException());
        await model.Usage.RefreshCommand.ExecuteAsync();
        Assert.Contains(updated, model.Usage.Status);
        Assert.Contains("Refresh failed", model.Usage.Status);
        Assert.Equal("Usage · Weekly · 1% left", model.Usage.Summary);
        model.InputText = "hello";
        Assert.True(model.SendMessageCommand.CanExecute(null!));
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task NotificationsWinOverOlderReadsAndSignOutClearsUsage()
    {
        FakeClient client = new FakeClient(CodexAuthenticationState.AuthenticationRequired);
        using CodexChatViewModel model = Create(new FakeWorkspaceContext(Workspace), client);
        await model.ConnectAsync();
        Assert.Equal(0, client.UsageReads);
        Assert.False(model.Usage.RefreshCommand.CanExecute(null!));
        client.Authenticate(CodexAuthenticationState.Authenticated);
        Assert.Equal(1, client.UsageReads);
        TaskCompletionSource<CodexUsageSnapshot> pending = new TaskCompletionSource<CodexUsageSnapshot>();
        client.Reading = pending.Task;
        Task refresh = model.Usage.RefreshCommand.ExecuteAsync();
        client.Push(UsageReading(10, 90));
        pending.SetResult(UsageReading(10, 2));
        await refresh;
        Assert.Equal("Usage · Weekly · 10% left", model.Usage.Summary);

        pending = new TaskCompletionSource<CodexUsageSnapshot>();
        client.Reading = pending.Task;
        refresh = model.Usage.RefreshCommand.ExecuteAsync();
        client.Authenticate(CodexAuthenticationState.AuthenticationRequired);
        pending.SetResult(UsageReading(10, 2));
        await refresh;
        Assert.Equal("Usage unavailable", model.Usage.Summary);
        Assert.Empty(model.Usage.Windows);
    }

    [Fact]
    public async Task IncludedLimitWarningRequiresServerConfirmationAndLeavesSendAvailable()
    {
        FakeClient client = new FakeClient(CodexAuthenticationState.Authenticated);
        using CodexChatViewModel model = Create(new FakeWorkspaceContext(Workspace), client);
        await model.ConnectAsync();
        client.Push(UsageReading(100, 50));
        Assert.Contains("0% left", model.Usage.Summary);
        client.Push(CodexUsageSnapshot.Parse(JObject.Parse("{rateLimits:{rateLimitReachedType:'rate_limit_reached'}}"))!);
        Assert.Equal("Included usage limit reached", model.Usage.Summary);
        client.Reading = Task.FromResult(CodexUsageSnapshot.Parse(JObject.Parse("{ordinaryUsageAllowed:false,rateLimits:{primary:{usedPercent:5}}}"))!);
        await model.Usage.RefreshCommand.ExecuteAsync();
        client.Push(UsageReading(5, 5));
        Assert.Equal("Included usage limit reached", model.Usage.Summary);
        model.InputText = "hello";
        Assert.True(model.SendMessageCommand.CanExecute(null!));
        client.Reading = Task.FromResult(CodexUsageSnapshot.Parse(JObject.Parse("{ordinaryUsageAllowed:true,rateLimits:{primary:{usedPercent:2}}}"))!);
        await model.Usage.RefreshCommand.ExecuteAsync();
        Assert.Contains("98% left", model.Usage.Summary);
    }

    [Fact]
    public void InitialStateIsDisconnectedAndRequiresWorkspace()
    {
        using (CodexChatViewModel viewModel = Create(new FakeWorkspaceContext(null), new FakeClient(CodexAuthenticationState.Unknown)))
        {
            Assert.Equal(ChatConnectionState.Disconnected, viewModel.ConnectionState);
            Assert.Equal("No solution open", viewModel.WorkspaceName);
            Assert.Empty(viewModel.Messages);
        }
    }

    [Fact]
    public async Task PolicyChangesBetweenTurnsStartAFreshThread()
    {
        var client = new FakeClient(CodexAuthenticationState.Authenticated);
        using var model = Create(new FakeWorkspaceContext(Workspace), client);
        await model.ConnectAsync();
        model.InputText = "one";
        await model.SendMessageAsync();
        model.ApprovalPolicy = "untrusted";
        Assert.Equal("never", model.ApprovalPolicy);
        string firstThread = model.CurrentConversation!.ThreadId;
        client.CompleteCurrentTurn("completed");
        model.ApprovalPolicy = "untrusted";
        Assert.Null(model.CurrentConversation);
        model.InputText = "two";
        await model.SendMessageAsync();
        Assert.Equal("untrusted", model.CurrentConversation!.ApprovalPolicy);
        Assert.NotEqual(firstThread, model.CurrentConversation.ThreadId);
    }

    [Fact]
    public async Task ConnectReflectsAuthenticatedAndAuthenticationRequiredStates()
    {
        FakeWorkspaceContext workspace = new FakeWorkspaceContext(Workspace);
        using (CodexChatViewModel authenticated = Create(workspace, new FakeClient(CodexAuthenticationState.Authenticated)))
        {
            await authenticated.ConnectAsync();
            Assert.Equal(ChatConnectionState.Ready, authenticated.ConnectionState);
        }
        using (CodexChatViewModel unauthenticated = Create(workspace, new FakeClient(CodexAuthenticationState.AuthenticationRequired)))
        {
            await unauthenticated.ConnectAsync();
            Assert.Equal(ChatConnectionState.AuthenticationRequired, unauthenticated.ConnectionState);
            Assert.True(unauthenticated.SignInCommand.CanExecute(null!));
        }
    }

    [Fact]
    public async Task SignInExposesOfficialBrowserUrlWithoutCredentials()
    {
        FakeClient client = new FakeClient(CodexAuthenticationState.AuthenticationRequired);
        using (CodexChatViewModel viewModel = Create(new FakeWorkspaceContext(Workspace), client))
        {
            await viewModel.ConnectAsync();
            string? receivedUrl = null;
            viewModel.AuthenticationUrlAvailable += (_, url) => receivedUrl = url;
            await viewModel.SignInAsync();
            Assert.Equal("https://example.test/login", receivedUrl);
        }
    }

    [Fact]
    public async Task SendCreatesWorkspaceConversationAndStreamsAssistantText()
    {
        FakeClient client = new FakeClient(CodexAuthenticationState.Authenticated);
        using (CodexChatViewModel viewModel = Create(new FakeWorkspaceContext(Workspace), client))
        {
            await viewModel.ConnectAsync();
            viewModel.InputText = "Inspect this solution";
            await viewModel.SendMessageAsync();
            Assert.Equal(1, client.StartConversationCalls);
            Assert.Equal("Inspect this solution", client.SentMessages[0]);
            Assert.Equal("user", viewModel.Messages[0].Role);
            client.EmitConversation(CodexConversationEventKind.ToolActivity, client.CurrentConversation!.ThreadId, "turn-1", "Workspace command blocked by policy.");
            Assert.Equal("Workspace command blocked by policy.", viewModel.Activity);
            Assert.Single(viewModel.Messages);

            client.EmitConversation(CodexConversationEventKind.AssistantDelta, client.CurrentConversation!.ThreadId, "turn-1", "Hello");
            client.EmitConversation(CodexConversationEventKind.AssistantDelta, client.CurrentConversation.ThreadId, "turn-1", " world");
            Assert.Equal("Hello world", viewModel.Messages[1].Content);
        }
    }

    [Fact]
    public async Task FirstSendDispatchesDraftAndHistoryUpdatesAfterThreadStart()
    {
        var client = new FakeClient(CodexAuthenticationState.Authenticated);
        var dispatcher = new ImmediateDispatcher();
        using var viewModel = new CodexChatViewModel(client, new FakeWorkspaceContext(Workspace), dispatcher);
        await viewModel.ConnectAsync();
        viewModel.InputText = "OK";
        var dispatched = new List<bool>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.InputText)) dispatched.Add(dispatcher.InCallback);
        };
        viewModel.Messages.CollectionChanged += (_, args) =>
        {
            if (args.NewItems != null) dispatched.Add(dispatcher.InCallback);
        };
        var gate = new TaskCompletionSource<CodexConversation>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ThreadStart = gate.Task;
        Task send = viewModel.SendMessageCommand.ExecuteAsync();
        Assert.False(viewModel.CanChangeMode);
        viewModel.Mode = SessionMode.Plan;
        Assert.Equal(SessionMode.Chat, viewModel.Mode);
        Assert.Empty(client.SentMessages);
        gate.SetResult(client.CurrentConversation!);
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { true, true }, dispatched);
        Assert.Equal("OK", Assert.Single(client.SentMessages));
    }

    [Fact]
    public async Task CompletionCancellationAndNewConversationUpdateState()
    {
        FakeClient client = new FakeClient(CodexAuthenticationState.Authenticated);
        using (CodexChatViewModel viewModel = Create(new FakeWorkspaceContext(Workspace), client))
        {
            await viewModel.ConnectAsync();
            viewModel.InputText = "First";
            await viewModel.SendMessageAsync();
            await viewModel.CancelTurnAsync();
            Assert.Equal(1, client.CancelCalls);
            client.CompleteCurrentTurn("interrupted");
            Assert.Equal(ChatConnectionState.Ready, viewModel.ConnectionState);

            await viewModel.NewConversationAsync();
            Assert.Equal(2, client.StartConversationCalls);
            Assert.Empty(viewModel.Messages);
        }
    }

    [Fact]
    public async Task EventsFromObsoleteConversationAndWorkspaceAreIgnored()
    {
        FakeWorkspaceContext workspace = new FakeWorkspaceContext(Workspace);
        FakeClient client = new FakeClient(CodexAuthenticationState.Authenticated);
        using (CodexChatViewModel viewModel = Create(workspace, client))
        {
            await viewModel.ConnectAsync();
            await viewModel.NewConversationAsync();
            string oldThreadId = client.CurrentConversation!.ThreadId;
            await viewModel.NewConversationAsync();
            client.EmitConversation(CodexConversationEventKind.AssistantDelta, oldThreadId, "turn-old", "ignored");
            Assert.Empty(viewModel.Messages);

            workspace.SetWorkspace(null);
            Assert.Null(viewModel.CurrentConversation);
            Assert.Equal("No solution open", viewModel.WorkspaceName);
            Assert.False(viewModel.SendMessageCommand.CanExecute(null!));
        }
    }

    [Fact]
    public async Task ErrorsAreVisibleAndConnectionCanBeRetried()
    {
        FakeClient client = new FakeClient(CodexAuthenticationState.Authenticated);
        using (CodexChatViewModel viewModel = Create(new FakeWorkspaceContext(Workspace), client))
        {
            await viewModel.ConnectAsync();
            client.EmitError("connection lost");
            Assert.Equal(ChatConnectionState.Error, viewModel.ConnectionState);
            Assert.Equal("connection lost", viewModel.ErrorMessage);
            await viewModel.ConnectAsync();
            Assert.Equal(ChatConnectionState.Ready, viewModel.ConnectionState);
        }
    }

    private static CodexChatViewModel Create(FakeWorkspaceContext workspace, FakeClient client) => new CodexChatViewModel(client, workspace, new ImmediateDispatcher());
    private static string Workspace => Path.GetFullPath(Directory.GetCurrentDirectory());

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool InCallback { get; private set; }
        public Task InvokeAsync(Action action)
        {
            InCallback = true;
            try { action(); }
            finally { InCallback = false; }
            return Task.CompletedTask;
        }
    }
    private sealed class FakeWorkspaceContext : IWorkspaceContext
    {
        public FakeWorkspaceContext(string? workspacePath) { WorkspacePath = workspacePath; }
        public string? WorkspacePath { get; private set; }
        public event EventHandler? WorkspaceChanged;
        public void SetWorkspace(string? workspacePath) { WorkspacePath = workspacePath; WorkspaceChanged?.Invoke(this, EventArgs.Empty); }
    }

    private sealed class FakeClient : ICodexAppServerClient
    {
        public FakeClient(CodexAuthenticationState authenticationState) { Authentication = new CodexAuthenticationInfo(authenticationState); }
        public CodexAuthenticationInfo Authentication { get; private set; }
        public CodexConversation? CurrentConversation { get; private set; }
        public int StartConversationCalls { get; private set; }
        public Task<CodexConversation>? ThreadStart { get; set; }
        public int CancelCalls { get; private set; }
        public int UsageReads { get; private set; }
        public Task<CodexUsageSnapshot> Reading { get; set; } = Task.FromResult(UsageReading(0, 0));
        public event EventHandler<CodexUsageSnapshot>? UsageUpdated;
        public void Push(CodexUsageSnapshot snapshot) => UsageUpdated?.Invoke(this, snapshot);
        public Task<CodexUsageSnapshot> ReadUsageAsync(CancellationToken cancellationToken) { UsageReads++; return Reading; }
        public void Authenticate(CodexAuthenticationState state)
        {
            Authentication = new CodexAuthenticationInfo(state);
            AuthenticationChanged?.Invoke(this, Authentication);
        }
        public List<string> SentMessages { get; } = new List<string>();
        public event EventHandler<CodexEventArgs>? EventReceived;
        public event EventHandler<CodexAuthenticationInfo>? AuthenticationChanged;
        public event EventHandler<CodexConversationEventArgs>? ConversationEventReceived;
        public Task StartAsync(CancellationToken cancellationToken) { AuthenticationChanged?.Invoke(this, Authentication); return Task.CompletedTask; }
        public Task<CodexLoginRequest> SignInAsync(CancellationToken cancellationToken) => Task.FromResult(new CodexLoginRequest("login", "https://example.test/login"));
        public Task<CodexConversation> StartConversationAsync(string workspaceDirectory, CancellationToken cancellationToken, string approvalPolicy = ReadOnlyApprovalPolicy.Never)
        {
            StartConversationCalls++;
            CurrentConversation = new CodexConversation("thread-" + StartConversationCalls, workspaceDirectory, approvalPolicy);
            return ThreadStart ?? Task.FromResult(CurrentConversation);
        }
        public List<SessionMode> SentModes { get; } = new List<SessionMode>();
        public Task SendAsync(string text, CancellationToken cancellationToken, SessionMode mode = SessionMode.Chat)
        {
            SentModes.Add(mode);
            SentMessages.Add(text);
            CurrentConversation!.ActiveTurnId = "turn-1";
            CurrentConversation.State = ConversationTurnState.Running;
            ConversationEventReceived?.Invoke(this, new CodexConversationEventArgs(CodexConversationEventKind.TurnStarted, CurrentConversation.ThreadId, "turn-1"));
            return Task.CompletedTask;
        }
        public Task CancelAsync(CancellationToken cancellationToken) { CancelCalls++; CurrentConversation!.State = ConversationTurnState.Cancelling; return Task.CompletedTask; }
        public void CompleteCurrentTurn(string status)
        {
            CodexConversation conversation = CurrentConversation!;
            conversation.ActiveTurnId = null;
            conversation.State = status == "interrupted" ? ConversationTurnState.Interrupted : ConversationTurnState.Completed;
            ConversationEventReceived?.Invoke(this, new CodexConversationEventArgs(status == "interrupted" ? CodexConversationEventKind.TurnInterrupted : CodexConversationEventKind.TurnCompleted, conversation.ThreadId, "turn-1"));
        }
        public void EmitConversation(CodexConversationEventKind kind, string threadId, string? turnId, string? text) => ConversationEventReceived?.Invoke(this, new CodexConversationEventArgs(kind, threadId, turnId, text));
        public void EmitError(string text) => EventReceived?.Invoke(this, new CodexEventArgs("error", text));
        public void EmitPlan(CodexConversationEventKind kind, string itemId, string text) => ConversationEventReceived?.Invoke(
            this, new CodexConversationEventArgs(kind, CurrentConversation!.ThreadId, CurrentConversation.ActiveTurnId, text, itemId));
        public void Dispose() { }
    }
}
