using System;
using CodexForVS.Client;

namespace CodexForVS.Extension;

internal sealed class EmptyWorkspaceContext : IWorkspaceContext
{
    public string? WorkspacePath => null;
    public string? SolutionFilePath => null;
    public event EventHandler? WorkspaceChanged { add { } remove { } }
}
