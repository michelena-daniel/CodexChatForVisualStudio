using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexForVS.Core;
using CodexForVS.Client;
using Xunit;

namespace CodexForVS.Tests;

public sealed partial class GitWorktreeTests
{
    private async Task CommitFiles(params (string Path, byte[] Value)[] files)
    {
        await InitializeAsync();
        foreach (var file in files) File.WriteAllBytes(Path.Combine(Repository, file.Path), file.Value);
        await Git(Repository, "add", ".");
        await Git(Repository, "commit", "-qm", "baseline");
    }

    private static readonly Func<Task> NoEditors = () => Task.CompletedTask;

    [Fact]
    public async Task EditedRenameBinaryAddAndDeleteAreReviewedIndependentlyAndLeaveIndexAlone()
    {
        string original = string.Join("\r\n", Enumerable.Range(0, 100).Select(i => "Original line " + i)) + "\r\n";
        await CommitFiles(("first.txt", System.Text.Encoding.UTF8.GetBytes(original)), ("remove.txt", new byte[] { 1, 2, 3 }));
        byte[] index = File.ReadAllBytes(Path.Combine(Repository, ".git", "index"));
        WorktreeSession session = await Service.PrepareAsync(Solution, _ => { }, default);
        string tree = session.WorktreePath;
        File.Move(Path.Combine(tree, "first.txt"), Path.Combine(tree, "renamed.txt"));
        File.AppendAllText(Path.Combine(tree, "renamed.txt"), "new line\r\n");
        File.Delete(Path.Combine(tree, "remove.txt"));
        byte[] binary = { 0, 255, 13, 10, 192 };
        File.WriteAllBytes(Path.Combine(tree, "icon.bin"), binary);
        var changes = await session.Review.DiscoverAsync(default);
        Assert.Equal(3, changes.Count);
        FileProposal rename = Assert.Single(changes, c => c.Kind == "Rename");
        FileProposal add = Assert.Single(changes, c => c.Kind == "Add");
        FileProposal delete = Assert.Single(changes, c => c.Kind == "Delete");
        Assert.False(File.Exists(Path.Combine(Repository, "renamed.txt")));
        FileComparison hex = session.Review.Preview(add);
        Assert.Contains("00 FF 0D 0A C0", File.ReadAllText(hex.Right));
        await session.Review.AcceptAsync(add, NoEditors, default);
        Assert.Equal(binary, File.ReadAllBytes(Path.Combine(Repository, "icon.bin")));
        session.Review.Discard(delete);
        Assert.True(File.Exists(Path.Combine(Repository, "remove.txt")));
        session.Review.Preview(rename);
        File.AppendAllText(rename.Right, "reviewer edit\r\n");
        await Assert.ThrowsAsync<IOException>(() => session.Review.AcceptAsync(rename, NoEditors, default));
        session.Review.Preview(rename);
        await session.Review.AcceptAsync(rename, NoEditors, default);
        Assert.Equal(File.ReadAllBytes(rename.Right), File.ReadAllBytes(Path.Combine(Repository, "renamed.txt")));
        Assert.False(File.Exists(Path.Combine(Repository, "first.txt")));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(Repository, ".git", "index")));
        Assert.Empty(await session.Review.DiscoverAsync(default));
    }

    private sealed class ReviewEditor : IReviewEditor, IUiDispatcher
    {
        public FileComparison? LastDiff { get; private set; }
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public void Compare(FileProposal proposal, FileComparison comparison) => LastDiff = comparison;
        public void Edit(FileProposal proposal) => File.WriteAllBytes(proposal.Right, new byte[] { 0xff, 0xfe, 65, 0, 13, 0, 10, 0 });
        public void CheckBuffers(WorktreeSession session, FileProposal proposal) { }
    }

    [Fact]
    public async Task ReviewCommandsRequireLatestSavedPreviewAndApplyOnlyTheSelectedFile()
    {
        await CommitFiles(("code.txt", new byte[] { 65, 66 }), ("other.txt", new byte[] { 67 }));
        WorktreeSession session = await Service.PrepareAsync(Solution, _ => { }, default);
        File.WriteAllText(Path.Combine(session.WorktreePath, "code.txt"), "generated");
        File.Delete(Path.Combine(session.WorktreePath, "other.txt"));
        ReviewEditor editor = new ReviewEditor();
        using FileReviewViewModel review = new FileReviewViewModel(editor, editor);
        review.SetSession(session);
        await review.Refresh.ExecuteAsync();
        review.Selected = review.Changes.Single(item => item.After == "code.txt");
        await review.Accept.ExecuteAsync();
        Assert.Contains("Open the diff", review.Status);
        await review.Preview.ExecuteAsync();
        Assert.Equal("generated", File.ReadAllText(editor.LastDiff!.Right));
        await review.Edit.ExecuteAsync();
        await review.Accept.ExecuteAsync();
        Assert.Contains("Reopen the diff", review.Status);
        await review.Preview.ExecuteAsync();
        byte[] bytes = File.ReadAllBytes(editor.LastDiff!.Right);
        await review.Accept.ExecuteAsync();
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Repository, "code.txt")));
        Assert.True(File.Exists(Path.Combine(Repository, "other.txt")));
        review.Selected = Assert.Single(review.Changes);
        await review.Preview.ExecuteAsync();
        await review.Accept.ExecuteAsync();
        Assert.False(File.Exists(Path.Combine(Repository, "other.txt")));
        Assert.Empty(review.Changes);
    }

    [Theory]
    [InlineData("dirty-before")]
    [InlineData("staged-before")]
    [InlineData("dirty-after")]
    [InlineData("staged-after")]
    [InlineData("unsaved-editor")]
    [InlineData("proposal-changed")]
    [InlineData("switched-branch")]
    public async Task RefusesConflictingAcceptanceWithoutChangingSourceOrIndex(string conflict)
    {
        byte[] original = { 1, 2, 3 };
        await CommitFiles(("code.bin", original));
        string active = Path.Combine(Repository, "code.bin");
        if (conflict == "dirty-before" || conflict == "staged-before") File.WriteAllText(active, "my edit");
        if (conflict == "staged-before") { await Git(Repository, "add", "code.bin"); File.WriteAllBytes(active, original); }
        WorktreeSession session = await Service.PrepareAsync(Solution, _ => { }, default);
        string source = Path.Combine(session.WorktreePath, "code.bin");
        File.WriteAllBytes(source, new byte[] { 2, 0, 255 });
        FileProposal item = Assert.Single(await session.Review.DiscoverAsync(default));
        session.Review.Preview(item);
        if (conflict == "dirty-after" || conflict == "staged-after") File.WriteAllText(active, "another edit");
        if (conflict == "staged-after") { await Git(Repository, "add", "code.bin"); File.WriteAllBytes(active, original); }
        if (conflict == "proposal-changed") File.Delete(source);
        if (conflict == "switched-branch") await Git(Repository, "switch", "-qc", "different");
        byte[] expected = File.ReadAllBytes(active);
        byte[] index = File.ReadAllBytes(Path.Combine(Repository, ".git", "index"));
        await Assert.ThrowsAsync<IOException>(() => session.Review.AcceptAsync(item,
            conflict == "unsaved-editor" ? () => throw new IOException("Open editor") : NoEditors, default));
        Assert.Equal(expected, File.ReadAllBytes(active));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(Repository, ".git", "index")));
    }

    [Fact]
    public async Task FailedRenameRemovesOnlyItsNewDestinationAndPreservesTheOriginal()
    {
        byte[] bytes = { 2, 3, 4 };
        await CommitFiles(("from.bin", bytes));
        WorktreeSession session = await Service.PrepareAsync(Solution, _ => { }, default);
        File.Move(Path.Combine(session.WorktreePath, "from.bin"), Path.Combine(session.WorktreePath, "to.bin"));
        FileProposal rename = Assert.Single(await session.Review.DiscoverAsync(default));
        Assert.Equal("Rename", rename.Kind);
        session.Review.Preview(rename);
        string from = Path.Combine(Repository, "from.bin");
        using FileStream locked = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAnyAsync<IOException>(() => session.Review.AcceptAsync(rename, NoEditors, default));
        Assert.Equal(bytes, File.ReadAllBytes(from));
        Assert.False(File.Exists(Path.Combine(Repository, "to.bin")));
        Assert.Empty(Directory.GetFiles(Repository, ".codex-*"));
    }

    [Fact]
    public async Task StagedOnlyFileIsPreviewedAndAcceptedAsBytes()
    {
        await CommitFiles();
        WorktreeSession session = await Service.PrepareAsync(Solution, _ => { }, default);
        string path = Path.Combine(session.WorktreePath, "staged.bin");
        byte[] bytes = { 0, 0xff, 0x80, 10 };
        File.WriteAllBytes(path, bytes);
        await Git(session.WorktreePath, "add", "--", "staged.bin");
        File.Delete(path);
        FileProposal item = Assert.Single(await session.Review.DiscoverAsync(default));
        Assert.Equal("Staged add", item.Kind);
        Assert.Equal(bytes, File.ReadAllBytes(item.Right));
        session.Review.Preview(item);
        await session.Review.AcceptAsync(item, NoEditors, default);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Repository, "staged.bin")));
    }

    [Fact]
    public async Task ActiveUntrackedCollisionAndCompetingDestinationStopAddition()
    {
        await CommitFiles();
        string active = Path.Combine(Repository, "local.txt");
        File.WriteAllText(active, "original local file");
        WorktreeSession session = await Service.PrepareAsync(Solution, _ => { }, default);
        File.Delete(active);
        File.WriteAllText(Path.Combine(session.WorktreePath, "local.txt"), "candidate");
        FileProposal item = Assert.Single(await session.Review.DiscoverAsync(default));
        session.Review.Preview(item);
        await Assert.ThrowsAsync<IOException>(() => session.Review.AcceptAsync(item, NoEditors, default));
        Assert.False(File.Exists(active));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFileName,string existingFileName,IntPtr security);

    [Fact]
    public async Task HardLinkedFileCannotExposeAnOutsideFileDuringInspectionOrReview()
    {
        await CommitFiles();
        WorktreeSession session = await Service.PrepareAsync(Solution,_=>{},default);
        string outside = Path.Combine(fixture,"outside.txt");
        File.WriteAllText(outside,"private data");
        Assert.True(CreateHardLink(Path.Combine(session.WorktreePath,"linked.txt"),outside,IntPtr.Zero));
        Assert.Throws<IOException>(()=>WorkspaceInspection.Read(session.WorktreePath,"linked.txt",0));
        await Assert.ThrowsAsync<IOException>(()=>session.Review.DiscoverAsync(default));
        Assert.Equal("private data",File.ReadAllText(outside));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData(".git/config")]
    [InlineData("dir/.Git/HEAD")]
    [InlineData("file.txt:hidden")]
    [InlineData("aux.txt")]
    [InlineData("GIT~1/config")]
    public void ReviewRejectsUnsafeGitPaths(string relative)
    {
        Assert.Throws<IOException>(() => ReviewPaths.Resolve(Repository, relative));
    }
}
