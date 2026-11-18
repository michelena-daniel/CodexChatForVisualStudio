using System;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using CodexForVS.Client;

namespace CodexForVS.Extension;

public partial class CodexChatView : UserControl
{
    public CodexChatView(CodexChatViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        viewModel.AuthenticationUrlAvailable += OpenAuthenticationUrl;
    }

    private void OnHistoryScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange > 0) ((ScrollViewer)sender).ScrollToEnd();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        if (!e.IsRepeat && DataContext is CodexChatViewModel model && model.SendMessageCommand.CanExecute(null!))
            model.SendMessageCommand.Execute(null!);
    }

    private void OpenAuthenticationUrl(object sender, string authenticationUrl)
    {
        try { Process.Start(new ProcessStartInfo(authenticationUrl) { UseShellExecute = true }); }
        catch { }
    }
}
