using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodexForVS.Client;

internal static class CodexThreadTools
{
    internal static async Task ConfigureAsync(IJsonRpcTransport transport, JObject parameters, bool implementation, CancellationToken cancellationToken)
    {
        parameters["approvalsReviewer"] = "user";
        parameters["dynamicTools"] = new JArray(WorktreeCommand.Definition(implementation), WorkspaceInspection.Definition());
        JObject config = new JObject
        {
            ["sandbox_workspace_write"] = new JObject { ["writable_roots"] = new JArray(), ["network_access"] = false, ["exclude_tmpdir_env_var"] = true, ["exclude_slash_tmp"] = true },
            ["windows.sandbox"] = "elevated",
            ["web_search"] = "disabled",
            ["features.hooks"] = false,
            ["features.plugins"] = false,
            ["features.apps"] = false,
            ["features.computer_use"] = false,
            ["features.multi_agent_v2"] = false,
            ["features.multi_agent"] = false,
            ["features.shell_tool"] = false,
            ["features.unified_exec"] = false,
            ["features.code_mode"] = false,
            ["features.browser_use"] = false
        };
        foreach (JObject server in await McpInventoryAsync(transport, null, cancellationToken).ConfigureAwait(false))
        {
            string? name = (string?)server["name"];
            if (string.IsNullOrEmpty(name) || !System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9_-]+$"))
                throw new InvalidOperationException("Cannot safely disable an external tool server.");
            config["mcp_servers." + name + ".enabled"] = false;
        }
        parameters["config"] = config;
    }

    internal static async Task VerifyAsync(IJsonRpcTransport transport, string threadId, CancellationToken token)
    {
        if ((await McpInventoryAsync(transport, threadId, token).ConfigureAwait(false)).Any(s => (string?)s["runtimeStatus"] != "disabled"))
            throw new InvalidOperationException("External MCP tools remain enabled. The thread cannot run with this configuration.");
    }

    private static async Task<List<JObject>> McpInventoryAsync(IJsonRpcTransport transport, string? threadId, CancellationToken token)
    {
        List<JObject> servers = new List<JObject>();
        string? cursor = null;
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            JObject page = await transport.RequestAsync("mcpServerStatus/list", new JObject { ["threadId"] = threadId, ["cursor"] = cursor }, token).ConfigureAwait(false);
            if (!(page["data"] is JArray data)) throw new InvalidDataException("Could not verify external tools.");
            foreach (JToken server in data) servers.Add(server as JObject ?? throw new InvalidDataException("Invalid external tool inventory."));
            cursor = (string?)page["nextCursor"];
            if (cursor != null && !seen.Add(cursor)) throw new InvalidDataException("Incomplete external tool inventory.");
        } while (cursor != null);
        return servers;
    }
}
