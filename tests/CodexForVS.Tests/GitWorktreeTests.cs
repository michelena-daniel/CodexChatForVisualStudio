using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CodexForVS.Core;
using Xunit;

namespace CodexForVS.Tests;

public sealed partial class GitWorktreeTests : IDisposable
{
    private readonly string fixture = Directory.CreateTempSubdirectory("CodexForVS-worktree-").FullName;
    private string Repository => Path.Combine(fixture, "active checkout");
    private string Solution => Path.Combine(Repository, "Example.sln");
    private string Store => Path.Combine(fixture, "owned sessions");
    private GitWorktreeService Service => new GitWorktreeService(Store);

    [Fact]
    public async Task NestedSolutionGetsDetachedCommittedFilesAndPreservesDirtyCheckout()
    {
        await InitializeAsync();
        string nested = Directory.CreateDirectory(Path.Combine(Repository, "nested & saved")).FullName;
        string source = Path.Combine(nested, "hello.txt");
        File.WriteAllText(source, "committed");
        File.WriteAllText(Path.Combine(nested, "Example.sln"), "");
        await Git(Repository, "add", ".");
        await Git(Repository, "commit", "-qm", "base");
        string head = await Git(Repository, "rev-parse", "HEAD");
        string branch = await Git(Repository, "symbolic-ref", "HEAD");
        File.WriteAllText(source, "staged");
        await Git(Repository, "add", ".");
        File.WriteAllText(source, "unstaged");
        string untracked = Path.Combine(nested, "scratch.bin");
        byte[] scratch = { 0xff, 0, 0x0d, 0x0a };
        File.WriteAllBytes(untracked, scratch);
        byte[] index = File.ReadAllBytes(Path.Combine(Repository, ".git", "index"));
        string status = await Git(Repository, "--no-optional-locks", "status", "--porcelain", "-z");

        WorktreeSession session = await Service.PrepareAsync(Path.Combine(nested, "Example.sln"), _ => { }, CancellationToken.None);

        Assert.Equal(WorktreeState.Ready, session.State);
        Assert.Equal(Repository, session.RepositoryRoot, ignoreCase: true);
        Assert.Equal(head, session.BaseCommit);
        Assert.Equal(Store, Path.GetDirectoryName(session.WorktreePath));
        Assert.Equal(head, await Git(session.WorktreePath, "rev-parse", "HEAD"));
        Assert.Equal("HEAD", await Git(session.WorktreePath, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("committed", File.ReadAllText(Path.Combine(session.WorktreePath, "nested & saved", "hello.txt")));
        Assert.False(File.Exists(Path.Combine(session.WorktreePath, "nested & saved", "scratch.bin")));
        Assert.Equal(branch, await Git(Repository, "symbolic-ref", "HEAD"));
        Assert.Equal(head, await Git(Repository, "rev-parse", "HEAD"));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(Repository, ".git", "index")));
        Assert.Equal(status, await Git(Repository, "--no-optional-locks", "status", "--porcelain", "-z"));
        Assert.Equal("staged", await Git(Repository, "show", ":nested & saved/hello.txt"));
        Assert.Equal("unstaged", File.ReadAllText(source));
        Assert.Equal(scratch, File.ReadAllBytes(untracked));
        AssertRecord(session, WorktreeState.Ready);
    }

