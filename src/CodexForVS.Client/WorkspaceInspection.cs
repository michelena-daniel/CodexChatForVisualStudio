using System;
using System.IO;
using System.Linq;
using System.Text;
using CodexForVS.Core;
using Newtonsoft.Json.Linq;

namespace CodexForVS.Client;

// Allows basic repository inspection even when Windows shell sandbox setup is pending.
internal static class WorkspaceInspection
{
    internal static JObject Definition() => new JObject
    {
        ["type"] = "function",
        ["name"] = "inspect_workspace",
        ["description"] = "List a workspace directory or read a portion of a text file. List . first, then follow the returned directory names to locate files. Paths are relative to the workspace or absolute within it. Offset counts characters in files and entries in directories.",
        ["inputSchema"] = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["path"] = new JObject { ["type"] = "string" },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }
            },
            ["required"] = new JArray("path"),
            ["additionalProperties"] = false
        }
    };

    internal static string Read(string root, string relative, int offset)
    {
        if (offset < 0) throw new InvalidDataException();
        string path = ResolveInspectionPath(root, relative);
        if (Directory.Exists(path))
        {
            using var directory = RegularFile.PinDirectory(path);
            return string.Join("\n", Directory.EnumerateFileSystemEntries(path)
                .Where(entry => !Path.GetFileName(entry).Equals(".git", StringComparison.OrdinalIgnoreCase))
                .Select(entry => Path.GetFileName(entry) + (Directory.Exists(entry) ? "/" : ""))
                .OrderBy(name => name, StringComparer.Ordinal).Skip(offset).Take(250));
        }
        using StreamReader reader = new StreamReader(RegularFile.OpenRead(path), Encoding.UTF8, true);
        char[] buffer = new char[16384];
        while (offset > 0)
        {
            int count = reader.ReadBlock(buffer, 0, Math.Min(offset, buffer.Length));
            if (count == 0) return "";
            offset -= count;
        }
        int read = reader.ReadBlock(buffer, 0, buffer.Length);
        if (Array.IndexOf(buffer, '\0', 0, read) >= 0) throw new InvalidDataException("Binary file.");
        return new string(buffer, 0, read);
    }

    private static string ResolveInspectionPath(string workspace, string requested)
    {
        // VS can supply a trailing separator. Win32 reports a directory handle's final
        // path without it. Preserve the separator on volume roots such as C:\.
        string root = Path.GetFullPath(workspace);
        string volume = Path.GetPathRoot(root)!;
        root = root.TrimEnd(Path.DirectorySeparatorChar);
        if (root.Length < volume.Length) root = volume;
        ReviewPaths.CheckAncestors(root);

        string path = requested.Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Specify a workspace path.");
        if (Path.IsPathRooted(path))
        {
            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return root;
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The path is outside the selected workspace.");
            path = path.Substring(prefix.Length);
        }
        // Keep '..' and other special components subject to the normal safety checks.
        string relative = string.Join("/", path.Split(Path.DirectorySeparatorChar).Where(part => part.Length != 0 && part != "."));
        return relative.Length == 0 ? root : ReviewPaths.Resolve(root, relative, allowDirectory: true);
    }
}
