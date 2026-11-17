using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexForVS.Client;

public sealed class JsonlJsonRpcTransport : IJsonRpcTransport
{
    private readonly TextReader reader; private readonly TextWriter writer; private readonly object gate = new object(); private readonly Dictionary<long, TaskCompletionSource<JObject>> pending = new Dictionary<long, TaskCompletionSource<JObject>>(); private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1); private long nextId; private Exception? terminal;
    public JsonlJsonRpcTransport(TextReader reader, TextWriter writer) { this.reader=reader; this.writer=writer; _=ReadLoopAsync(); }
    public event EventHandler<JObject>? MessageReceived; public event EventHandler<Exception>? Faulted;
    public async Task<JObject> RequestAsync(string method,JObject parameters,CancellationToken token) { TaskCompletionSource<JObject> source=new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously); long id; lock(gate){if(terminal!=null)throw new InvalidOperationException("Transport is closed.",terminal);id=++nextId;pending.Add(id,source);} try{await WriteAsync(new JObject{{"method",method},{"id",id},{"params",parameters}},token).ConfigureAwait(false);}catch(Exception error){Complete(id,error);throw;} using(token.Register(()=>Complete(id,new OperationCanceledException(token))))return await source.Task.ConfigureAwait(false); }
    public Task NotifyAsync(string method,JObject parameters,CancellationToken token){lock(gate)if(terminal!=null)throw new InvalidOperationException("Transport is closed.",terminal);return WriteAsync(new JObject{{"method",method},{"params",parameters}},token);}
    public Task RespondAsync(JToken id,JObject result,CancellationToken token)=>WriteAsync(new JObject{{"id",id},{"result",result}},token);
    private async Task WriteAsync(JObject message,CancellationToken token){await writeLock.WaitAsync(token).ConfigureAwait(false);try{await writer.WriteLineAsync(message.ToString(Formatting.None)).ConfigureAwait(false);await writer.FlushAsync().ConfigureAwait(false);}finally{writeLock.Release();}}
    private async Task ReadLoopAsync(){try{string? line;while((line=await reader.ReadLineAsync().ConfigureAwait(false))!=null){JObject message;try{message=JObject.Parse(line);}catch(JsonException error){Faulted?.Invoke(this,error);continue;}JToken? id=message["id"];TaskCompletionSource<JObject>? request=null;if(id?.Type==JTokenType.Integer){lock(gate){if(pending.TryGetValue((long)id,out request))pending.Remove((long)id);}if(request!=null){if(message["error"] is JObject error)request.TrySetException(new JsonRpcRemoteException((string?)error["message"]??"Remote error"));else request.TrySetResult((JObject?)message["result"]??new JObject());continue;}}MessageReceived?.Invoke(this,message);}Terminate(new EndOfStreamException("JSON-RPC stream closed."));}catch(Exception error){Terminate(error);}}
    private void Complete(long id,Exception error){TaskCompletionSource<JObject>? request=null;lock(gate){if(pending.TryGetValue(id,out request))pending.Remove(id);}request?.TrySetException(error);}
    private void Terminate(Exception error){List<TaskCompletionSource<JObject>> requests;lock(gate){if(terminal!=null)return;terminal=error;requests=new List<TaskCompletionSource<JObject>>(pending.Values);pending.Clear();}foreach(TaskCompletionSource<JObject> request in requests)request.TrySetException(error);Faulted?.Invoke(this,error);}
    public void Dispose(){Terminate(new ObjectDisposedException(nameof(JsonlJsonRpcTransport)));reader.Dispose();writer.Dispose();writeLock.Dispose();}
}
public sealed class JsonRpcRemoteException : Exception { public JsonRpcRemoteException(string message):base(message){} }
