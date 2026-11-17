using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodexForVS.Client;

public interface IJsonRpcTransport : IDisposable
{
    event EventHandler<JObject>? MessageReceived;
    event EventHandler<Exception>? Faulted;
    Task<JObject> RequestAsync(string method, JObject parameters, CancellationToken cancellationToken);
    Task NotifyAsync(string method, JObject parameters, CancellationToken cancellationToken);
    Task RespondAsync(JToken id, JObject result, CancellationToken cancellationToken);
}

public interface ICodexProcess : IDisposable
{
    event EventHandler? Exited;
    event EventHandler<string>? StandardErrorReceived;
    IJsonRpcTransport Start();
    bool IsRunning { get; }
}

public interface IWorkspaceContext
{
    string? WorkspacePath { get; }
    event EventHandler? WorkspaceChanged;
}
