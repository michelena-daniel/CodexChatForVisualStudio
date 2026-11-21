using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CodexForVS.Core;

public interface IWorktreeService
{
    Task<WorktreeSession> PrepareAsync(string solutionFile, Action<WorktreeSession> onProgress, CancellationToken cancellationToken);
}

public enum WorktreeState { Preparing, CheckingOut, Ready, Cancelled, Failed, Implementing, ReviewReady }

public sealed class WorktreeSession
{
    internal WorktreeSession(string store, string repositoryRoot, string baseCommit)
    {
        Id = Guid.NewGuid().ToString("N");
        RepositoryRoot = repositoryRoot;
        BaseCommit = baseCommit;
        WorktreePath = Path.Combine(store, Id);
        RecordPath = Path.Combine(store, Id + ".xml");
        Review = new WorktreeReview(this);
    }

    public WorktreeReview Review { get; }
    public string Id { get; }
    public string RepositoryRoot { get; }
    public string BaseCommit { get; }
    public string WorktreePath { get; }
    public string RecordPath { get; }
    public WorktreeState State { get; private set; }

    internal void Save(WorktreeState state)
    {
        // Publish a complete record on the same volume. A crash leaves the preceding state readable.
        string temporaryPath = RecordPath + ".tmp";
        XElement record = new XElement("session", new XElement("id", Id), new XElement("repositoryRoot", RepositoryRoot),
            new XElement("baseCommit", BaseCommit), new XElement("worktreePath", WorktreePath), new XElement("state", state));
        using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            record.Save(stream);
            stream.Flush(true);
        }
        if (state == WorktreeState.Preparing) File.Move(temporaryPath, RecordPath);
        else File.Replace(temporaryPath, RecordPath, null);
        State = state;
    }
}
