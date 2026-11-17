using System;
using CodexForVS.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CodexForVS.Extension;

public sealed class CodexChatToolWindow : ToolWindowPane
{
    private readonly CodexAppServerClient client;
    private readonly CodexChatViewModel viewModel;
    private readonly IDisposable? workspaceContext;

    public CodexChatToolWindow() : base(null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Caption = "Codex Chat";
        IVsSolution? solution = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
        IWorkspaceContext context = solution == null ? new EmptyWorkspaceContext() : new VisualStudioWorkspaceContext(solution);
        workspaceContext = context as IDisposable;
        client = new CodexAppServerClient();
        viewModel = new CodexChatViewModel(client, context, new WpfUiDispatcher());
        Content = new CodexChatView(viewModel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { viewModel.Dispose(); client.Dispose(); workspaceContext?.Dispose(); }
        base.Dispose(disposing);
    }
}
