using System;
using System.IO;

using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CodexForVS.Core;

internal static class ReviewPaths
{
    internal static string Resolve(string root, string relative, bool allowDirectory = false)
    {
        if (Path.IsPathRooted(relative)) throw new IOException("Expected a repository-relative file.");
        string path = root;
        CheckAncestors(root);
        foreach (string part in relative.Split('/'))
        {
            if (part.Length == 0 || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                Regex.IsMatch(part, @"^[^ .]{1,6}~[0-9]+(\.|$)") || part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(part, @"^(CON(IN\$|OUT\$)?|PRN|AUX|NUL|(COM|LPT)[0-9¹²³])(\.|$)", RegexOptions.IgnoreCase))
                throw new IOException("Unsupported or unsafe Git path: " + relative);
            path = Path.Combine(path, part);
            CheckLink(path);
            if (Directory.Exists(path) && (File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git"))))
                throw new IOException("Nested repositories require separate review: " + relative);
        }
        if (!allowDirectory && Directory.Exists(path)) throw new IOException("Expected a regular file: " + relative);
        return path;
    }

    internal static void CheckAncestors(string path)
    {
        for (DirectoryInfo? directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            CheckLink(directory.FullName);
    }

    private static void CheckLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Review stopped at a filesystem link: " + path);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    internal static string? Digest(string path)
    {
        try
        {
            using FileStream input = RegularFile.OpenRead(path);
            using SHA256 sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(input));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static void Copy(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        using FileStream source = RegularFile.OpenRead(from);
        using FileStream destination = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(destination);
        destination.Flush(true);
    }
}
