using System;

namespace CodexForVS.Core;

public enum CodexAuthenticationState { Unknown, AuthenticationRequired, SigningIn, Authenticated, Cancelled, Failed }

public sealed class CodexAuthenticationInfo
{
    public CodexAuthenticationInfo(CodexAuthenticationState state, string? planType = null, string? errorMessage = null) { State = state; PlanType = planType; ErrorMessage = errorMessage; }
    public CodexAuthenticationState State { get; }
    public string? PlanType { get; }
    public string? ErrorMessage { get; }
}

public sealed class CodexLoginRequest
{
    public CodexLoginRequest(string loginId, string authenticationUrl) { LoginId = loginId ?? throw new ArgumentNullException(nameof(loginId)); AuthenticationUrl = authenticationUrl ?? throw new ArgumentNullException(nameof(authenticationUrl)); }
    public string LoginId { get; }
    public string AuthenticationUrl { get; }
}

public enum ConversationTurnState { Idle, Starting, Running, Cancelling, Completed, Failed, Interrupted }

public sealed class CodexConversation
{
    public CodexConversation(string threadId, string workspaceDirectory) { ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId)); WorkspaceDirectory = workspaceDirectory ?? throw new ArgumentNullException(nameof(workspaceDirectory)); State = ConversationTurnState.Idle; }
    public string ThreadId { get; }
    public string WorkspaceDirectory { get; }
    public string? ActiveTurnId { get; internal set; }
    public ConversationTurnState State { get; internal set; }
    public string AssistantText { get; internal set; } = string.Empty;
    public string? ErrorMessage { get; internal set; }
}

public enum CodexConversationEventKind { AssistantDelta, AgentMessage, ToolActivity, TurnStarted, TurnCompleted, TurnInterrupted, TurnFailed, Error, Disconnected }

public sealed class CodexConversationEventArgs : EventArgs
{
    public CodexConversationEventArgs(CodexConversationEventKind kind, string threadId, string? turnId, string? text = null) { Kind = kind; ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId)); TurnId = turnId; Text = text; }
    public CodexConversationEventKind Kind { get; }
    public string ThreadId { get; }
    public string? TurnId { get; }
    public string? Text { get; }
}
