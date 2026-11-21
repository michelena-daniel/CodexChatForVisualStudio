using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace CodexForVS.Core;

internal sealed class PublicationDirectories : IDisposable
{
    private readonly Dictionary<string,SafeFileHandle> handles = new Dictionary<string,SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
    private readonly string root;

    internal PublicationDirectories(string root)
    {
        this.root = root;
        handles.Add(root,RegularFile.PinDirectory(root));
    }

    internal void PinParent(string? path,bool create)
    {
        if (path == null) return;
        string parent = Path.GetDirectoryName(path)!;
        if (handles.ContainsKey(parent)) return;
        if (!parent.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new IOException("Publication path is outside the active checkout.");
        PinParent(parent,create);
        if (create && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
        handles.Add(parent,RegularFile.PinDirectory(parent));
    }

    public void Dispose()
    {
        foreach (SafeFileHandle directory in handles.Values) directory.Dispose();
    }
}
