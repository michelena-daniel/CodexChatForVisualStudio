using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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
        UTF8Encoding utf8 = new UTF8Encoding(false);
        ProcessStartInfo startInfo = new ProcessStartInfo(CodexExecutable.Resolve(), "app-server --listen stdio:// -c windows.sandbox=elevated --disable hooks --disable apps --disable plugins --disable computer_use --disable multi_agent_v2") { WorkingDirectory=Environment.SystemDirectory, RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true, StandardOutputEncoding=utf8, StandardErrorEncoding=utf8, UseShellExecute=false, CreateNoWindow=true };
        startInfo.EnvironmentVariables.Remove("OPENAI_API_KEY");
        startInfo.EnvironmentVariables.Remove("CODEX_API_KEY");
        process = new Process { StartInfo=startInfo, EnableRaisingEvents=true };
        process.Exited += (_,__) => Exited?.Invoke(this,EventArgs.Empty);
        try { if (!process.Start()) throw new InvalidOperationException("Codex App Server did not start."); }
        catch (Exception error) { process.Dispose(); process=null; throw new CodexLaunchException("Visual Studio found codex.exe, but Windows could not start it.",error); }
        // .NET Framework has no ProcessStartInfo.StandardInputEncoding.
        transport = new JsonlJsonRpcTransport(process.StandardOutput, new StreamWriter(process.StandardInput.BaseStream, utf8));
        _ = ReadErrorsAsync(process.StandardError); return transport;
    }
    private async Task ReadErrorsAsync(System.IO.StreamReader errors) { string? line; while((line=await errors.ReadLineAsync().ConfigureAwait(false)) is not null) StandardErrorReceived?.Invoke(this,line); }
    public void Dispose() { transport?.Dispose(); transport=null; try { if(process is not null&&!process.HasExited) process.Kill(); } catch {} process?.Dispose(); process=null; }
}
