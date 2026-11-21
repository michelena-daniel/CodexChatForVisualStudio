using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;

namespace CodexForVS.Extension;

internal sealed class WorktreeCommandDialog : DialogWindow
{
    internal WorktreeCommandDialog(string directory, string command)
    {
        Title = "Run this worktree command?";
        Width = 620; Height = 360; MinWidth = 340; MinHeight = 220;
        DockPanel layout = new DockPanel { Margin = new Thickness(12) };
        TextBlock workspace = new TextBlock { Text = directory, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(workspace, Dock.Top);
        layout.Children.Add(workspace);
        StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button allow = new Button { Content = "Run once", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 6, 0) };
        Button decline = new Button { Content = "Decline", IsCancel = true, IsDefault = true, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 0, 0) };
        allow.Click += (_, __) => DialogResult = true;
        actions.Children.Add(allow); actions.Children.Add(decline);
        DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions);
        layout.Children.Add(new TextBox
        {
            Text = command,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        Content = layout;
    }
}
