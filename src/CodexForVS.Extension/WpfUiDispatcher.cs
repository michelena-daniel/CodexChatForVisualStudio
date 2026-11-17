using System;
using System.Threading.Tasks;
using CodexForVS.Client;
using Microsoft.VisualStudio.Shell;

namespace CodexForVS.Extension;

internal sealed class WpfUiDispatcher : IUiDispatcher
{
    public async Task InvokeAsync(Action action)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        action();
    }
}
