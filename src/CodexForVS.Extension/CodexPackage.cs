using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace CodexForVS.Extension;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
// Increment this value whenever a package update must cause Visual Studio to
// reload the command-table resource from its configuration cache.
[ProvideMenuResource("Menus.ctmenu", 4)]
[ProvideToolWindow(typeof(CodexChatToolWindow))]
[Guid(Guids.PackageString)]
public sealed class CodexPackage : AsyncPackage
{
    internal const int ShowToolWindowCommandId = 0x0100;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        IMenuCommandService? commandService = await GetServiceAsync(typeof(IMenuCommandService)) as IMenuCommandService;
        if (commandService is null)
        {
            return;
        }

        CommandID commandId = new(Guids.CommandSet, ShowToolWindowCommandId);
        MenuCommand command = new(ShowToolWindow, commandId);
        commandService.AddCommand(command);
    }

    private void ShowToolWindow(object sender, EventArgs e)
    {
        JoinableTaskFactory.RunAsync(async delegate
        {
            ToolWindowPane? toolWindow = await ShowToolWindowAsync(typeof(CodexChatToolWindow), 0, true, DisposalToken);
            if (toolWindow is null || toolWindow.Frame is null)
            {
                throw new NotSupportedException("Cannot create the Codex Chat tool window.");
            }
        }).FileAndForget("CodexForVS/ShowToolWindow");
    }
}
