using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

[assembly: AssemblyTitle("delta")]
[assembly: AssemblyDescription("Command line for the Delta/Route2 translation toolset.")]

// The command line half of the toolset. It rebuilds the binaries and drives
// the same pipeline steps as the window, through the same DeltaProject layout
// and the same detected interpreter.
internal static class DeltaCliProgram
{
    private static string toolsDirectory;

    private static int Main(string[] rawArguments)
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch { }
        toolsDirectory = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')).FullName;

        if (rawArguments.Length == 4 && rawArguments[0] == "--complete-build")
            return CompleteDeferredBuild(rawArguments[1], rawArguments[2], rawArguments[3]);

        List<string> arguments = new List<string>(rawArguments);
        if (arguments.Count == 0 || IsHelp(arguments[0]))
        {
            Usage();
            return arguments.Count == 0 ? 2 : 0;
        }

        string command = arguments[0].ToLowerInvariant();
        arguments.RemoveAt(0);
        try
        {
            switch (command)
            {
                case "build": return Build(arguments);
                case "extract": return Extract(arguments);
                case "translate": return Translate(arguments, false);
                case "estimate": return Translate(arguments, true);
                case "proofread": return Proofread(arguments);
                case "overlay": return Overlay(arguments);
                case "menu": return Menu(arguments);
                case "pipeline": return Pipeline(arguments);
                case "archive": return Resource(arguments);
                case "run": return Run(arguments);
                case "python": return ReportPython(arguments);
                default:
                    Console.Error.WriteLine("Unknown command: " + command);
                    Usage();
                    return 2;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("error: " + DeltaProject.Describe(error));
            return 1;
        }
    }

