using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace CodexForVS.Core;

internal sealed class WorktreeGit
{
    private readonly string executable;

    internal WorktreeGit()
    {
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd");
        string[] paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        executable = new[] { installed }.Concat(paths.Select(path => path.Trim('"')))
            .Where(path => Path.IsPathRooted(path) && (Path.GetPathRoot(path)?.Length ?? 0) >= 3)
            .Select(path => Path.Combine(path, "git.exe")).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("Install Git for Windows or add git.exe to PATH.");
    }

    internal async Task<(int Code, string Output, string Error)> RunAsync(string directory, params string[] arguments)
    {
        string[] options = { "--no-optional-locks", "-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false",
            "-c", "submodule.recurse=false", "-c", "gc.auto=0" };
        ProcessStartInfo start = new ProcessStartInfo(executable, string.Join(" ", options.Concat(arguments).Select(Quote)))
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        // An IDE launched from a Git hook or terminal can inherit a different index, worktree or Git directory.
        foreach (string key in start.EnvironmentVariables.Keys.Cast<string>().Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.EnvironmentVariables.Remove(key);
        start.EnvironmentVariables["LC_ALL"] = "C";
        start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        start.EnvironmentVariables["GIT_ALLOW_PROTOCOL"] = "";
        start.EnvironmentVariables["GIT_NO_LAZY_FETCH"] = "1";
        if (arguments.Contains("--no-index"))
        {
            start.EnvironmentVariables["GIT_CONFIG_NOSYSTEM"] = "1";
            start.EnvironmentVariables["GIT_CONFIG_GLOBAL"] = "NUL";
            start.EnvironmentVariables["GIT_ATTR_NOSYSTEM"] = "1";
        }
        using Process process = new Process { StartInfo = start };
        process.Start();
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        // Called from the preparation worker. Let Git finish its own bookkeeping on cancellation.
        process.WaitForExit();
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    internal async Task<List<string>> CheckoutOptionsAsync(string directory)
    {
        var filters = await RunAsync(directory, "config", "--null", "--name-only", "--get-regexp", @"^filter\..*\.(clean|smudge|process|required)$").ConfigureAwait(false);
        if (filters.Code != 0 && filters.Code != 1) throw new IOException("Git could not inspect checkout filters.");
        List<string> options = new List<string> { "-c","core.sparseCheckout=false" };
        foreach (string key in filters.Output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
        {
            options.Add("-c");
            options.Add(key + (key.EndsWith(".required",StringComparison.Ordinal) ? "=false" : "="));
        }
        return options;
    }

    // Windows CommandLineToArgvW quoting for netstandard2.0, which has no ProcessStartInfo.ArgumentList.
    private static string Quote(string argument)
    {
        StringBuilder result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
