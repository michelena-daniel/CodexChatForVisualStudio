using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CodexForVS.Client;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool> canExecute;
    private bool isExecuting;

    public AsyncCommand(Func<Task> execute, Func<bool> canExecute)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object parameter) => !isExecuting && canExecute();
    public async void Execute(object parameter) => await ExecuteAsync().ConfigureAwait(false);

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null!)) return;
        isExecuting = true;
        RaiseCanExecuteChanged();
        try { await execute(); }
        finally { isExecuting = false; RaiseCanExecuteChanged(); }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
