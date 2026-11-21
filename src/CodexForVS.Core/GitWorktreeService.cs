using System;
using System.Collections.Generic;
using System.IO;

using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexForVS.Core;

public sealed class GitWorktreeService : IWorktreeService
{
    private readonly string store;

    public GitWorktreeService() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexForVS", "sessions")) { }

    internal GitWorktreeService(string store) { this.store = Path.GetFullPath(store); }

    public Task<WorktreeSession> PrepareAsync(string solutionFile, Action<WorktreeSession> onProgress, CancellationToken cancellationToken) =>
        Task.Run(() => PrepareOnWorkerAsync(solutionFile, onProgress, cancellationToken), cancellationToken);

    private async Task<WorktreeSession> PrepareOnWorkerAsync(string solutionFile, Action<WorktreeSession> onProgress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(solutionFile) || !File.Exists(solutionFile))
            throw new InvalidOperationException("Open a saved solution to prepare a worktree.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(solutionFile))!;
        WorktreeGit git = new WorktreeGit();
        var discovery = await git.RunAsync(directory, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (discovery.Code != 0)
            throw new InvalidOperationException(discovery.Error.Contains("not a git repository")
                ? "The solution is outside a Git repository." : "Git could not read this solution's working tree.");
        string root = Path.GetFullPath(discovery.Output.TrimEnd('\r', '\n'));
        var head = await git.RunAsync(root, "rev-parse", "--verify", "--quiet", "HEAD^{commit}").ConfigureAwait(false);
        string commit = head.Output.TrimEnd('\r', '\n');
        if (head.Code != 0 || !Regex.IsMatch(commit, @"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z"))
            throw new InvalidOperationException("The repository has no readable HEAD commit. Create an initial commit before preparing a worktree.");

        await VerifyStorageLocationAsync(git).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(store);
        WorktreeSession session = new WorktreeSession(store, root, commit);
        if (Directory.Exists(session.WorktreePath) || File.Exists(session.WorktreePath))
            throw new IOException("The new session path is already occupied.");
        session.Save(WorktreeState.Preparing);
        try
        {
            onProgress(session);
            token.ThrowIfCancellationRequested();
            var add = await git.RunAsync(root, "worktree", "add", "--detach", "--no-checkout", "--", session.WorktreePath, commit).ConfigureAwait(false);
            if (add.Code != 0) throw new InvalidOperationException("Git could not register the detached worktree.");
            session.Save(WorktreeState.CheckingOut);
            onProgress(session);
            token.ThrowIfCancellationRequested();

            // Verify the new working tree before asking Git to populate its private index and files.
            var location = await git.RunAsync(session.WorktreePath, "rev-parse", "--show-toplevel", "HEAD").ConfigureAwait(false);
            string[] lines = location.Output.TrimEnd('\r', '\n').Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (location.Code != 0 || lines.Length != 2 || !string.Equals(Path.GetFullPath(lines[0]), session.WorktreePath, StringComparison.OrdinalIgnoreCase) || lines[1] != commit)
                throw new InvalidOperationException("Git returned an unexpected worktree path or base commit.");
            var branch = await git.RunAsync(session.WorktreePath, "symbolic-ref", "--quiet", "HEAD").ConfigureAwait(false);
            if (branch.Code != 1) throw new InvalidOperationException("Git could not confirm a detached HEAD.");

            List<string> checkout = await git.CheckoutOptionsAsync(session.WorktreePath).ConfigureAwait(false);
            checkout.AddRange(new[] { "read-tree", "-m", "-u", commit });
            token.ThrowIfCancellationRequested();
            var populate = await git.RunAsync(session.WorktreePath, checkout.ToArray()).ConfigureAwait(false);
            if (populate.Code != 0) throw new InvalidOperationException("Git could not check out the committed files in the new worktree.");
            token.ThrowIfCancellationRequested();
            await session.Review.CaptureAsync(token).ConfigureAwait(false);
            session.Save(WorktreeState.Ready);
            return session;
        }
        catch (OperationCanceledException)
        {
            session.Save(WorktreeState.Cancelled);
            throw;
        }
        catch
        {
            session.Save(WorktreeState.Failed);
            throw;
        }
    }

    private async Task VerifyStorageLocationAsync(WorktreeGit git)
    {
        DirectoryInfo? nearest = null;
        for (DirectoryInfo? parent = new DirectoryInfo(store); parent != null; parent = parent.Parent)
        {
            if (!parent.Exists) continue;
            nearest ??= parent;
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The extension's worktree storage path contains a filesystem link.");
        }
        if (nearest == null) throw new IOException("The worktree storage volume is unavailable.");
        // Discover from the destination side too: two path spellings can refer to the same checkout.
        var probe = await git.RunAsync(nearest.FullName, "rev-parse", "--git-dir").ConfigureAwait(false);
        if (probe.Code == 0) throw new InvalidOperationException("The extension's worktree storage is inside a Git repository.");
        if (!probe.Error.Contains("not a git repository")) throw new IOException("Git could not verify the worktree storage location.");
    }
}
