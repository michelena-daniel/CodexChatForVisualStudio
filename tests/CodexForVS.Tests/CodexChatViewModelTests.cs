using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodexForVS.Client;
using CodexForVS.Core;
using Xunit;

namespace CodexForVS.Tests;

public sealed class CodexChatViewModelTests
{
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
        public Task SendAsync(string text, CancellationToken cancellationToken)
        {
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
        public void Dispose() { }
    }
}
