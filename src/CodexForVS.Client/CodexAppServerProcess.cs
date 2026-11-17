using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace CodexForVS.Client;

public sealed class CodexAppServerProcess : ICodexProcess
{
    private Process? process; private IJsonRpcTransport? transport;
    public event EventHandler? Exited; public event EventHandler<string>? StandardErrorReceived;
    public bool IsRunning => process is not null && !process.HasExited;
    public IJsonRpcTransport Start()
    {
        if (transport is not null) return transport;
        ProcessStartInfo startInfo = new ProcessStartInfo("codex", "app-server --listen stdio://") { RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true, UseShellExecute=false, CreateNoWindow=true };
        process = new Process { StartInfo=startInfo, EnableRaisingEvents=true };
        process.Exited += (_,__) => Exited?.Invoke(this,EventArgs.Empty);
        try { if (!process.Start()) throw new InvalidOperationException("Codex App Server did not start."); }
        catch (Exception error) { process.Dispose(); process=null; throw new InvalidOperationException("Unable to start the Codex CLI. Verify that 'codex' is installed and on PATH.",error); }
        transport = new JsonlJsonRpcTransport(process.StandardOutput,process.StandardInput);
        _ = ReadErrorsAsync(process.StandardError); return transport;
    }
    private async Task ReadErrorsAsync(System.IO.StreamReader errors) { string? line; while((line=await errors.ReadLineAsync().ConfigureAwait(false)) is not null) StandardErrorReceived?.Invoke(this,line); }
    public void Dispose() { transport?.Dispose(); transport=null; try { if(process is not null&&!process.HasExited) process.Kill(); } catch {} process?.Dispose(); process=null; }
}