    [Fact]
    public async Task MissingSolutionRepositoryAndCommitGiveDistinctReasonsWithoutAllocatingStorage()
    {
        Assert.Contains("saved solution", (await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.PrepareAsync("", _ => { }, default))).Message);
        string standalone = Path.Combine(fixture, "standalone.sln");
        Assert.Contains("saved solution", (await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.PrepareAsync(standalone, _ => { }, default))).Message);
        File.WriteAllText(standalone, "");
        Assert.Contains("outside a Git repository", (await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.PrepareAsync(standalone, _ => { }, default))).Message);
        await InitializeAsync();
        Assert.Contains("initial commit", (await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.PrepareAsync(Solution, _ => { }, default))).Message);
        Assert.False(Directory.Exists(Store));
    }

    [Fact]
    public async Task CancelAfterRegistrationKeepsTheOwnedPathAndDurableCancelledRecord()
    {
        await InitializeAsync();
        await Git(Repository, "commit", "--allow-empty", "-qm", "base");
        using CancellationTokenSource cancel = new CancellationTokenSource();
        WorktreeSession? registered = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.PrepareAsync(Solution, session =>
        {
            AssertRecord(session, session.State);
            if (session.State == WorktreeState.CheckingOut)
            {
                registered = session;
                cancel.Cancel();
            }
        }, cancel.Token));
        Assert.NotNull(registered);
        AssertRecord(registered!, WorktreeState.Cancelled);
        Assert.True(File.Exists(Path.Combine(registered.WorktreePath, ".git")));
        Assert.Contains(registered.Id, await Git(Repository, "worktree", "list", "--porcelain"));
    }

    [Fact]
    public async Task CheckoutConflictRecordsFailureAndRetainsTheUnexpectedFile()
    {
        await InitializeAsync();
        File.WriteAllText(Path.Combine(Repository, "sample.txt"), "from commit");
        await Git(Repository, "add", ".");
        await Git(Repository, "commit", "-qm", "base");
        WorktreeSession? partial = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.PrepareAsync(Solution, session =>
        {
            if (session.State != WorktreeState.CheckingOut) return;
            partial = session;
            File.WriteAllText(Path.Combine(session.WorktreePath, "sample.txt"), "keep this");
        }, default));
        Assert.NotNull(partial);
        AssertRecord(partial!, WorktreeState.Failed);
        Assert.Equal("keep this", File.ReadAllText(Path.Combine(partial.WorktreePath, "sample.txt")));
        Assert.Equal("from commit", File.ReadAllText(Path.Combine(Repository, "sample.txt")));
    }

    [Fact]
    public async Task RejectsStorageInsideActiveCheckout()
    {
        await InitializeAsync();
        await Git(Repository, "commit", "--allow-empty", "-qm", "base");
        string unsafeStore = Path.Combine(Repository, "would be tracked");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GitWorktreeService(unsafeStore).PrepareAsync(Solution, _ => { }, default));
        Assert.False(Directory.Exists(unsafeStore));
    }

    [Fact]
    public async Task CheckoutUsesStoredContentsAndDoesNotRunHooksOrExternalSmudgeFilter()
    {
        await InitializeAsync();
        File.WriteAllText(Path.Combine(Repository, ".gitattributes"), "*.txt filter=fixture\n");
        File.WriteAllText(Path.Combine(Repository, "value.txt"), "original");
        await Git(Repository, "add", ".");
        await Git(Repository, "commit", "-qm", "base");
        await Git(Repository, "config", "filter.fixture.smudge", "sed s/original/transformed/");
        await Git(Repository, "config", "filter.fixture.required", "true");
        File.WriteAllText(Path.Combine(Repository, ".git", "hooks", "post-checkout"), "#!/bin/sh\ntouch hook-ran\n");
        WorktreeSession session = await Service.PrepareAsync(Solution, _ => { }, default);
        Assert.Equal("original", File.ReadAllText(Path.Combine(session.WorktreePath, "value.txt")));
        Assert.False(File.Exists(Path.Combine(session.WorktreePath, "hook-ran")));
    }

    private static void AssertRecord(WorktreeSession session, WorktreeState state)
    {
        XElement record = XElement.Load(session.RecordPath);
        Assert.Equal(session.Id, (string?)record.Element("id"));
        Assert.Equal(session.WorktreePath, (string?)record.Element("worktreePath"));
        Assert.Equal(session.BaseCommit, (string?)record.Element("baseCommit"));
        Assert.Equal(session.RepositoryRoot, (string?)record.Element("repositoryRoot"));
        Assert.Equal(state.ToString(), (string?)record.Element("state"));
    }

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(Repository);
        File.WriteAllText(Solution, "");
        await Git(Repository, "init", "-q");
        await Git(Repository, "config", "user.name", "Fixture");
        await Git(Repository, "config", "user.email", "fixture@example.invalid");
        await Git(Repository, "config", "commit.gpgSign", "false");
    }

    private static async Task<string> Git(string directory, params string[] args)
    {
        ProcessStartInfo start = new ProcessStartInfo("git.exe")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(process.ExitCode == 0, await error);
        return (await output).TrimEnd('\r', '\n');
    }

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(fixture, recursive: true);
    }
}
