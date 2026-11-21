using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexForVS.Client;
using CodexForVS.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexForVS.Tests;

public sealed partial class GitWorktreeTests
{
    [Theory]
    [InlineData("ready", true)]
    [InlineData("updateRequired", false)]
    [InlineData("extra-root", false)]
    [InlineData("live-mcp", false)]
    public async Task ImplementationValidatesEffectiveIsolationBeforeModelWork(string scenario, bool usable)
    {
        await CommitFiles();
        WorktreeSession session = await Service.PrepareAsync(Solution, _ => { }, default);
        var transport = new CodexAppServerClientTests.FakeTransport();
        using CodexAppServerClient client = new CodexAppServerClient(transport);
        transport.Reply = (method, parameters) =>
        {
            switch (method)
            {
                case "windowsSandbox/readiness": return new JObject { ["status"] = scenario == "updateRequired" ? scenario : "ready" };
                case "thread/start":
                    return new JObject
                    {
                        ["thread"] = new JObject { ["id"] = "isolated" },
                        ["cwd"] = session.WorktreePath,
                        ["approvalPolicy"] = "untrusted",
                        ["approvalsReviewer"] = "user",
                        ["model"] = "chosen",
                        ["runtimeWorkspaceRoots"] = new JArray(session.WorktreePath),
                        ["sandbox"] = WorktreeCommand.Sandbox(scenario == "extra-root" ? Repository : session.WorktreePath)
                    };
                case "mcpServerStatus/list":
                    return new JObject
                    {
                        ["data"] = new JArray(new JObject
                        {
                            ["name"] = "fixture",
                            ["runtimeStatus"] = scenario == "live-mcp" ? "connected" : "disabled"
                        })
                    };
                case "command/exec": return JObject.Parse("{exitCode:0,stdout:'worktree only',stderr:''}");
                default: return null;
            }
        };
        await client.StartAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartConversationAsync(Repository, default, "untrusted", session));
        if (!usable)
        {
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.StartConversationAsync(session.WorktreePath, default, "untrusted", session, "chosen"));
            Assert.DoesNotContain(transport.Requests, r => r.Method == "turn/start");
            return;
        }
        CodexConversation thread = await client.StartConversationAsync(session.WorktreePath, default, "untrusted", session, "chosen");
        JObject start = Assert.Single(transport.Requests, r => r.Method == "thread/start").Parameters;
        Assert.False((bool)start["config"]!["features.shell_tool"]!);
        Assert.False((bool)start["config"]!["mcp_servers.fixture.enabled"]!);
        Assert.False((bool)start["allowProviderModelFallback"]!);
        Assert.Equal("worktree_command", (string?)start.SelectToken("dynamicTools[0].name"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SendAsync("wrong mode", default, SessionMode.Chat));
        await client.SendAsync("Implement my plan", default, SessionMode.Implement);
        JObject turn = Assert.Single(transport.Requests, r => r.Method == "turn/start").Parameters;
        Assert.Equal(session.WorktreePath, (string?)turn["cwd"]);
        Assert.True(JToken.DeepEquals(WorktreeCommand.Sandbox(session.WorktreePath), turn["sandboxPolicy"]));
        Assert.Equal("untrusted", (string?)turn["approvalPolicy"]);
        JObject call = new JObject
        {
            ["method"] = "item/tool/call",
            ["id"] = "request",
            ["params"] = new JObject
            {
                ["tool"] = "worktree_command",
                ["threadId"] = thread.ThreadId,
                ["turnId"] = thread.ActiveTurnId,
                ["arguments"] = new JObject { ["command"] = "Get-ChildItem" }
            }
        };
        client.ConfirmManagedCommandAsync = _ => Task.FromResult(false);
        transport.Emit(call);
        await transport.ResponseReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False((bool)transport.Response!["success"]!);
        Assert.DoesNotContain(transport.Requests, r => r.Method == "command/exec");
        client.ConfirmManagedCommandAsync = command => Task.FromResult(command == "Get-ChildItem");
        transport.ResponseReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Emit(call);
        await transport.ResponseReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((bool)transport.Response!["success"]!);
        JObject execution = Assert.Single(transport.Requests, r => r.Method == "command/exec").Parameters;
        Assert.Equal(session.WorktreePath, (string?)execution["cwd"]);
        Assert.True(JToken.DeepEquals(turn["sandboxPolicy"], execution["sandboxPolicy"]));
        Assert.Equal("request", (string?)transport.ResponseId);

        TaskCompletionSource<bool> decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConfirmManagedCommandAsync = _ => decision.Task;
        transport.ResponseReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        call["id"] = "late";
        transport.Emit(call);
        await client.CancelAsync(default);
        decision.SetResult(true);
        await transport.ResponseReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((bool)transport.Response!["success"]!);
        Assert.Single(transport.Requests, r => r.Method == "command/exec");
    }

    [Fact]
    public async Task ReadOnlyPlanCanInspectFilesAndUntrustedCommandsCannotRun()
    {
        await CommitFiles(("note.txt", System.Text.Encoding.UTF8.GetBytes("workspace contents")));
        string project = Directory.CreateDirectory(Path.Combine(Repository, "McpServer1")).FullName;
        File.WriteAllText(Path.Combine(project, "Program.cs"), "class Program { }");
        var transport = new CodexAppServerClientTests.FakeTransport();
        using CodexAppServerClient client = new CodexAppServerClient(transport);
        await client.StartAsync(default);
        CodexConversation thread = await client.StartConversationAsync(Repository + Path.DirectorySeparatorChar, default, "untrusted");
        await client.SendAsync("How would you change this?", default, SessionMode.Plan);
        async Task<JObject> Call(string tool, JObject arguments)
        {
            transport.ResponseReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.Emit(new JObject
            {
                ["method"] = "item/tool/call",
                ["id"] = "probe",
                ["params"] = new JObject
                { ["threadId"] = thread.ThreadId, ["turnId"] = thread.ActiveTurnId, ["tool"] = tool, ["arguments"] = arguments }
            });
            await transport.ResponseReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            return transport.Response!;
        }
        JObject result = await Call("inspect_workspace", new JObject { ["path"] = "note.txt" });
        Assert.Equal("workspace contents", (string?)result.SelectToken("contentItems[0].text"));
        foreach (string root in new[] { ".", "./", @".\", Repository.Replace('\\', '/') })
        {
            JObject listing = await Call("inspect_workspace", new JObject { ["path"] = root });
            Assert.True((bool)listing["success"]!);
            Assert.Contains("McpServer1/", ((string)listing.SelectToken("contentItems[0].text")!).Split('\n'));
        }
        JObject missing = await Call("inspect_workspace", new JObject { ["path"] = "Program.cs" });
        Assert.False((bool)missing["success"]!);
        Assert.Contains("List .", (string?)missing.SelectToken("contentItems[0].text"));
        foreach (string path in new[] { @"McpServer1\Program.cs", "./McpServer1/Program.cs", Path.Combine(project, "Program.cs") })
        {
            JObject source = await Call("inspect_workspace", new JObject { ["path"] = path });
            Assert.Equal("class Program { }", (string?)source.SelectToken("contentItems[0].text"));
        }
        foreach (string outside in new[] { Repository + "-sibling/file", Repository + "/../escape", @".\.git\config" })
            Assert.False((bool)(await Call("inspect_workspace", new JObject { ["path"] = outside }))["success"]!);
        Assert.False((bool)(await Call("inspect_workspace", new JObject { ["path"] = "../escape" }))["success"]!);
        Assert.False((bool)(await Call("workspace_command", new JObject { ["command"] = "Set-Content note.txt other" }))["success"]!);
        Assert.False((bool)(await Call("worktree_command", new JObject { ["command"] = "Set-Content note.txt other" }))["success"]!);
        Assert.DoesNotContain(transport.Requests, r => r.Method == "command/exec");
        Assert.Equal("workspace contents", File.ReadAllText(Path.Combine(Repository, "note.txt")));
    }

    [Fact]
    public async Task CapturesEditsMadeBetweenPreparationAndImplementation()
    {
        byte[] original = { 1, 2 };
        await CommitFiles(("late.bin", original));
        WorktreeSession session = await Service.PrepareAsync(Solution, _ => { }, default);
        string active = Path.Combine(Repository, "late.bin");
        File.WriteAllText(active, "my pre-run edit");
        await session.Review.BeforeImplementationAsync(default);
        File.WriteAllBytes(active, original);
        File.WriteAllText(Path.Combine(session.WorktreePath, "late.bin"), "changed");
        FileProposal proposal = Assert.Single(await session.Review.DiscoverAsync(default));
        session.Review.Preview(proposal);
        await Assert.ThrowsAsync<IOException>(() => session.Review.AcceptAsync(proposal, NoEditors, default));
        Assert.Equal(original, File.ReadAllBytes(active));
    }
}
