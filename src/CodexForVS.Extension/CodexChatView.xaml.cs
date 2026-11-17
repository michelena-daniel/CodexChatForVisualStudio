using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Controls;
using CodexForVS.Client;

namespace CodexForVS.Extension;

public partial class CodexChatView : UserControl
{
    public CodexChatView(CodexChatViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        viewModel.AuthenticationUrlAvailable += OpenAuthenticationUrl;
        viewModel.Messages.CollectionChanged += OnMessagesChanged;
    }

    private void OnMessagesChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (object item in e.NewItems) if (item is INotifyPropertyChanged message) message.PropertyChanged += OnMessagePropertyChanged;
        if (e.OldItems != null) foreach (object item in e.OldItems) if (item is INotifyPropertyChanged message) message.PropertyChanged -= OnMessagePropertyChanged;
        if (MessagesList.Items.Count > 0) MessagesList.ScrollIntoView(MessagesList.Items[MessagesList.Items.Count - 1]);
    }

    private void OnMessagePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Content" && MessagesList.Items.Count > 0) MessagesList.ScrollIntoView(MessagesList.Items[MessagesList.Items.Count - 1]);
    }

    private void OpenAuthenticationUrl(object sender, string authenticationUrl)
    {
        try { Process.Start(new ProcessStartInfo(authenticationUrl) { UseShellExecute = true }); }
        catch { }
    }
}
