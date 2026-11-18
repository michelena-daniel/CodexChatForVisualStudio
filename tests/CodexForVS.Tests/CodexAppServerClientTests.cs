using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexForVS.Client;
using CodexForVS.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexForVS.Tests;

public sealed class CodexAppServerClientTests
{
    [Theory]
    [InlineData(null, "never")]
    [InlineData("untrusted", "untrusted")]
    public async Task HandshakeAndReadOnlyThreadUseTransport(string? selection, string expected)
    {
        FakeTransport transport = new FakeTransport();
        using (CodexAppServerClient client = new CodexAppServerClient(transport))
        {
            await client.StartAsync(CancellationToken.None);
            CodexConversation conversation = selection == null
                ? await client.StartConversationAsync(Workspace, CancellationToken.None)
                : await client.StartConversationAsync(Workspace, CancellationToken.None, selection);
            await client.SendAsync("hi", CancellationToken.None);

            Assert.Equal("initialize", transport.Requests[0].Method);
            Assert.Equal("account/read", transport.Requests[1].Method);
            Assert.Equal("thread/start", transport.Requests[2].Method);
            Assert.Equal("openai", (string?)transport.Requests[2].Parameters["modelProvider"]);
            Assert.Equal(Workspace, (string?)transport.Requests[2].Parameters["cwd"]);
            Assert.Equal("read-only", (string?)transport.Requests[2].Parameters["sandbox"]);
            Assert.Equal(expected, (string?)transport.Requests[2].Parameters["approvalPolicy"]);
            Assert.Equal("account/read", transport.Requests[3].Method);
            Request turn = Assert.Single(transport.Requests, request => request.Method == "turn/start");
            Assert.Equal(expected, (string?)turn.Parameters["approvalPolicy"]);
            Assert.False((bool)turn.Parameters.SelectToken("sandboxPolicy.networkAccess")!);
            Assert.Equal(Workspace, (string?)turn.Parameters["cwd"]);
            Assert.Equal("readOnly", (string?)turn.Parameters.SelectToken("sandboxPolicy.type"));
            Assert.Equal("initialized", transport.Notifications[0]);
            Assert.Equal("thread-1", conversation.ThreadId);
        }
    }

    [Fact]
    public async Task AuthenticationUsesBrowserLoginAndCompletionNotification()
    {
        FakeTransport transport = new FakeTransport { AccountResponse = UnauthenticatedAccount() };
        using (CodexAppServerClient client = new CodexAppServerClient(transport))
        {
            await client.StartAsync(CancellationToken.None);
            Assert.Equal(CodexAuthenticationState.AuthenticationRequired, client.Authentication.State);

            CodexLoginRequest login = await client.SignInAsync(CancellationToken.None);
            Assert.Equal("login-1", login.LoginId);
            Assert.Equal("https://example.test/login", login.AuthenticationUrl);
            Assert.Equal("chatgpt", (string?)transport.Requests.Single(request => request.Method == "account/login/start").Parameters["type"]);

            transport.Emit(Notification("account/login/completed", new JObject { ["success"] = true, ["loginId"] = "login-1" }));
            Assert.Equal(CodexAuthenticationState.SigningIn, client.Authentication.State);
            transport.Emit(Notification("account/updated", new JObject { ["authMode"] = "chatgpt", ["planType"] = "plus" }));
            Assert.Equal(CodexAuthenticationState.Authenticated, client.Authentication.State);
        }
    }

    [Fact]
    public async Task ExistingConversationRefusesNextTurnWhenLoginSwitchesToApiKey()
    {
        FakeTransport transport = new FakeTransport();
        using CodexAppServerClient client = new CodexAppServerClient(transport);
        await client.StartAsync(CancellationToken.None);
        await client.StartConversationAsync(Workspace, CancellationToken.None);
        transport.AccountResponse = new JObject { ["account"] = new JObject { ["type"] = "apiKey" } };

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync("hello", CancellationToken.None));

