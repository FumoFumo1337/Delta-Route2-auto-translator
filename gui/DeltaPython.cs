using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

// Finding a usable Python interpreter is the one piece of plumbing the window
// and the command line genuinely share, so it lives on its own instead of being
// written twice.
internal static class DeltaPython
{
    internal static readonly Version MinimumVersion = new Version(3, 10);

    internal sealed class PythonRuntime
    {
        public string Executable;
        public Version Version;
        public string Source;
    }

    internal sealed class PythonCandidate
    {
        public string Executable;
        public string ArgumentsPrefix;
        public string Source;

        public PythonCandidate(string executable, string argumentsPrefix, string source)
        {
            Executable = executable;
            ArgumentsPrefix = argumentsPrefix;
            Source = source;
        }
    }

    internal static PythonRuntime Detect()
    {
        List<PythonCandidate> candidates = new List<PythonCandidate>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string configured = Environment.GetEnvironmentVariable("PYTHON");
        if (!string.IsNullOrWhiteSpace(configured))
            AddPythonCandidate(candidates, seen, ResolveCommand(configured.Trim()), string.Empty, "PYTHON environment variable");

        AddPythonCandidate(candidates, seen, FindOnPath("python.exe"), string.Empty, "system PATH");
        AddPythonCandidate(candidates, seen, FindOnPath("python3.exe"), string.Empty, "system PATH");
        AddPythonCandidate(candidates, seen, FindOnPath("py.exe"), "-3", "Windows Python launcher");

        string home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            AddPythonInstallRoot(candidates, seen, Path.Combine(home, "AppData", "Local", "Programs", "Python"), "HOME");
            AddPythonInstallRoot(candidates, seen, Path.Combine(home, ".pyenv", "pyenv-win", "versions"), "HOME/.pyenv");
            AddPythonCandidate(candidates, seen, Path.Combine(home, "scoop", "apps", "python", "current", "python.exe"), string.Empty, "HOME/Scoop");
        }

        string localPrograms = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localPrograms))
            AddPythonInstallRoot(candidates, seen, Path.Combine(localPrograms, "Programs", "Python"), "user installation");
        AddPythonInstallRoot(candidates, seen, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python"), "system installation");
        AddPythonInstallRoot(candidates, seen, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Python"), "system installation");

        PythonRuntime bestCompatible = null;
        PythonRuntime bestFound = null;
        foreach (PythonCandidate candidate in candidates)
        {
            PythonRuntime runtime = InspectPython(candidate);
            if (runtime == null)
                continue;
            if (bestFound == null || runtime.Version.CompareTo(bestFound.Version) > 0)
                bestFound = runtime;
            if (runtime.Version.CompareTo(MinimumVersion) >= 0 &&
                (bestCompatible == null || runtime.Version.CompareTo(bestCompatible.Version) > 0))
                bestCompatible = runtime;
        }
        return bestCompatible ?? bestFound;
    }

    private static void AddPythonInstallRoot(
        List<PythonCandidate> candidates,
        HashSet<string> seen,
        string root,
        string source)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;
        AddPythonCandidate(candidates, seen, Path.Combine(root, "python.exe"), string.Empty, source);
        try
        {
            foreach (string directory in Directory.GetDirectories(root).OrderByDescending(path => path))
                AddPythonCandidate(candidates, seen, Path.Combine(directory, "python.exe"), string.Empty, source);
        }
        catch
        {
        }
    }

    private static void AddPythonCandidate(
        List<PythonCandidate> candidates,
        HashSet<string> seen,
        string executable,
        string argumentsPrefix,
        string source)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            return;
        string fullPath;
        try { fullPath = Path.GetFullPath(executable); }
        catch { return; }
        string key = fullPath + "|" + argumentsPrefix;
        if (seen.Add(key))
            candidates.Add(new PythonCandidate(fullPath, argumentsPrefix, source));
    }

    private static string ResolveCommand(string value)
    {
        if (File.Exists(value))
            return value;
        return FindOnPath(value);
    }

    internal static string FindOnPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string rawDirectory in path.Split(Path.PathSeparator))
        {
            string directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
                continue;
            try
            {
                string candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate) && candidate.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) < 0)
                    return candidate;
            }
            catch
            {
            }
        }
        return null;
    }

    private static PythonRuntime InspectPython(PythonCandidate candidate)
    {
        string probe = "-X utf8 -c \"import sys; print(sys.executable); print('.'.join(str(value) for value in sys.version_info[:3]))\"";
        ProcessStartInfo start = new ProcessStartInfo
        {
            FileName = candidate.Executable,
            Arguments = (candidate.ArgumentsPrefix.Length > 0 ? candidate.ArgumentsPrefix + " " : string.Empty) + probe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        try
        {
            // Read asynchronously: a synchronous ReadToEnd blocks until the child
            // closes the pipe, so the timeout below could never fire and a wedged
            // interpreter froze detection - which runs on the UI thread at startup.
            using (Process process = new Process { StartInfo = start })
            {
                StringBuilder collected = new StringBuilder();
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data != null)
                        lock (collected) collected.AppendLine(args.Data);
                };
                process.ErrorDataReceived += delegate { };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(); } catch { }
                    return null;
                }
                // The parameterless overload is what flushes the async handlers.
                process.WaitForExit();
                if (process.ExitCode != 0)
                    return null;
                string output;
                lock (collected) output = collected.ToString();
                string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2)
                    return null;
                Version version;
                if (!Version.TryParse(lines[lines.Length - 1].Trim(), out version))
                    return null;
                string resolved = lines[0].Trim();
                if (!File.Exists(resolved))
                    resolved = candidate.Executable;
                return new PythonRuntime
                {
                    Executable = resolved,
                    Version = version,
                    Source = candidate.Source
                };
            }
        }
        catch
        {
            return null;
        }
    }

    // Both front ends run the scripts the same way: UTF-8 in and out, unbuffered
    // so the log fills as the run goes rather than all at once, and the tools
    // folder as the working directory. The DeepL key is not handled here - the
    // command line inherits it from the environment and the window writes it to
    // the child's stdin.
    internal static ProcessStartInfo Start(
        string executable, string arguments, string workingDirectory, bool redirect)
    {
        ProcessStartInfo start = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "-X utf8 -u " + arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect
        };
        if (redirect)
        {
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
        }
        // Command-line switches avoid materialising ProcessStartInfo's legacy
        // environment dictionary. Some hosts legally provide both Path and
        // PATH; .NET Framework throws while copying that block even though
        // CreateProcess and Python accept it.
        return start;
    }
}
