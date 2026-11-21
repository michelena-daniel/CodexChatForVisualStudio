using System;
using System.IO;

using System.Text;

namespace CodexForVS.Core;

public sealed class FileComparison
{
    internal FileComparison(string left, string right, string digest) { Left = left; Right = right; Digest = digest; }
    internal string Digest { get; }
    public string Left { get; }
    public string Right { get; }

    internal static FileComparison Create(FileProposal proposal, string directory)
    {
        string folder = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        string left = Path.Combine(folder, "before", Path.GetFileName(proposal.Left));
        string right = Path.Combine(folder, "after", Path.GetFileName(proposal.Right));
        ReviewPaths.Copy(proposal.Left, left);
        ReviewPaths.Copy(proposal.Right, right);
        string digest = ReviewPaths.Digest(right)!;
        if (IsBinary(left) || IsBinary(right))
        {
            left = Hex(left, Path.Combine(folder, "before.hex.txt"));
            right = Hex(right, Path.Combine(folder, "after.hex.txt"));
        }
        File.SetAttributes(left, File.GetAttributes(left) | FileAttributes.ReadOnly);
        File.SetAttributes(right, File.GetAttributes(right) | FileAttributes.ReadOnly);
        return new FileComparison(left, right, digest);
    }

    private static bool IsBinary(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] buffer = new byte[8192];
        int read = stream.Read(buffer, 0, buffer.Length);
        if (read >= 2 && ((buffer[0] == 0xff && buffer[1] == 0xfe) || (buffer[0] == 0xfe && buffer[1] == 0xff))) return false;
        if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0) return true;
        try { new UTF8Encoding(false, true).GetDecoder().GetCharCount(buffer, 0, read, stream.Length == read); return false; }
        catch (DecoderFallbackException) { return true; }
    }

    private static string Hex(string source, string destination)
    {
        using FileStream input = File.OpenRead(source);
        using StreamWriter output = new StreamWriter(destination, false, Encoding.ASCII);
        byte[] buffer = new byte[16];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            output.WriteLine((input.Position - read).ToString("X8") + "  " + BitConverter.ToString(buffer, 0, read).Replace('-', ' '));
        return destination;
    }
}