        Assert.Equal(CodexAuthenticationState.AuthenticationRequired, client.Authentication.State);
        Assert.DoesNotContain(transport.Requests, request => request.Method == "turn/start");
    }

    [Fact]
    public async Task FailedLoginAndAccountUpdatesProduceTypedAuthenticationState()
    {
        FakeTransport transport = new FakeTransport { AccountResponse = UnauthenticatedAccount() };
        using (CodexAppServerClient client = new CodexAppServerClient(transport))
        {
            await client.StartAsync(CancellationToken.None);
            await client.SignInAsync(CancellationToken.None);
            transport.Emit(Notification("account/login/completed", new JObject { ["success"] = false, ["loginId"] = "login-1", ["error"] = "expired" }));
            Assert.Equal(CodexAuthenticationState.Failed, client.Authentication.State);

            transport.Emit(Notification("account/updated", new JObject { ["authMode"] = "chatgpt", ["planType"] = "plus" }));
            Assert.Equal(CodexAuthenticationState.Authenticated, client.Authentication.State);
            Assert.Equal("plus", client.Authentication.PlanType);
        }
    }

    [Fact]
    public async Task PendingLoginIsReusedAndUnauthenticatedConversationIsRejected()
    {
        FakeTransport transport = new FakeTransport { AccountResponse = UnauthenticatedAccount() };
        using (CodexAppServerClient client = new CodexAppServerClient(transport))
        {
            await client.StartAsync(CancellationToken.None);
            CodexLoginRequest first = await client.SignInAsync(CancellationToken.None);
            CodexLoginRequest second = await client.SignInAsync(CancellationToken.None);
            Assert.Same(first, second);
            Assert.Single(transport.Requests, request => request.Method == "account/login/start");
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartConversationAsync(Workspace, CancellationToken.None));
        }
    }

    [Fact]
    public async Task CancelledLoginRequestTransitionsToCancelled()
    {
        FakeTransport transport = new FakeTransport { AccountResponse = UnauthenticatedAccount(), CancelLoginRequest = true };
        using (CodexAppServerClient client = new CodexAppServerClient(transport))
        {
            await client.StartAsync(CancellationToken.None);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SignInAsync(CancellationToken.None));
            Assert.Equal(CodexAuthenticationState.Cancelled, client.Authentication.State);
        }
    }

    [Fact]
    public async Task SequentialTurnsReuseThreadAndStaleEventsAreIgnored()
    {
        FakeTransport transport = new FakeTransport();
        using (CodexAppServerClient client = new CodexAppServerClient(transport))
        {
            await client.StartAsync(CancellationToken.None);
            CodexConversation conversation = await client.StartConversationAsync(Workspace, CancellationToken.None);
            await client.SendAsync("first", CancellationToken.None);
            transport.Emit(TurnCompleted(conversation.ThreadId, "turn-1", "completed"));
            await client.SendAsync("second", CancellationToken.None);

            transport.Emit(Notification("item/agentMessage/delta", new JObject { ["threadId"] = "old-thread", ["turnId"] = "turn-2", ["itemId"] = "item", ["delta"] = "ignored" }));
            transport.Emit(Notification("item/agentMessage/delta", new JObject { ["threadId"] = conversation.ThreadId, ["turnId"] = "turn-2", ["itemId"] = "item", ["delta"] = "hello" }));
            transport.Emit(Notification("item/agentMessage/delta", new JObject { ["threadId"] = conversation.ThreadId, ["turnId"] = "turn-2", ["itemId"] = "item", ["delta"] = " world" }));

            Assert.Equal("hello world", conversation.AssistantText);
            Assert.Equal(2, transport.Requests.Count(request => request.Method == "turn/start"));
            Assert.All(transport.Requests.Where(request => request.Method == "turn/start"), request => Assert.Equal(conversation.ThreadId, (string?)request.Parameters["threadId"]));
        }
    }

    [Fact]
    public async Task ConcurrentTurnIsRejectedAndCancellationWaitsForTerminalNotification()
    {
        FakeTransport transport = new FakeTransport();
        using (CodexAppServerClient client = new CodexAppServerClient(transport))
        {
            await client.StartAsync(CancellationToken.None);
            CodexConversation conversation = await client.StartConversationAsync(Workspace, CancellationToken.None);
            await client.SendAsync("first", CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync("second", CancellationToken.None));

            await client.CancelAsync(CancellationToken.None);
            await client.CancelAsync(CancellationToken.None);
            Assert.Single(transport.Requests, request => request.Method == "turn/interrupt");
            Assert.Equal(ConversationTurnState.Cancelling, conversation.State);

            transport.Emit(TurnCompleted(conversation.ThreadId, "turn-1", "interrupted"));
            Assert.Equal(ConversationTurnState.Interrupted, conversation.State);
            await client.SendAsync("after cancel", CancellationToken.None);
            Assert.Equal("turn-2", conversation.ActiveTurnId);
        }
    }

    [Fact]
    public async Task ImmediateCancelWaitsForServerToInstallActiveTurn()
    {
        FakeTransport transport = new FakeTransport { DeferTurnStarted = true };
        using CodexAppServerClient client = new CodexAppServerClient(transport);
        await client.StartAsync(CancellationToken.None);
        CodexConversation conversation = await client.StartConversationAsync(Workspace, CancellationToken.None);
        Task sending = client.SendAsync("hello", CancellationToken.None);
        Task cancelling = client.CancelAsync(CancellationToken.None);
        Assert.Equal(ConversationTurnState.Starting, conversation.State);
        Assert.DoesNotContain(transport.Requests, request => request.Method == "turn/interrupt");

        transport.Emit(TurnStarted(conversation.ThreadId, "turn-1"));
        await Task.WhenAll(sending, cancelling).WaitAsync(TimeSpan.FromSeconds(3));
        Request interrupt = Assert.Single(transport.Requests, request => request.Method == "turn/interrupt");
        Assert.Equal("turn-1", (string?)interrupt.Parameters["turnId"]);
        transport.Emit(TurnCompleted(conversation.ThreadId, "turn-1", "interrupted"));
        Assert.Equal(ConversationTurnState.Interrupted, conversation.State);
    }

    [Fact]
    public async Task DisconnectAfterAcknowledgementReleasesSendAndQueuedCancel()
    {
        FakeTransport transport = new FakeTransport { DeferTurnStarted = true };
        using CodexAppServerClient client = new CodexAppServerClient(transport);
        await client.StartAsync(CancellationToken.None);
        CodexConversation conversation = await client.StartConversationAsync(Workspace, CancellationToken.None);
        Task sending = client.SendAsync("hello", CancellationToken.None);
        Task cancelling = client.CancelAsync(CancellationToken.None);
        transport.RaiseFault(new EndOfStreamException());
        await Assert.ThrowsAsync<InvalidOperationException>(() => sending.WaitAsync(TimeSpan.FromSeconds(3)));
        await cancelling.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ConversationTurnState.Failed, conversation.State);
        Assert.DoesNotContain(transport.Requests, request => request.Method == "turn/interrupt");
    }

    [Fact]
    public async Task NewConversationReplacesSelectionAndTransportFaultFailsOnlyActiveTurn()
    {
        FakeTransport transport = new FakeTransport();
        using (CodexAppServerClient client = new CodexAppServerClient(transport))
        {
            await client.StartAsync(CancellationToken.None);
            CodexConversation first = await client.StartConversationAsync(Workspace, CancellationToken.None);
            CodexConversation second = await client.StartConversationAsync(Workspace, CancellationToken.None);
            Assert.NotEqual(first.ThreadId, second.ThreadId);

            await client.SendAsync("active", CancellationToken.None);
            transport.RaiseFault(new EndOfStreamException());
            Assert.Equal(ConversationTurnState.Failed, second.State);
            Assert.Equal("Codex App Server connection ended.", second.ErrorMessage);
            Assert.Equal(ConversationTurnState.Idle, first.State);
        }
    }

    [Fact]
    public async Task ToolActivityAndFailedTurnAreCorrelatedToActiveConversation()
    {
        FakeTransport transport = new FakeTransport();
        using (CodexAppServerClient client = new CodexAppServerClient(transport))
        {
            List<CodexConversationEventKind> events = new List<CodexConversationEventKind>();
            client.ConversationEventReceived += (_, eventArgs) => events.Add(eventArgs.Kind);
            await client.StartAsync(CancellationToken.None);
            CodexConversation conversation = await client.StartConversationAsync(Workspace, CancellationToken.None);
            await client.SendAsync("analyze", CancellationToken.None);
            foreach (string itemType in new[] { "userMessage", "agentMessage" })
            {
                foreach (string method in new[] { "item/started", "item/completed" })
                {
                    transport.Emit(Notification(method, new JObject { ["threadId"] = conversation.ThreadId, ["turnId"] = "turn-1", ["item"] = new JObject { ["type"] = itemType } }));
                }
            }
            Assert.DoesNotContain(CodexConversationEventKind.ToolActivity, events);
            transport.Emit(Notification("item/started", new JObject { ["threadId"] = conversation.ThreadId, ["turnId"] = "turn-1", ["startedAtMs"] = 1, ["item"] = new JObject { ["type"] = "commandExecution" } }));
            transport.Emit(Notification("turn/completed", new JObject { ["threadId"] = conversation.ThreadId, ["turn"] = new JObject { ["id"] = "turn-1", ["status"] = "failed", ["error"] = new JObject { ["message"] = "read-only denial" } } }));
            Assert.Contains(CodexConversationEventKind.ToolActivity, events);
            Assert.Contains(CodexConversationEventKind.TurnFailed, events);
            Assert.Equal(ConversationTurnState.Failed, conversation.State);
            Assert.Equal("read-only denial", conversation.ErrorMessage);
        }
    }

    [Fact]
    public async Task DispatcherPreservesApprovalId()
    {
        FakeTransport transport = new FakeTransport();
        using (CodexAppServerClient client = new CodexAppServerClient(transport))
        {
            await client.StartAsync(CancellationToken.None);
            transport.Emit(new JObject { ["id"] = 42, ["method"] = "item/permissions/requestApproval", ["params"] = new JObject() });
            await transport.ResponseReceived.Task;
            Assert.Equal(42, (int)transport.ResponseId!);
            Assert.Empty(transport.Response!["permissions"]!);
        }
    }

    private static string Workspace => Path.GetFullPath(Directory.GetCurrentDirectory());
    private static JObject Notification(string method, JObject parameters) => new JObject { ["method"] = method, ["params"] = parameters };
    private static JObject TurnStarted(string threadId, string turnId) => Notification("turn/started", new JObject { ["threadId"] = threadId, ["turn"] = new JObject { ["id"] = turnId, ["status"] = "inProgress" } });
    private static JObject TurnCompleted(string threadId, string turnId, string status) => Notification("turn/completed", new JObject { ["threadId"] = threadId, ["turn"] = new JObject { ["id"] = turnId, ["status"] = status } });
    private static JObject UnauthenticatedAccount() => new JObject { ["requiresOpenaiAuth"] = true, ["account"] = JValue.CreateNull() };

    private sealed class FakeTransport : IJsonRpcTransport
    {
        private int nextThread;
        private int nextTurn;
        public readonly List<Request> Requests = new List<Request>();
        public readonly List<string> Notifications = new List<string>();
        public JObject? Response;
        public JToken? ResponseId;
        public bool CancelLoginRequest { get; set; }
        public bool DeferTurnStarted { get; set; }
        public TaskCompletionSource<bool> ResponseReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public JObject AccountResponse { get; set; } = new JObject { ["requiresOpenaiAuth"] = true, ["account"] = new JObject { ["type"] = "chatgpt", ["planType"] = "plus" } };
        public event EventHandler<JObject>? MessageReceived;
        public event EventHandler<Exception>? Faulted;

        public Task<JObject> RequestAsync(string method, JObject parameters, CancellationToken cancellationToken)
        {
            Requests.Add(new Request(method, parameters));
            if (method == "account/read") return Task.FromResult(AccountResponse);
            if (method == "account/login/start") return CancelLoginRequest ? Task.FromCanceled<JObject>(new CancellationToken(true)) : Task.FromResult(new JObject { ["type"] = "chatgpt", ["loginId"] = "login-1", ["authUrl"] = "https://example.test/login" });
            if (method == "thread/start") return Task.FromResult(new JObject { ["thread"] = new JObject { ["id"] = "thread-" + ++nextThread } });
            if (method == "turn/start")
            {
                string turnId = "turn-" + ++nextTurn;
                if (!DeferTurnStarted) Emit(TurnStarted((string)parameters["threadId"]!, turnId));
                return Task.FromResult(new JObject { ["turn"] = new JObject { ["id"] = turnId } });
            }
            return Task.FromResult(new JObject());
        }
        public Task NotifyAsync(string method, JObject parameters, CancellationToken cancellationToken) { Notifications.Add(method); return Task.CompletedTask; }
        public Task RespondAsync(JToken id, JObject result, CancellationToken cancellationToken) { ResponseId = id; Response = result; ResponseReceived.TrySetResult(true); return Task.CompletedTask; }
        public void Emit(JObject message) => MessageReceived?.Invoke(this, message);
        public void RaiseFault(Exception error) => Faulted?.Invoke(this, error);
        public void Dispose() { }
    }

    private sealed class Request
    {
        public Request(string method, JObject parameters) { Method = method; Parameters = parameters; }
        public string Method { get; }
        public JObject Parameters { get; }
    }
}
