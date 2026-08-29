using System;
using System.IO;
using System.Linq;
using System.Text;

// Every path the pipeline touches is derived from three inputs: the game folder,
// the tools folder and the target language. The window and the command line both
// build it here so a rename can never mean two different things to them.
internal sealed class DeltaProject
{
    public string GameDirectory;
    public string Executable;
    public string Source;
    public string Language;
    public string WorkDirectory;
    public string Workbook;
    public string TranslatedWorkbook;
    public string ProofreadWorkbook;
    public string Rules;
    public string Cache;
    public string Overlay;
    public string MenuCatalog;
    public string RuntimeMenu;
    public string GameName;
    public bool Reika;

    internal static DeltaProject Create(
        string toolsDirectory,
        string game,
        string executable,
        string source,
        string language,
        bool reika)
    {
        if (!Directory.Exists(game))
            throw new InvalidOperationException("Select a valid game folder first.");
        if (!File.Exists(executable))
            throw new InvalidOperationException("Select the game executable.");
        if (!File.Exists(source))
            throw new InvalidOperationException("Select the Delta source archive (usually RSAN.SD).");

        string code = language.ToLowerInvariant();
        string gameName = GameFolderName(game);
        string work = WorkDirectoryFor(toolsDirectory, game);
        return new DeltaProject
        {
            GameDirectory = game,
            Executable = executable,
            Source = source,
            Language = language,
            WorkDirectory = work,
            Workbook = Path.Combine(work, "source.xlsx"),
            TranslatedWorkbook = Path.Combine(work, "translation." + code + ".xlsx"),
            ProofreadWorkbook = Path.Combine(work, "translation." + code + ".proofread.xlsx"),
            Rules = Path.Combine(work, "proofread_rules." + code + ".json"),
            Cache = Path.Combine(work, "deepl_cache.jsonl"),
            Overlay = Path.Combine(game, "delta_overlay." + code + ".bin"),
            MenuCatalog = Path.Combine(work, "delta_menu.json"),
            RuntimeMenu = Path.Combine(game, "delta_menu." + code + ".tsv"),
            GameName = gameName,
            Reika = reika
        };
    }

    // FileNotFoundException carries the path in FileName, not in Message, and
    // this toolset juggles paths across two folders - dropping it turns "Run
    // extract first" into a hunt. Both front ends report through this.
    internal static string Describe(Exception error)
    {
        FileNotFoundException missingFile = error as FileNotFoundException;
        if (missingFile != null && !string.IsNullOrEmpty(missingFile.FileName))
            return missingFile.Message + " Expected: " + missingFile.FileName;
        DirectoryNotFoundException missingDirectory = error as DirectoryNotFoundException;
        if (missingDirectory != null)
            return missingDirectory.Message;
        return error.Message;
    }

    // ProcessStartInfo.Arguments is one Windows command-line string on the
    // .NET Framework used here. Backslashes immediately before a quote, and at
    // the end of a quoted argument, must be doubled for CommandLineToArgvW.
    internal static string QuoteArgument(string value)
    {
        if (value == null)
            throw new ArgumentNullException("value");

        StringBuilder quoted = new StringBuilder(value.Length + 2);
        quoted.Append('"');
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1);
                quoted.Append('"');
                backslashes = 0;
                continue;
            }
            quoted.Append('\\', backslashes);
            quoted.Append(character);
            backslashes = 0;
        }
        quoted.Append('\\', backslashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }

    // The overlay is built from the proofread workbook whenever one exists, and
    // silently from the raw translation when it does not. Both gaps are invisible
    // in the build output: a proofread pass that failed leaves no file at all, so
    // the fallback looks exactly like a project that never ran the step.
    internal void WarnAboutProofread(Action<string> report)
    {
        if (!File.Exists(TranslatedWorkbook))
            return;
        if (!File.Exists(ProofreadWorkbook))
        {
            report("warning: " + Path.GetFileName(ProofreadWorkbook) + " does not exist; " +
                "the overlay is built from the raw translation. If proofread was run and " +
                "failed, its output is missing and this build has none of its fixes.");
            return;
        }
        DateTime proofread = File.GetLastWriteTimeUtc(ProofreadWorkbook);
        DateTime translated = File.GetLastWriteTimeUtc(TranslatedWorkbook);
        if (translated <= proofread)
            return;
        report("warning: " + Path.GetFileName(TranslatedWorkbook) + " is newer than " +
            Path.GetFileName(ProofreadWorkbook) + "; the overlay is built from the " +
            "proofread copy, so edits made since that pass are not included. Run proofread again.");
    }

    // Steps that only touch generated assets need the work folder but not the
    // executable or the script archive, so the rule lives on its own.
    internal static string GameFolderName(string game)
    {
        return Sanitize(Path.GetFileName(
            game.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
    }

    internal static string WorkDirectoryFor(string toolsDirectory, string game)
    {
        return Path.Combine(toolsDirectory, "work", GameFolderName(game));
    }

    // Auto-detection: an RSA.EXE next to RSAN.SD is the Reika build the
    // name-plate and script rules were written against.
    internal static bool LooksLikeReika(string executable)
    {
        return string.Equals(Path.GetFileName(executable), "RSA.EXE", StringComparison.OrdinalIgnoreCase);
    }

    internal static string Sanitize(string value)
    {
        StringBuilder result = new StringBuilder();
        foreach (char character in value)
            result.Append(Path.GetInvalidFileNameChars().Contains(character) ? '_' : character);
        return result.Length == 0 ? "delta_game" : result.ToString();
    }

    // work\<game>\proofread_rules.<lang>.json is the file the proofreader
    // reads; the template is only its starting point, copied once when it does
    // not exist. There is deliberately no per-game profile: a second tracked
    // copy of the same rules is a copy that goes stale.
    internal string RulesSeed(string toolsDirectory)
    {
        return Path.Combine(
            toolsDirectory, "profiles",
            "proofread_rules." + Language.ToLowerInvariant() + ".template.json");
    }

    internal string ProofreadProfile(string toolsDirectory)
    {
        return Path.Combine(
            toolsDirectory, "profiles",
            "proofread_common." + Language.ToLowerInvariant() + ".json");
    }
}
