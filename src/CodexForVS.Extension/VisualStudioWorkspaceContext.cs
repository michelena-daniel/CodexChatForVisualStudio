using System;
using System.IO;
using CodexForVS.Client;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CodexForVS.Extension;

internal sealed class VisualStudioWorkspaceContext : IWorkspaceContext, IVsSolutionEvents, IDisposable
{
    private readonly IVsSolution solution;
    private uint solutionEventsCookie;
    private bool disposed;

    public VisualStudioWorkspaceContext(IVsSolution solution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.solution = solution ?? throw new ArgumentNullException(nameof(solution));
        ErrorHandler.ThrowOnFailure(solution.AdviseSolutionEvents(this, out solutionEventsCookie));
        RefreshWorkspace();
    }

    public string? WorkspacePath { get; private set; }
    public string? SolutionFilePath { get; private set; }
    public event EventHandler? WorkspaceChanged;

    private void RefreshWorkspace()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ErrorHandler.ThrowOnFailure(solution.GetSolutionInfo(out string solutionDirectory, out string solutionFile, out string userOptionsFile));
        string? path = string.IsNullOrWhiteSpace(solutionFile) ? null : solutionDirectory;
        string? file = path == null ? null : Path.Combine(path, solutionFile);
        if (string.Equals(SolutionFilePath, file, StringComparison.OrdinalIgnoreCase)) return;
        SolutionFilePath = file;
        WorkspacePath = path;
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public int OnAfterOpenProject(IVsHierarchy hierarchy, int added) => VSConstants.S_OK;
    public int OnQueryCloseProject(IVsHierarchy hierarchy, int removing, ref int cancel) => VSConstants.S_OK;
    public int OnBeforeCloseProject(IVsHierarchy hierarchy, int removed) => VSConstants.S_OK;
    public int OnAfterLoadProject(IVsHierarchy stubHierarchy, IVsHierarchy realHierarchy) => VSConstants.S_OK;
    public int OnQueryUnloadProject(IVsHierarchy realHierarchy, ref int cancel) => VSConstants.S_OK;
    public int OnBeforeUnloadProject(IVsHierarchy realHierarchy, IVsHierarchy stubHierarchy) => VSConstants.S_OK;
    public int OnAfterOpenSolution(object reserved, int newSolution) { ThreadHelper.ThrowIfNotOnUIThread(); RefreshWorkspace(); return VSConstants.S_OK; }
    public int OnQueryCloseSolution(object reserved, ref int cancel) => VSConstants.S_OK;
    public int OnBeforeCloseSolution(object reserved) { ThreadHelper.ThrowIfNotOnUIThread(); WorkspacePath = SolutionFilePath = null; WorkspaceChanged?.Invoke(this, EventArgs.Empty); return VSConstants.S_OK; }
    public int OnAfterCloseSolution(object reserved) { ThreadHelper.ThrowIfNotOnUIThread(); WorkspacePath = SolutionFilePath = null; WorkspaceChanged?.Invoke(this, EventArgs.Empty); return VSConstants.S_OK; }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposed) return;
        disposed = true;
        if (solutionEventsCookie != 0) { solution.UnadviseSolutionEvents(solutionEventsCookie); solutionEventsCookie = 0; }
    }
}
