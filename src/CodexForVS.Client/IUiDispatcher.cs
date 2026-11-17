using System;
using System.Threading.Tasks;

namespace CodexForVS.Client;

/// <summary>Minimal UI-marshalling boundary so the chat state can be tested without WPF or Visual Studio.</summary>
public interface IUiDispatcher
{
    Task InvokeAsync(Action action);
}
