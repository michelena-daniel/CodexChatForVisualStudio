using System;
using System.Collections.ObjectModel;
using System.ComponentModel;

using System.Threading;
using System.Threading.Tasks;
using CodexForVS.Core;

namespace CodexForVS.Client;

public interface IReviewEditor
{
    void Compare(FileProposal proposal, FileComparison comparison);
    void Edit(FileProposal proposal);
    void CheckBuffers(WorktreeSession session, FileProposal proposal);
}

public sealed class FileReviewViewModel : INotifyPropertyChanged, IDisposable
{

    private WorktreeSession? session;
    private FileProposal? selected;
    private CancellationTokenSource token = new CancellationTokenSource();
    private bool running;
    public bool Busy { get; private set; }
    public string Status { get; private set; } = "";
    public ObservableCollection<FileProposal> Changes { get; } = new ObservableCollection<FileProposal>();
    public FileProposal? Selected { get => selected; set { if (selected == value) return; selected = value; Notify(); } }
    public AsyncCommand Refresh { get; }
    public AsyncCommand Preview { get; }
    public AsyncCommand Edit { get; }
    public AsyncCommand Accept { get; }
    public AsyncCommand Discard { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal FileReviewViewModel(IReviewEditor? editor, IUiDispatcher dispatcher)
    {

        bool Available() => !running && !Busy && session != null;
        bool CanReview() => Available() && selected != null && editor != null;
        Refresh = new AsyncCommand(RefreshAsync, Available);
        Preview = new AsyncCommand(() => Run(async (s, t) =>
        {
            FileProposal item = Selected!;
            FileComparison comparison = await Task.Run(() => s.Review.Preview(item), t);
            try { t.ThrowIfCancellationRequested(); editor!.Compare(item, comparison); }
            catch { item.PreviewDigest = null; throw; }
            return "Review the selected proposal before accepting.";
        }), CanReview);
        Edit = new AsyncCommand(() => Run((s, t) =>
        {
            editor!.Edit(Selected!);
            return Task.FromResult("Save your proposal edits and reopen the diff before accepting.");
        }), () => CanReview() && selected!.After != null);
        Accept = new AsyncCommand(() => Run(async (s, t) =>
        {
            FileProposal item = Selected!;
            await Task.Run(() => s.Review.AcceptAsync(item, () => dispatcher.InvokeAsync(() =>
            {
                t.ThrowIfCancellationRequested();
                editor!.CheckBuffers(s, item);
            }), t), t);
            t.ThrowIfCancellationRequested();
            Changes.Remove(item); Selected = null;
            return "Accepted " + item.Description;
        }), CanReview);
        Discard = new AsyncCommand(() => Run((s, t) =>
        {
            FileProposal item = Selected!;
            s.Review.Discard(item); Changes.Remove(item); Selected = null;
            return Task.FromResult("Discarded " + item.Description);
        }), () => Available() && selected != null);
    }

    internal void SetSession(WorktreeSession? value)
    {
        if (session == value) return;
        token.Cancel(); token.Dispose(); token = new CancellationTokenSource();
        session = value; Selected = null; Changes.Clear(); Status = ""; Notify();
    }

    internal void SetRunning(bool value) { running = value; Notify(); }

    internal Task RefreshAsync() => Run(async (s, t) =>
    {
        var changes = await Task.Run(() => s.Review.DiscoverAsync(t), t);
        t.ThrowIfCancellationRequested();
        Changes.Clear();
        foreach (FileProposal item in changes) Changes.Add(item);
        Selected = null;
        return changes.Count == 0 ? "No pending file changes." : changes.Count + " file changes to review.";
    });

    private async Task Run(Func<WorktreeSession, CancellationToken, Task<string>> operation)
    {
        WorktreeSession? active = session;
        if (Busy || running || active == null) return;
        Busy = true; Notify();
        try { string result = await operation(active, token.Token); if (session == active) Status = result; }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (session == active) Status = error.Message; }
        finally { Busy = false; Notify(); }
    }

    private void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        Refresh.RaiseCanExecuteChanged(); Preview.RaiseCanExecuteChanged(); Edit.RaiseCanExecuteChanged();
        Accept.RaiseCanExecuteChanged(); Discard.RaiseCanExecuteChanged();
    }
    public void Dispose() { token.Cancel(); token.Dispose(); }
}