    private static bool IsHelp(string value)
    {
        return value == "-h" || value == "--help" || value == "/?" ||
            string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);
    }

    private static void Usage()
    {
        Console.WriteLine(@"delta - Delta/Route2 translation toolset

  delta build [--proxy]
        Rebuild bin\DeltaTranslator.exe and bin\delta.exe. --proxy also builds
        the runtime hook bin\winmm.dll, which needs Visual Studio.

  delta extract    --game DIR
  delta translate  --game DIR [--lang RU] [--overwrite] [--max-dialogs N]
                   [--loop-retries N]
  delta estimate   --game DIR [--lang RU] [--max-dialogs N]
  delta proofread  --game DIR [--lang RU]
  delta overlay    --game DIR [--lang RU] [--strict-fit]
  delta menu       --game DIR             both languages, --lang is not used
  delta pipeline   --game DIR [--lang RU] [--overwrite]
        Pipeline steps. pipeline runs extract, translate, proofread and overlay
        in order. overlay reports how much of the workbook is translated and
        measures every line against the message window, listing the ones that
        overflow in work\<game>\delta_overlay.<lang>.fit.tsv. --strict-fit turns
        that warning into a failure and writes no overlay.

  delta archive ...            CGF/IAF/MHU operations through the C# backend.
  delta run SCRIPT ...         Any script in py\, with the detected Python.
  delta python                 Report the interpreter that would be used.

Shared options
  --game DIR      The game folder. Defaults to the current directory.
  --exe FILE      Game executable. Defaults to RSA.EXE in the game folder.
  --source FILE   Script archive. Defaults to RSAN.SD in the game folder.
  --lang RU|EN    Target language. Defaults to RU.
  --generic       Skip the Reika-specific name and script rules.
  --tools DIR     The delta_translation folder. Defaults to the one holding
                  this executable.

The DeepL key comes from the DEEPL_API_KEY environment variable. Installing the
proxy and the launcher into a game stays in DeltaTranslator.exe.");
    }

    // ---- option parsing -------------------------------------------------

    private sealed class Options
    {
        public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string Value(string name, string fallback)
        {
            string result;
            return Values.TryGetValue(name, out result) ? result : fallback;
        }

        public bool Flag(string name) { return Flags.Contains(name); }
    }

    private static readonly HashSet<string> ValueOptions = new HashSet<string>(
        new[] { "game", "exe", "source", "lang", "tools", "garbro", "max-dialogs", "loop-retries" },
        StringComparer.OrdinalIgnoreCase);

    // Flags are checked against this list rather than accepted blindly, so a
    // misspelled --overwite fails instead of quietly translating nothing.
    private static readonly HashSet<string> FlagOptions = new HashSet<string>(
        new[] { "proxy", "generic", "overwrite", "strict-fit" },
        StringComparer.OrdinalIgnoreCase);

    private static Options Parse(List<string> arguments)
    {
        Options options = new Options();
        for (int index = 0; index < arguments.Count; index++)
        {
            string item = arguments[index];
            if (!item.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Unexpected argument: " + item);
            string name = item.Substring(2);
            string inline = null;
            int equals = name.IndexOf('=');
            if (equals >= 0)
            {
                inline = name.Substring(equals + 1);
                name = name.Substring(0, equals);
            }
            if (ValueOptions.Contains(name))
            {
                if (inline == null)
                {
                    if (index + 1 >= arguments.Count)
                        throw new ArgumentException("--" + name + " needs a value");
                    inline = arguments[++index];
                }
                options.Values[name] = inline;
            }
            else
            {
                if (!FlagOptions.Contains(name))
                    throw new ArgumentException("Unknown option: --" + name);
                if (inline != null)
                    throw new ArgumentException("--" + name + " does not take a value");
                options.Flags.Add(name);
            }
        }
        if (options.Values.ContainsKey("tools"))
            toolsDirectory = Path.GetFullPath(options.Values["tools"]);
        return options;
    }

    private static DeltaProject Project(Options options)
    {
        string game = Path.GetFullPath(options.Value("game", Directory.GetCurrentDirectory()));
        string executable = options.Value("exe", Path.Combine(game, "RSA.EXE"));
        string source = options.Value("source", Path.Combine(game, "RSAN.SD"));
        string language = options.Value("lang", "RU").ToUpperInvariant();
        if (language != "RU" && language != "EN")
            throw new ArgumentException("--lang must be RU or EN");
        bool reika = !options.Flag("generic") && DeltaProject.LooksLikeReika(executable);
        return DeltaProject.Create(toolsDirectory, game, executable, source, language, reika);
    }

    private static string Quote(string value)
    {
        return DeltaProject.QuoteArgument(value);
    }

    // ---- running the scripts --------------------------------------------

    private static DeltaPython.PythonRuntime RequirePython()
    {
        DeltaPython.PythonRuntime runtime = DeltaPython.Detect();
        if (runtime == null)
            throw new InvalidOperationException(
                "Python " + DeltaPython.MinimumVersion +
                " or newer was not found in PATH, the Windows launcher, or HOME.");
        if (runtime.Version.CompareTo(DeltaPython.MinimumVersion) < 0)
            throw new InvalidOperationException(
                "Found Python " + runtime.Version + " at " + runtime.Executable +
                ", but Python " + DeltaPython.MinimumVersion + " or newer is required.");
        return runtime;
    }

    private static int Script(string script, List<string> arguments)
    {
        StringBuilder line = new StringBuilder();
        foreach (string argument in arguments)
        {
            if (line.Length > 0)
                line.Append(' ');
            line.Append(Quote(argument));
        }
        return Script(script, line.ToString());
    }

    private static int Script(string script, string arguments)
    {
        string path = Path.Combine(toolsDirectory, "py", script);
        if (!File.Exists(path))
            throw new FileNotFoundException("Script not found.", path);
        string line = Quote(path) + (arguments.Length > 0 ? " " + arguments : "");

        DeltaPython.PythonRuntime runtime = RequirePython();
        // Forward the script output line by line rather than letting it inherit
        // the console: a redirected or scripted host would otherwise see nothing.
        ProcessStartInfo start = DeltaPython.Start(runtime.Executable, line, toolsDirectory, true);
        // The key is inherited from the environment. It is deliberately not
        // required up front: whether DeepL is called at all depends on what the
        // cache covers, and only the script has read it.
        return Forward(start);
    }

    private static int Resource(List<string> arguments)
    {
        StringBuilder line = new StringBuilder();
        foreach (string argument in arguments)
        {
            if (line.Length > 0) line.Append(' ');
            line.Append(Quote(argument));
        }
        return Resource(line.ToString());
    }

    private static int Resource(string arguments)
    {
        string executable = Path.Combine(toolsDirectory, "bin", "DeltaResourceTool.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Build the C# resource backend first.", executable);
        return Forward(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = toolsDirectory
        });
    }

    // Child output is read and re-printed rather than inherited, so it survives
    // being piped, redirected to a file, or run from another program.
    private static int Forward(ProcessStartInfo start)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using (Process process = new Process { StartInfo = start, EnableRaisingEvents = true })
        {
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
            { if (args.Data != null) Console.Out.WriteLine(args.Data); };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
            { if (args.Data != null) Console.Error.WriteLine(args.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    private static int Run(List<string> arguments)
    {
        if (arguments.Count == 0)
            throw new ArgumentException("run needs a script name from py\\");
        string script = arguments[0];
        if (!script.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            script += ".py";
        return Script(script, arguments.GetRange(1, arguments.Count - 1));
    }

    private static int ReportPython(List<string> arguments)
    {
        Parse(arguments);
        DeltaPython.PythonRuntime runtime = DeltaPython.Detect();
        if (runtime == null)
        {
            Console.Error.WriteLine("No Python interpreter was found.");
            return 1;
        }
        Console.WriteLine("Python " + runtime.Version + " via " + runtime.Source);
        Console.WriteLine(runtime.Executable);
        Console.WriteLine("Scripts: " + Path.Combine(toolsDirectory, "py"));
        return runtime.Version.CompareTo(DeltaPython.MinimumVersion) >= 0 ? 0 : 1;
    }

    // ---- commands --------------------------------------------------------

    private static int Extract(List<string> arguments)
    {
        Options options = Parse(arguments);
        DeltaProject project = Project(options);
        Directory.CreateDirectory(project.WorkDirectory);
        // The extractor verifies RSAN.SD against the build it was written for.
        // That check only means something for that profile.
        string unknown = project.Reika ? "" : " --allow-unknown";
        return Script("delta_overlay.py",
            "extract " + Quote(project.Source) + " " + Quote(project.Workbook) + unknown);
    }

    private static int Translate(List<string> arguments, bool estimate)
    {
        Options options = Parse(arguments);
        DeltaProject project = Project(options);
        if (!File.Exists(project.Workbook))
            throw new FileNotFoundException("Run extract first.", project.Workbook);
        string extra = estimate
            ? " --estimate" + (options.Flag("overwrite") ? " --overwrite" : "")
            : (options.Flag("overwrite") ? " --overwrite" : "");
        string dialogs = options.Values.ContainsKey("max-dialogs") ? " --max-dialogs " + options.Values["max-dialogs"] : "";
        string loopRetries = options.Values.ContainsKey("loop-retries") ? " --loop-retries " + options.Values["loop-retries"] : "";
        return Script("delta_deepl.py",
            Quote(project.Workbook) + " " + Quote(project.TranslatedWorkbook) +
            " --target-lang " + project.Language + " --cache " + Quote(project.Cache) + extra + dialogs + loopRetries);
    }

    private static int Proofread(List<string> arguments)
    {
        Options options = Parse(arguments);
        DeltaProject project = Project(options);
        string input = File.Exists(project.TranslatedWorkbook) ? project.TranslatedWorkbook : project.Workbook;
        if (!File.Exists(input))
            throw new FileNotFoundException("Run extract first.", input);
        string seed = project.RulesSeed(toolsDirectory);
        if (!File.Exists(project.Rules) && File.Exists(seed))
        {
            Directory.CreateDirectory(project.WorkDirectory);
            File.Copy(seed, project.Rules);
            Console.WriteLine("Created " + project.Rules + " from " + Path.GetFileName(seed));
        }
        string profilePath = project.ProofreadProfile(toolsDirectory);
        string profile = File.Exists(profilePath) ? " --profile " + Quote(profilePath) : "";
        string rules = File.Exists(project.Rules) ? " --rules " + Quote(project.Rules) : "";
        return Script("proofread.py",
            Quote(input) + " " + Quote(project.ProofreadWorkbook) +
            " --backup " + Quote(project.ProofreadWorkbook + ".before.xlsx") + profile + rules);
    }

    private static int Overlay(List<string> arguments)
    {
        Options options = Parse(arguments);
        DeltaProject project = Project(options);
        if (!File.Exists(project.TranslatedWorkbook))
            throw new FileNotFoundException("Run translate first.", project.TranslatedWorkbook);
        string input = File.Exists(project.ProofreadWorkbook) ? project.ProofreadWorkbook : project.TranslatedWorkbook;
        project.WarnAboutProofread(Console.Error.WriteLine);
        string names = project.Reika ? " --target-lang " + project.Language : "";
        string staged = Path.Combine(project.WorkDirectory, Path.GetFileName(project.Overlay));

        // Measure against the layout the game will actually run with, when it
        // already has one, so the widths in the report are the widths on screen.
        string ini = Path.Combine(project.GameDirectory, "delta_launcher.ini");
        string layout = File.Exists(ini) ? " --launcher-ini " + Quote(ini) : "";
        string fitReport = Path.Combine(
            project.WorkDirectory,
            Path.GetFileNameWithoutExtension(project.Overlay) + ".fit.tsv");

        string strict = options.Flags.Contains("strict-fit") ? " --strict-fit" : "";

        int code = Script("delta_overlay.py",
            "build-overlay " + Quote(input) + " " + Quote(staged) + names + layout
            + " --fit-report " + Quote(fitReport) + strict);
        if (code != 0)
            return code;

        File.Copy(staged, project.Overlay, true);
        Console.WriteLine("Installed: " + project.Overlay);
        return 0;
    }

    private static int Menu(List<string> arguments)
    {
        Options options = Parse(arguments);
        DeltaProject project = Project(options);
        Directory.CreateDirectory(project.WorkDirectory);
        string catalog = Quote(project.MenuCatalog);
        int code = Resource("menu extract " + Quote(project.Executable) + " " + catalog);
        if (code != 0) return code;
        foreach (string language in new[] { "RU", "EN" })
        {
            code = Script("delta_menu.py",
                "translate " + catalog + " " + catalog + " --target-lang " + language +
                " --cache " + Quote(project.Cache));
            if (code != 0) return code;
        }
        // The runtime table is read from the game folder; the copy in the work
        // folder is what the next build diffs against.
        foreach (string directory in new[] { project.GameDirectory, project.WorkDirectory })
            foreach (string language in new[] { "RU", "EN" })
            {
                string output = Path.Combine(directory, "delta_menu." + language.ToLowerInvariant() + ".tsv");
                code = Resource(
                    "menu runtime " + catalog + " " + Quote(output) + " --target-lang " + language);
                if (code != 0) return code;
            }
        return 0;
    }

    private static int Pipeline(List<string> arguments)
    {
        foreach (string step in new[] { "extract", "translate", "proofread", "overlay" })
        {
            Console.WriteLine();
            Console.WriteLine("== " + step);
            List<string> stepArguments = new List<string>(arguments);
            if (step != "translate")
                stepArguments.Remove("--overwrite");
            int code;
            switch (step)
            {
                case "extract": code = Extract(stepArguments); break;
                case "translate": code = Translate(stepArguments, false); break;
                case "proofread": code = Proofread(stepArguments); break;
                default: code = Overlay(stepArguments); break;
            }
            if (code != 0)
            {
                Console.Error.WriteLine(step + " failed with exit code " + code);
                return code;
            }
        }
        return 0;
    }

    // ---- build -----------------------------------------------------------

    private static int Build(List<string> arguments)
    {
        Options options = Parse(arguments);
        string bin = Path.Combine(toolsDirectory, "bin");
        Directory.CreateDirectory(bin);

        string csc = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"Microsoft.NET\Framework\v4.0.30319\csc.exe");
        if (!File.Exists(csc))
            throw new FileNotFoundException("The .NET Framework C# compiler was not found.", csc);

        string gui = Path.Combine(toolsDirectory, "gui");
        string shared = Quote(Path.Combine(gui, "DeltaPython.cs")) + " " +
            Quote(Path.Combine(gui, "DeltaProject.cs")) + " " +
            Quote(Path.Combine(gui, "AssemblyInfo.cs"));
        string icon = "/win32icon:" + Quote(Path.Combine(toolsDirectory, "DeltaTranslator.ico"));
        string references = "/reference:System.dll /reference:System.Core.dll " +
            "/reference:System.Drawing.dll /reference:System.Windows.Forms.dll";
        // The window reads the menu catalog; JavaScriptSerializer lives here.
        string windowReferences = references +
            " /reference:System.Web.Extensions.dll /reference:System.Security.dll";

        // DeltaTranslator.exe is both the toolset window and, once copied into a
        // game folder as DeltaLauncher.exe, the language picker.
        int code = Build(csc, Path.Combine(bin, "DeltaTranslator.exe"),
            "/target:winexe /main:DeltaTranslatorProgram " + icon + " " +
            windowReferences + " " + Quote(Path.Combine(gui, "DeltaTranslatorGui.cs")) + " " + shared);
        if (code != 0) return code;

        code = Build(csc, Path.Combine(bin, "delta.exe"),
            "/target:exe /main:DeltaCliProgram " + icon + " " +
            references + " " + Quote(Path.Combine(gui, "DeltaCli.cs")) + " " + shared);
        if (code != 0) return code;

        string garbroDirectory = FindLatestGarbroDirectory(
            Path.Combine(toolsDirectory, "vendor"));
        if (garbroDirectory == null)
            throw new DirectoryNotFoundException("The vendored GARbro directory was not found.");
        Console.WriteLine("GARbro directory: " + garbroDirectory);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string presentationCore = FindAssembly(
            Path.Combine(windows, @"Microsoft.NET\assembly\GAC_32\PresentationCore"),
            "PresentationCore.dll");
        string windowsBase = FindAssembly(
            Path.Combine(windows, @"Microsoft.NET\assembly\GAC_MSIL\WindowsBase"),
            "WindowsBase.dll");
        string systemXaml = FindAssembly(
            Path.Combine(windows, @"Microsoft.NET\assembly\GAC_MSIL\System.Xaml"),
            "System.Xaml.dll");
        string resourceReferences = windowReferences +
            " /reference:" + Quote(windowsBase) +
            " /reference:" + Quote(presentationCore) +
            " /reference:" + Quote(systemXaml) +
            " /reference:" + Quote(Path.Combine(garbroDirectory, "GameRes.dll")) +
            " /reference:" + Quote(Path.Combine(garbroDirectory, "ArcFormats.dll"));
        code = Build(csc, Path.Combine(bin, "DeltaResourceTool.exe"),
            "/target:exe /platform:x64 /main:DeltaResourceToolProgram " + icon + " " +
            resourceReferences + " " + Quote(Path.Combine(gui, "DeltaResourceTool.cs")) + " " +
            Quote(Path.Combine(gui, "AssemblyInfo.cs")));
        if (code != 0) return code;
        foreach (string dependency in Directory.GetFiles(
            garbroDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            string target = Path.Combine(bin, Path.GetFileName(dependency));
            File.Copy(dependency, target, true);
            string version;
            try
            {
                version = FileVersionInfo.GetVersionInfo(dependency).FileVersion;
            }
            catch
            {
                version = null;
            }
            Console.WriteLine("GARbro dependency: " + Path.GetFileName(dependency) +
                (string.IsNullOrEmpty(version) ? string.Empty : " " + version));
        }

        if (options.Flag("proxy"))
        {
            code = BuildProxy(bin);
            if (code != 0) return code;
        }
        return 0;
    }

    private static string FindLatestGarbroDirectory(string vendorDirectory)
    {
        var candidates = Directory.GetDirectories(
            vendorDirectory, "GARbro*", SearchOption.TopDirectoryOnly)
            .Where(path => File.Exists(Path.Combine(path, "GameRes.dll")) &&
                File.Exists(Path.Combine(path, "ArcFormats.dll")))
            .Select(path => new { Path = path, Version = ParseGarbroVersion(path) })
            .ToArray();

        var versioned = candidates.Where(candidate => candidate.Version != null)
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefault();
        var fallback = candidates.FirstOrDefault();
        return versioned != null ? versioned.Path :
            (fallback != null ? fallback.Path : null);
    }

    private static Version ParseGarbroVersion(string path)
    {
        string name = Path.GetFileName(path);
        if (!name.StartsWith("GARbro", StringComparison.OrdinalIgnoreCase))
            return null;
        string value = name.Substring("GARbro".Length).TrimStart('-', '_', 'v', 'V');
        Version version;
        return Version.TryParse(value, out version) ? version : null;
    }

    private static string FindAssembly(string directory, string name)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(".NET assembly directory was not found: " + directory);
        string found = Directory.GetFiles(directory, name, SearchOption.AllDirectories).FirstOrDefault();
        if (found == null)
            throw new FileNotFoundException("Required .NET assembly was not found.", name);
        return found;
    }

    // Compile beside the target and swap. If the image is running, a tiny
    // second CLI process waits for this build to finish and replaces it then;
    // generated binaries have no useful backup state to preserve.
    private static int Build(string csc, string target, string arguments)
    {
        string staged = target + ".new";
        int code = Compile(csc, "/nologo /optimize+ /out:" + Quote(staged) + " " + arguments);
        if (code != 0)
            return code;

        if (File.Exists(target) && !TryDelete(target))
        {
            string current = Assembly.GetExecutingAssembly().Location;
            string helper = string.Equals(
                Path.GetFullPath(current), Path.GetFullPath(target),
                StringComparison.OrdinalIgnoreCase) ? staged : current;
            Process.Start(new ProcessStartInfo
            {
                FileName = helper,
                Arguments = "--complete-build " + Process.GetCurrentProcess().Id + " " +
                    Quote(staged) + " " + Quote(target),
                WorkingDirectory = toolsDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Console.WriteLine("Built (replacement pending): " + target);
            return 0;
        }
        File.Move(staged, target);
        Console.WriteLine("Built: " + target);
        return 0;
    }

    private static int CompleteDeferredBuild(string processId, string staged, string target)
    {
        int id;
        if (!int.TryParse(processId, out id)) return 2;
        try { Process.GetProcessById(id).WaitForExit(); }
        catch (ArgumentException) { }

        for (int attempt = 0; attempt < 600; attempt++)
        {
            if ((!File.Exists(target) || TryDelete(target)) && File.Exists(staged))
            {
                File.Move(staged, target);
                return 0;
            }
            System.Threading.Thread.Sleep(100);
        }
        return 1;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static int Compile(string csc, string arguments)
    {
        int code = Forward(new ProcessStartInfo
        {
            FileName = csc,
            Arguments = arguments,
            WorkingDirectory = toolsDirectory
        });
        if (code != 0)
            Console.Error.WriteLine("The compiler returned exit code " + code);
        return code;
    }

    private static int BuildProxy(string bin)
    {
        string vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft Visual Studio\Installer\vswhere.exe");
        if (!File.Exists(vswhere))
            throw new FileNotFoundException("Visual Studio was not found.", vswhere);

        string installation = Capture(vswhere, "-latest -products * -property installationPath").Trim();
        if (installation.Length == 0)
            throw new InvalidOperationException("vswhere did not report a Visual Studio installation.");
        string msbuild = Path.Combine(installation, @"MSBuild\Current\Bin\MSBuild.exe");
        if (!File.Exists(msbuild))
            throw new FileNotFoundException("MSBuild was not found.", msbuild);

        string project = Path.Combine(toolsDirectory, @"vendor\VNTranslationTools\VNTextProxy\VNTextProxy.vcxproj");
        string proxyOutput = Path.Combine(bin, "proxy-build");
        string proxyIntermediate = Path.Combine(bin, "proxy-obj");
        Directory.CreateDirectory(proxyOutput);
        Directory.CreateDirectory(proxyIntermediate);
        // The project file still names v142; pass the installed toolset instead.
        int code = Forward(new ProcessStartInfo
        {
            FileName = msbuild,
            Arguments = Quote(project) +
                " /p:Platform=Win32 /p:Configuration=Release" +
                " /p:OutDir=" + Quote(proxyOutput.Replace('\\', '/') + "/") +
                " /p:IntDir=" + Quote(proxyIntermediate.Replace('\\', '/') + "/") +
                " /p:PlatformToolset=v143 /v:minimal /nologo",
            WorkingDirectory = toolsDirectory
        });
        if (code != 0)
            return code;

        string built = Path.Combine(proxyOutput, "winmm.dll");
        if (!File.Exists(built))
            throw new FileNotFoundException("MSBuild reported success but produced no winmm.dll.", built);
        File.Copy(built, Path.Combine(bin, "winmm.dll"), true);
        Console.WriteLine("Built: " + Path.Combine(bin, "winmm.dll"));
        return 0;
    }

    private static string Capture(string executable, string arguments)
    {
        ProcessStartInfo start = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        using (Process process = Process.Start(start))
        {
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
    }
}
