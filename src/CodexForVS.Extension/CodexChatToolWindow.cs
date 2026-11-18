using System;
using System.ComponentModel;
using CodexForVS.Client;
using CodexForVS.Core;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;
using Microsoft.VisualStudio.Shell.Interop;

namespace CodexForVS.Extension;

public sealed class CodexChatToolWindow : ToolWindowPane
{
    private readonly CodexAppServerClient client;
    private readonly CodexChatViewModel viewModel;
    private readonly IDisposable? workspaceContext;
    private const string SettingsCollection = "CodexForVS";
    private const string PolicySetting = "ReadOnlyApprovalPolicy";
    private readonly WritableSettingsStore settings;

    public CodexChatToolWindow() : base(null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Caption = "Codex Chat";
        IVsSolution? solution = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
        IWorkspaceContext context = solution == null ? new EmptyWorkspaceContext() : new VisualStudioWorkspaceContext(solution);
        workspaceContext = context as IDisposable;
        client = new CodexAppServerClient();
        settings = new ShellSettingsManager(ServiceProvider.GlobalProvider).GetWritableSettingsStore(SettingsScope.UserSettings);
        settings.CreateCollection(SettingsCollection);
        string savedPolicy = settings.GetString(SettingsCollection, PolicySetting, ReadOnlyApprovalPolicy.Never);
        viewModel = new CodexChatViewModel(client, context, new WpfUiDispatcher(),
            savedPolicy == ReadOnlyApprovalPolicy.Never ? ReadOnlyApprovalPolicy.Never : ReadOnlyApprovalPolicy.Untrusted);
        viewModel.PropertyChanged += SavePolicy;
        Content = new CodexChatView(viewModel);
    }

    private void SavePolicy(object sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(viewModel.ApprovalPolicy))
            settings.SetString(SettingsCollection, PolicySetting, viewModel.ApprovalPolicy);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { viewModel.PropertyChanged -= SavePolicy; viewModel.Dispose(); client.Dispose(); workspaceContext?.Dispose(); }
        base.Dispose(disposing);
    }
}
