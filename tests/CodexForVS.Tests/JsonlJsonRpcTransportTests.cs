using System;using System.Collections.Concurrent;using System.IO;using System.Threading;using System.Threading.Tasks;using CodexForVS.Client;using Newtonsoft.Json.Linq;using Xunit;
namespace CodexForVS.Tests;
public sealed class JsonlJsonRpcTransportTests{
[Fact]public async Task CorrelatesOutOfOrderResponses(){using var w=new Wire();using var t=new JsonlJsonRpcTransport(w.Reader,w.Writer);Task<JObject>a=t.RequestAsync("a",new JObject(),CancellationToken.None);Task<JObject>b=t.RequestAsync("b",new JObject(),CancellationToken.None);await w.Writer.WaitAsync(2);w.Push("{\"id\":2,\"result\":{\"v\":2}}");w.Push("{\"id\":1,\"result\":{\"v\":1}}");Assert.Equal(1,(int)(await a)["v"]!);Assert.Equal(2,(int)(await b)["v"]!);}
[Fact]public async Task EofFaultsPendingAndFutureRequests(){using var w=new Wire();using var t=new JsonlJsonRpcTransport(w.Reader,w.Writer);Task<JObject>r=t.RequestAsync("x",new JObject(),CancellationToken.None);await w.Writer.WaitAsync(1);w.Close();await Assert.ThrowsAsync<EndOfStreamException>(()=>r);await Assert.ThrowsAsync<InvalidOperationException>(()=>t.RequestAsync("y",new JObject(),CancellationToken.None));}
[Fact]
public async Task ServerToolCallWithSameIdCannotCompleteAnOutgoingRequest()
{
    using Wire wire = new Wire();
    using JsonlJsonRpcTransport transport = new JsonlJsonRpcTransport(wire.Reader,wire.Writer);
    TaskCompletionSource<JObject> inbound = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
    transport.MessageReceived += (_,message) => inbound.TrySetResult(message);
    Task<JObject> pending = transport.RequestAsync("turn/start",new JObject(),default);
    await wire.Writer.WaitAsync(1);
    wire.Push("{\"id\":1,\"method\":\"item/tool/call\",\"params\":{}}");
    Assert.Equal("item/tool/call",(string?)(await inbound.Task.WaitAsync(TimeSpan.FromSeconds(3)))["method"]);
    Assert.False(pending.IsCompleted);
    wire.Push("{\"id\":1,\"result\":{\"ok\":true}}");
    Assert.True((bool)(await pending.WaitAsync(TimeSpan.FromSeconds(3)))["ok"]!);
}
private sealed class Wire:IDisposable{public QueueReader Reader=new QueueReader();public Recorder Writer=new Recorder();public void Push(string s)=>Reader.Push(s);public void Close()=>Reader.Close();public void Dispose()=>Reader.Close();}
private sealed class QueueReader:TextReader{private readonly ConcurrentQueue<string>q=new ConcurrentQueue<string>();private readonly SemaphoreSlim s=new SemaphoreSlim(0);private bool closed;public void Push(string x){q.Enqueue(x);s.Release();}public new void Close(){closed=true;s.Release();}public override async Task<string?> ReadLineAsync(){await s.WaitAsync();return q.TryDequeue(out string?x)?x:closed?null:null;}}
private sealed class Recorder:TextWriter{private readonly SemaphoreSlim s=new SemaphoreSlim(0);public override System.Text.Encoding Encoding=>System.Text.Encoding.UTF8;public override Task WriteLineAsync(string?x){s.Release();return Task.CompletedTask;}public async Task WaitAsync(int n){while(n-->0)await s.WaitAsync();}}
}
