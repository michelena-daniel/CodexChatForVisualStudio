using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CodexForVS.Client;

internal static class CodexExecutable
{
    internal static string Resolve()
    {
        string npmDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        return Resolve(Environment.GetEnvironmentVariable("PATH") ?? "", npmDirectory, RuntimeInformation.OSArchitecture);
    }

    internal static string Resolve(string searchPath, string npmDirectory, Architecture architecture)
    {
        foreach (string entry in (searchPath + Path.PathSeparator + npmDirectory).Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');
            // Do not resolve relative PATH entries against the open solution.
            if (!Path.IsPathRooted(directory) || (Path.GetPathRoot(directory)?.Length ?? 0) < 3) continue;
            string executable = Path.Combine(directory, "codex.exe");
            if (File.Exists(executable)) return executable;

            if (!File.Exists(Path.Combine(directory, "codex.cmd"))) continue;
            string platform = architecture == Architecture.Arm64 ? "arm64" : "x64";
            string triple = architecture == Architecture.Arm64 ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
            string modules = Path.Combine(directory, "node_modules");
            string package = Path.Combine(modules, "@openai", "codex");
            string vendorBinary = Path.Combine("vendor", triple, "bin", "codex.exe");
            foreach (string root in new[] { Path.Combine(package, "node_modules"), modules })
            {
                executable = Path.Combine(root, "@openai", "codex-win32-" + platform, vendorBinary);
                if (File.Exists(executable)) return executable;
            }
            executable = Path.Combine(package, vendorBinary);
            if (File.Exists(executable)) return executable;
        }
        throw new CodexLaunchException("Visual Studio could not locate codex.exe on PATH or in the Codex npm installation.");
    }
}

internal sealed class CodexLaunchException : Exception
{
    internal CodexLaunchException(string message, Exception? cause = null) : base(message, cause) { }
}
