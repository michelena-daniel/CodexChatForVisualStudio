using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodexForVS.Core;
using Newtonsoft.Json.Linq;

namespace CodexForVS.Client;

// A model-generated command never goes through Visual Studio's shell or inherits a prefix-rule
// approval. command/exec is a host request with an explicit per-command sandbox policy.
internal sealed class WorktreeCommand
{
    private readonly object gate = new object();
    private readonly Dictionary<string, IJsonRpcTransport> processes = new Dictionary<string, IJsonRpcTransport>();

    internal static JObject Definition(bool implementation = true) => new JObject
    {
        ["type"] = "function",
        ["name"] = implementation ? "worktree_command" : "workspace_command",
        ["description"] = implementation ? "Run PowerShell in the isolated worktree with developer confirmation." : "Run PowerShell with a read-only sandbox in the solution directory. Prefer inspect_workspace for reading files.",
        ["inputSchema"] = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["command"] = new JObject { ["type"] = "string" } },
            ["required"] = new JArray("command"),
            ["additionalProperties"] = false
        }
    };

    internal static JObject Sandbox(string root) => new JObject
    {
        ["type"] = "workspaceWrite",
        ["writableRoots"] = new JArray(root),
        ["networkAccess"] = false,
        ["excludeTmpdirEnvVar"] = true,
        ["excludeSlashTmp"] = true
    };

    internal static JObject Result(bool success, string text) => new JObject
    {
        ["success"] = success,
        ["contentItems"] = new JArray(new JObject { ["type"] = "inputText", ["text"] = text })
    };

    internal async Task<JObject> RunAsync(IJsonRpcTransport transport, CodexConversation conversation, string command,
        Func<string, Task<bool>>? confirm, Func<bool> stillRunning)
    {
        WorktreeSession? session = conversation.Worktree;
        if (!stillRunning()) return Result(false, "Turn ended.");
        if (session != null)
        {
            if (confirm == null || !await confirm(command).ConfigureAwait(false)) return Result(false, "Command declined.");
            await session.Review.VerifyAsync().ConfigureAwait(false);
        }
        else
        {
            if (conversation.ApprovalPolicy != ReadOnlyApprovalPolicy.Never) return Result(false, "Use inspect_workspace under untrusted policy.");
            JObject readiness = await transport.RequestAsync("windowsSandbox/readiness", new JObject(), CancellationToken.None).ConfigureAwait(false);
            if ((string?)readiness["status"] != "ready") return Result(false, "Use inspect_workspace while Windows sandbox setup is pending.");
        }
        string id = Guid.NewGuid().ToString("N");
        Task<JObject> execution;
        lock (gate)
        {
            if (!stillRunning()) return Result(false, "Turn ended.");
            processes.Add(id, transport);
            string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            JObject parameters = new JObject
            {
                ["processId"] = id,
                ["command"] = new JArray(powershell, "-NoProfile", "-NonInteractive", "-Command", command),
                ["cwd"] = conversation.WorkspaceDirectory,
                ["sandboxPolicy"] = session == null ? new JObject { ["type"] = "readOnly", ["networkAccess"] = false } : Sandbox(session.WorktreePath),
                ["timeoutMs"] = 120000,
                ["outputBytesCap"] = 32768
            };
            execution = transport.RequestAsync("command/exec", parameters, CancellationToken.None);
        }
        try
        {
            JObject result = await execution.ConfigureAwait(false);
            return Result((int?)result["exitCode"] == 0, (string?)result["stdout"] + "\n" + (string?)result["stderr"]);
        }
        finally { lock (gate) processes.Remove(id); }
    }

    internal void StopAll()
    {
        KeyValuePair<string, IJsonRpcTransport>[] pending;
        lock (gate) { pending = new List<KeyValuePair<string, IJsonRpcTransport>>(processes).ToArray(); }
        foreach (var process in pending) _ = StopAsync(process);
    }

    private static async Task StopAsync(KeyValuePair<string, IJsonRpcTransport> process)
    {
        try { await process.Value.RequestAsync("command/exec/terminate", new JObject { ["processId"] = process.Key }, CancellationToken.None).ConfigureAwait(false); }
        catch { /* Already exited or the owning connection closed. */ }
    }
}
