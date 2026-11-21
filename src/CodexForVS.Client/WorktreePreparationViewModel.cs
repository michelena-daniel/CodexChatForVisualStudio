using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CodexForVS.Core;

namespace CodexForVS.Client;

public sealed class WorktreePreparationViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IWorktreeService service;
    private readonly IUiDispatcher dispatcher;
    private string? solutionFile;
    private CancellationTokenSource? operation;
    public WorktreeSession? Session { get; private set; }
    private bool disposed;

    internal WorktreePreparationViewModel(IWorktreeService service, IUiDispatcher dispatcher)
    {
        this.service = service;
        this.dispatcher = dispatcher;
        PrepareCommand = new AsyncCommand(PrepareAsync, () => !disposed && solutionFile != null && operation == null && Session == null);
    }

    public AsyncCommand PrepareCommand { get; }
    public string Status { get; private set; } = "Open a saved solution to prepare a worktree.";
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetSolution(string? file)
    {
        if (string.Equals(file, solutionFile, StringComparison.OrdinalIgnoreCase)) return;
        operation?.Cancel();
        operation = null;
        solutionFile = file;
        Session = null;
        SetStatus(file == null ? "Open a saved solution to prepare a worktree." : string.Empty);
    }

    private async Task PrepareAsync()
    {
        using CancellationTokenSource request = new CancellationTokenSource();
        operation = request;
        string? recordPath = null;
        SetStatus("Preparing a detached Git worktree…");
        try
        {
            WorktreeSession session = await service.PrepareAsync(solutionFile!, progress => recordPath = progress.RecordPath, request.Token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                if (operation != request) return;
                Session = session;
                SetStatus($"Prepared from commit {session.BaseCommit}\n{session.WorktreePath}\nUncommitted changes and unsaved editor buffers stay in the active solution.");
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (operation == request) SetStatus(error.Message + (recordPath == null ? "" : "\nSession record: " + recordPath));
            }).ConfigureAwait(false);
        }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (operation == request) operation = null;
                PrepareCommand.RaiseCanExecuteChanged();
            }).ConfigureAwait(false);
        }
    }

    private void SetStatus(string message)
    {
        Status = message;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        PrepareCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        disposed = true;
        SetSolution(null);
    }
}
