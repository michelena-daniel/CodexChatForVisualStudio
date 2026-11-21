using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexForVS.Core;

public sealed class FileProposal
{
    internal FileProposal(string kind, string? before, string? after, string left, string right, string? sourceDigest)
    {
        Kind = kind; Before = before; After = after; Left = left; Right = right; SourceDigest = sourceDigest;
    }
    public string Kind { get; }
    public string? Before { get; }
    public string? After { get; }
    public string Left { get; }
    public string Right { get; }
    public string Description => Kind + "  " + (Before != After && Before != null && After != null ? Before + " → " + After : After ?? Before);
    internal string? SourceDigest { get; }
    internal string? IndexEntry { get; set; }
    internal bool StagedOnly { get; set; }
    internal string? PreviewDigest { get; set; }
    internal string Key => Description + "|" + SourceDigest + "|" + IndexEntry;
}

public sealed class WorktreeReview
{
    private readonly WorktreeSession session;
    private readonly WorktreeGit git = new WorktreeGit();
    private readonly Dictionary<string, string> baseline = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly HashSet<string> conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> decided = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<FileProposal> current = new HashSet<FileProposal>();
    private readonly string directory;
    private string BaseDirectory => Path.Combine(directory, "base");
    private string? unsupported;
    private string branch = "";
    private string registeredGitDirectory = "";
    private readonly Dictionary<string, string> baseModes = new Dictionary<string, string>(StringComparer.Ordinal);
    private async Task<Dictionary<string, string>> IndexAsync()
    {
        Dictionary<string, string> entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string entry in Paths(await Git(session.WorktreePath, "ls-files", "--stage", "-z").ConfigureAwait(false)))
        {
            int split = entry.IndexOf('\t');
            string record = entry.Substring(0, split);
            if (!(record.StartsWith("100644 ") || record.StartsWith("100755 ")) || !record.EndsWith(" 0", StringComparison.Ordinal))
                throw new IOException("Resolve unsupported or unmerged Git index entries before review.");
            entries.Add(entry.Substring(split + 1), record);
        }
        return entries;
    }
    internal WorktreeReview(WorktreeSession session)
    {
        this.session = session;
        directory = session.RecordPath + ".review";
    }

    private async Task<string> Git(string cwd, params string[] args)
    {
        var result = await git.RunAsync(cwd, args).ConfigureAwait(false);
        if (result.Code != 0) throw new IOException("Git could not verify " + args[0] + " in " + cwd);
        return result.Output;
    }

    private static string[] Paths(string value) => value.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
    private Task<string> IndexChanges(string root) => Git(root, "diff", "--cached", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z", session.BaseCommit, "--");

    internal async Task CaptureAsync(CancellationToken token)
    {
        registeredGitDirectory = Path.GetFullPath((await Git(session.WorktreePath, "rev-parse", "--absolute-git-dir").ConfigureAwait(false)).TrimEnd('\r', '\n'));
        Directory.CreateDirectory(BaseDirectory);
        branch = await Git(session.RepositoryRoot, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        conflicts.UnionWith(Paths(await IndexChanges(session.RepositoryRoot).ConfigureAwait(false)));
        conflicts.UnionWith(Paths(await Git(session.RepositoryRoot, "ls-files", "--others", "-z").ConfigureAwait(false)));
        foreach (string entry in Paths(await Git(session.WorktreePath, "ls-files", "--stage", "-z").ConfigureAwait(false)))
        {
            token.ThrowIfCancellationRequested();
            string relative = entry.Substring(entry.IndexOf('\t') + 1);
            if (!entry.StartsWith("100644 ") && !entry.StartsWith("100755 "))
            {
                unsupported = "Implementation and file review require ordinary files. Unsupported Git entry: " + relative;
                continue;
            }
            string target = ReviewPaths.Resolve(BaseDirectory, relative);
            ReviewPaths.Copy(ReviewPaths.Resolve(session.WorktreePath, relative), target);
            string digest = ReviewPaths.Digest(target)!;
            baseline.Add(relative, digest);
            baseModes.Add(relative, entry.Substring(0, 6));
            if (ReviewPaths.Digest(ReviewPaths.Resolve(session.RepositoryRoot, relative)) != digest)
                conflicts.Add(relative);
        }
    }

    internal async Task BeforeImplementationAsync(CancellationToken token)
    {
        await VerifyAsync().ConfigureAwait(false);
        conflicts.UnionWith(Paths(await IndexChanges(session.RepositoryRoot).ConfigureAwait(false)));
        conflicts.UnionWith(Paths(await Git(session.RepositoryRoot, "ls-files", "--others", "-z").ConfigureAwait(false)));
        foreach (var file in baseline)
        {
            token.ThrowIfCancellationRequested();
            if (ReviewPaths.Digest(ReviewPaths.Resolve(session.RepositoryRoot, file.Key)) != file.Value) conflicts.Add(file.Key);
        }
        // Keep the ownership record outside the writable sandbox even if the IDE exits mid-turn.
        session.Save(WorktreeState.Implementing);
    }

    public async Task VerifyAsync()
    {
        if (unsupported != null) throw new IOException(unsupported);
        ReviewPaths.CheckAncestors(session.WorktreePath);
        ReviewPaths.CheckAncestors(session.RepositoryRoot);
        ReviewPaths.CheckAncestors(directory);
        string? common = null;
        foreach (string root in new[] { session.WorktreePath, session.RepositoryRoot })
        {
            string[] identity = (await Git(root, "rev-parse", "--path-format=absolute", "--show-toplevel", "HEAD", "--absolute-git-dir", "--git-common-dir").ConfigureAwait(false))
                .TrimEnd('\r', '\n').Split('\n').Select(part => part.TrimEnd('\r')).ToArray();
            if (identity.Length != 4 || !string.Equals(Path.GetFullPath(identity[0]), root, StringComparison.OrdinalIgnoreCase) || identity[1] != session.BaseCommit)
                throw new IOException("Repository location or HEAD changed. Prepare a new worktree.");
            string admin = Path.GetFullPath(identity[2]);
            bool owned = string.Equals(admin, registeredGitDirectory, StringComparison.OrdinalIgnoreCase);
            if (owned != (root == session.WorktreePath)) throw new IOException("Git worktree registration changed.");
            string commonPath = Path.GetFullPath(identity[3]);
            if (common != null && !string.Equals(common, commonPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("The worktree belongs to a different repository.");
            common = commonPath;
        }
        if ((await Git(session.WorktreePath, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim() != "HEAD" ||
            await Git(session.RepositoryRoot, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false) != branch)
            throw new IOException("The active branch or detached worktree changed.");
    }

    public async Task<IReadOnlyList<FileProposal>> DiscoverAsync(CancellationToken token)
    {
        current.Clear();
        await VerifyAsync().ConfigureAwait(false);
        string batch = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        string after = Path.Combine(batch, "new");
        string deleted = Path.Combine(batch, "deleted");
        string modified = Path.Combine(batch, "modified");
        Directory.CreateDirectory(after);
        Directory.CreateDirectory(deleted);
        string empty = Path.Combine(batch, "empty");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] candidates = Paths(await Git(session.WorktreePath, "ls-files", "--cached", "--others", "--exclude-standard", "-z").ConfigureAwait(false));
        foreach (string path in candidates.Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            string source = ReviewPaths.Resolve(session.WorktreePath, path);
            if (!File.Exists(source)) continue;
            string? digest = ReviewPaths.Digest(source);
            if (digest == null) continue;
            bool tracked = baseline.TryGetValue(path, out string original);
            if (digest != original)
            {
                string copy = ReviewPaths.Resolve(tracked ? modified : after, path);
                ReviewPaths.Copy(source, copy);
                digest = ReviewPaths.Digest(copy)!;
            }
            hashes.Add(path, digest);
        }

        // Use Git's similarity algorithm for edited renames, including untracked destinations.
        foreach (string path in baseline.Keys.Where(path => !hashes.ContainsKey(path)))
            ReviewPaths.Copy(ReviewPaths.Resolve(BaseDirectory, path), ReviewPaths.Resolve(deleted, path));
        var compare = await git.RunAsync(batch, "-c", "core.autocrlf=false", "diff", "--no-index", "--no-ext-diff", "--no-textconv",
            "--name-status", "-z", "--find-renames", "--", "deleted", "new").ConfigureAwait(false);
        if (compare.Code != 0 && compare.Code != 1) throw new IOException("Git could not compare the review snapshots.");
        string[] fields = Paths(compare.Output);
        Dictionary<string, string> index = await IndexAsync().ConfigureAwait(false);
        HashSet<string> paired = new HashSet<string>(StringComparer.Ordinal);
        List<FileProposal> changes = new List<FileProposal>();
        void Add(string kind, string? old, string? updated)
        {
            FileProposal change = new FileProposal(kind, old, updated, old == null ? empty : ReviewPaths.Resolve(BaseDirectory, old),
                updated == null ? empty : ReviewPaths.Resolve(baseline.ContainsKey(updated) ? modified : after, updated), updated == null ? null : hashes[updated]);
            if (updated != null && index.TryGetValue(updated, out string entry)) change.IndexEntry = entry;
            if (!decided.Contains(change.Key)) { changes.Add(change); current.Add(change); }
        }
        for (int i = 0; i < fields.Length;)
        {
            string status = fields[i++];
            string first = fields[i++];
            if (!status.StartsWith("R", StringComparison.Ordinal)) continue;
            string second = fields[i++];
            if (!first.StartsWith("deleted/", StringComparison.Ordinal) || !second.StartsWith("new/", StringComparison.Ordinal)) throw new IOException("Unexpected Git rename path.");
            string old = first.Substring("deleted/".Length), updated = second.Substring("new/".Length);
            if (!baseline.ContainsKey(old) || !hashes.ContainsKey(updated)) throw new IOException("Unexpected Git rename.");
            paired.Add(old); paired.Add(updated); Add("Rename", old, updated);
        }
        HashSet<string> staged = new HashSet<string>(Paths(await IndexChanges(session.WorktreePath).ConfigureAwait(false)), StringComparer.Ordinal);
        foreach (string path in baseline.Keys.Union(hashes.Keys).Union(staged).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (paired.Contains(path)) continue;
            baseline.TryGetValue(path, out string original);
            hashes.TryGetValue(path, out string proposal);
            if (original == proposal)
            {
                if (staged.Contains(path))
                {
                    string stagedFile = empty;
                    bool hasEntry = index.TryGetValue(path, out string entry);
                    if (hasEntry)
                    {
                        string stagedDirectory = Path.Combine(batch, "index");
                        stagedFile = ReviewPaths.Resolve(stagedDirectory, path);
                        Directory.CreateDirectory(Path.GetDirectoryName(stagedFile)!);
                        List<string> options = await git.CheckoutOptionsAsync(session.WorktreePath).ConfigureAwait(false);
                        options.AddRange(new[] { "checkout-index", "--prefix=" + stagedDirectory.Replace('\\', '/') + "/", "--", path });
                        await Git(session.WorktreePath, options.ToArray()).ConfigureAwait(false);
                    }
                    FileProposal indexOnly = new FileProposal("Staged " + (hasEntry ? original == null ? "add" : "modify" : "delete"),
                        original == null ? null : path, hasEntry ? path : null, original == null ? empty : ReviewPaths.Resolve(BaseDirectory, path),
                        stagedFile, proposal)
                    { StagedOnly = true, IndexEntry = entry };
                    if (!decided.Contains(indexOnly.Key)) { current.Add(indexOnly); changes.Add(indexOnly); }
                }
                continue;
            }
            Add(original == null ? "Add" : proposal == null ? "Delete" : "Modify", original == null ? null : path, proposal == null ? null : path);
        }
        session.Save(WorktreeState.ReviewReady);
        return changes;
    }

    public FileComparison Preview(FileProposal change)
    {
        RequireCurrent(change);
        if (change.Before != null && ReviewPaths.Digest(change.Left) != baseline[change.Before]) throw new IOException("The baseline preview was changed.");
        if (change.Before == null && new FileInfo(change.Left).Length != 0 || change.After == null && new FileInfo(change.Right).Length != 0)
            throw new IOException("The empty side of the comparison was edited. Refresh the review.");
        FileComparison comparison = FileComparison.Create(change, directory);
        change.PreviewDigest = comparison.Digest;
        return comparison;
    }

    public void Discard(FileProposal change) { RequireCurrent(change); Finish(change); }
    private void Finish(FileProposal change) { decided.Add(change.Key); current.Remove(change); }
    private void RequireCurrent(FileProposal change)
    {
        if (!current.Contains(change)) throw new IOException("Refresh the review and select a current proposal.");
    }

    public async Task AcceptAsync(FileProposal change, Func<Task> checkEditor, CancellationToken token)
    {
        RequireCurrent(change);
        if (change.PreviewDigest == null) throw new IOException("Open the diff before accepting this change.");
        await checkEditor().ConfigureAwait(false);
        await VerifyAsync().ConfigureAwait(false);
        var index = await IndexAsync().ConfigureAwait(false);
        index.TryGetValue(change.After ?? change.Before!, out string entry);
        if (entry != change.IndexEntry && (change.StagedOnly || change.After != null)) throw new IOException("The proposed index changed. Refresh the review.");
        if (entry != null && change.After != null)
        {
            string expectedMode = change.Before != null ? baseModes[change.Before] : "100644";
            if (!entry.StartsWith(expectedMode, StringComparison.Ordinal)) throw new IOException("Git mode changes require manual handling on Windows.");
        }
        HashSet<string> staged = new HashSet<string>(Paths(await IndexChanges(session.RepositoryRoot).ConfigureAwait(false)), StringComparer.OrdinalIgnoreCase);
        foreach (string path in new[] { change.Before, change.After }.OfType<string>().Distinct())
        {
            string active = ReviewPaths.Resolve(session.RepositoryRoot, path);
            baseline.TryGetValue(path, out string expected);
            if (conflicts.Contains(path) || staged.Contains(path) || ReviewPaths.Digest(active) != expected)
                throw new IOException("Conflict in the active checkout: " + path);
            string? worktreeHash = ReviewPaths.Digest(ReviewPaths.Resolve(session.WorktreePath, path));
            if (worktreeHash != (change.StagedOnly || path == change.After ? change.SourceDigest : null))
                throw new IOException("The worktree changed. Refresh the review.");
        }
        if (ReviewPaths.Digest(change.Right) != change.PreviewDigest) throw new IOException("The proposal was edited. Reopen the diff before accepting.");
        await checkEditor().ConfigureAwait(false);
        await VerifyAsync().ConfigureAwait(false);
        var finalIndex = new HashSet<string>(Paths(await IndexChanges(session.RepositoryRoot).ConfigureAwait(false)), StringComparer.OrdinalIgnoreCase);
        if (new[] { change.Before, change.After }.OfType<string>().Any(finalIndex.Contains)) throw new IOException("The active Git index changed during acceptance.");
        token.ThrowIfCancellationRequested();
        string? before = change.Before == null ? null : ReviewPaths.Resolve(session.RepositoryRoot, change.Before);
        string? after = change.After == null ? null : ReviewPaths.Resolve(session.RepositoryRoot, change.After);
        if (before != after && string.Equals(before, after, StringComparison.OrdinalIgnoreCase)) throw new IOException("Case-only renames require manual review.");
        FileAcceptance.Apply(change, session.RepositoryRoot, directory, before, after, change.Before == null ? null : baseline[change.Before], token);
        Finish(change);
    }
}
