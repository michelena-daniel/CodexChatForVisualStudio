using System;
using System.IO;
using System.Runtime.InteropServices;
using CodexForVS.Client;
using Xunit;

namespace CodexForVS.Tests;

public sealed class CodexExecutableTests
{
    [Fact]
    public void ResolvesNativeBinaryBehindNpmShimWhenPrefixIsAbsentFromPath()
    {
        DirectoryInfo prefix = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(prefix.FullName, "codex.cmd"), "unused shim");
            Assert.Throws<CodexLaunchException>(() => CodexExecutable.Resolve("", prefix.FullName, Architecture.X64));
            string binary = Path.Combine(prefix.FullName, "node_modules", "@openai", "codex", "node_modules",
                "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.WriteAllBytes(binary, Array.Empty<byte>());
            Assert.Equal(binary, CodexExecutable.Resolve("", prefix.FullName, Architecture.X64));
            string standalone = Path.Combine(prefix.FullName, "codex.exe");
            File.WriteAllBytes(standalone, Array.Empty<byte>());
            Assert.Equal(standalone, CodexExecutable.Resolve(prefix.FullName, "", Architecture.X64));
        }
        finally { prefix.Delete(recursive: true); }
    }
}
