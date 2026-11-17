using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace CodexForVS.Core;
public enum ChatConnectionState { Disconnected, Connecting, AuthenticationRequired, Ready, Running, Error }
public sealed class ChatMessage : INotifyPropertyChanged
{
    private string content;
    public ChatMessage(string role, string content) { Role = role; this.content = content; Timestamp = DateTimeOffset.Now; }
    public string Role { get; }
    public string Content { get { return content; } set { if (content == value) return; content = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content))); } }
    public DateTimeOffset Timestamp { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
}
