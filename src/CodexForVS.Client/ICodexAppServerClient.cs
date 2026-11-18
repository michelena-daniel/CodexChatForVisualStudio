using System;
using System.Threading;
using System.Threading.Tasks;
using CodexForVS.Core;

namespace CodexForVS.Client;

/// <summary>
/// Marker boundary for the official Codex App Server integration planned for M1.
/// No process or protocol behavior is implemented in M0.
/// </summary>
public interface ICodexAppServerClient : System.IDisposable
{
    CodexAuthenticationInfo Authentication { get; }
    CodexConversation? CurrentConversation { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task<CodexLoginRequest> SignInAsync(CancellationToken cancellationToken);
    Task<CodexConversation> StartConversationAsync(string workspaceDirectory, CancellationToken cancellationToken, string approvalPolicy = ReadOnlyApprovalPolicy.Never);
    Task SendAsync(string text, CancellationToken cancellationToken);
    Task CancelAsync(CancellationToken cancellationToken);
    event EventHandler<CodexEventArgs>? EventReceived;
    event EventHandler<CodexAuthenticationInfo>? AuthenticationChanged;
    event EventHandler<CodexConversationEventArgs>? ConversationEventReceived;
}
public sealed class CodexEventArgs : System.EventArgs { public CodexEventArgs(string kind,string text) { Kind=kind; Text=text; } public string Kind { get; } public string Text { get; } }
