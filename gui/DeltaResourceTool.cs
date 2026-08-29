using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Media.Imaging;
using GameRes;
using GameRes.Formats.Triangle;

[assembly: System.Reflection.AssemblyTitle("Delta Resource Tool")]
[assembly: System.Reflection.AssemblyDescription("Native .NET resource backend for Delta/Route2 translation tools.")]

internal static class DeltaResourceToolProgram
{
    private const uint RleFlag = 0x80000000;
    private const uint StoredFlag = 0x40000000;
    private const uint SizeMask = 0x3FFFFFFF;
    private const uint IndexFlag = 0x80000000;
    private const uint OffsetMask = 0x7FFFFFFF;
    private const int NameField = 28;
    private const int EntryRecord = 32;
    private const int RingSize = 4096;
    private const int Window = 18;
    private const int Threshold = 2;
    private const string ManifestName = "manifest.json";

    private sealed class ParsedArguments
    {
        public readonly List<string> Positionals = new List<string>();
        public readonly Dictionary<string, string> Values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<string>> Multiple =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Flags =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string Value(string name, string fallback)
        {
            string value;
            return Values.TryGetValue(name, out value) ? value : fallback;
        }
    }

    private sealed class CgfRecord
    {
        public string Name;
        public bool Flagged;
        public int Offset;
        public int Size;
    }

    private static int Main(string[] rawArguments)
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch { }
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
        {
            string requester = args.RequestingAssembly == null
                ? "(unknown)" : args.RequestingAssembly.FullName;
            Console.Error.WriteLine(
                "DLL resolution anomaly: requested " + args.Name + " by " + requester +
                "; base directory " + AppDomain.CurrentDomain.BaseDirectory);
            return null;
        };
        try
        {
            if (rawArguments.Length == 0)
                throw new ArgumentException("Expected cgf, iaf, mhu, fit, or menu command.");
            string family = rawArguments[0].ToLowerInvariant();
            string[] rest = rawArguments.Skip(1).ToArray();
            switch (family)
            {
                case "cgf": return Cgf(rest);
                case "iaf": return Iaf(rest);
                case "mhu": return Mhu(rest);
                case "fit": return DeltaFitServer.Run(rest);
                case "menu": return DeltaMenuResources.Run(rest);
                default: throw new ArgumentException("Unknown resource command: " + family);
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("error: " + Describe(error));
            if (!(error is ArgumentException))
                Console.Error.WriteLine("diagnostic exception: " + error);
            return 1;
        }
    }

    private static string Describe(Exception error)
    {
        while (error.InnerException != null &&
               (error is System.Reflection.TargetInvocationException ||
                error is TypeInitializationException))
            error = error.InnerException;
        return error.Message;
    }

    private static ParsedArguments Parse(
        IEnumerable<string> raw, IEnumerable<string> valueNames,
        IEnumerable<string> multiNames, IEnumerable<string> flagNames)
    {
        HashSet<string> values = new HashSet<string>(valueNames, StringComparer.OrdinalIgnoreCase);
        HashSet<string> multis = new HashSet<string>(multiNames, StringComparer.OrdinalIgnoreCase);
        HashSet<string> flags = new HashSet<string>(flagNames, StringComparer.OrdinalIgnoreCase);
        string[] items = raw.ToArray();
        ParsedArguments parsed = new ParsedArguments();
        for (int index = 0; index < items.Length; index++)
        {
            string item = items[index];
            if (!item.StartsWith("--", StringComparison.Ordinal))
            {
                parsed.Positionals.Add(item);
                continue;
            }
            string name = item.Substring(2);
            if (flags.Contains(name))
            {
                parsed.Flags.Add(name);
                continue;
            }
            if (!values.Contains(name) && !multis.Contains(name))
                throw new ArgumentException("Unknown option: --" + name);
            if (++index >= items.Length)
                throw new ArgumentException("--" + name + " needs a value");
            if (multis.Contains(name))
            {
                List<string> list;
                if (!parsed.Multiple.TryGetValue(name, out list))
                    parsed.Multiple[name] = list = new List<string>();
                list.Add(items[index]);
            }
            else
                parsed.Values[name] = items[index];
        }
        return parsed;
    }

    private static ArchiveFormat CgfFormat()
    {
        ArchiveFormat result = FormatCatalog.Instance.ArcFormats
            .FirstOrDefault(format => string.Equals(format.Tag, "CGF", StringComparison.Ordinal));
        if (result == null)
            throw new InvalidOperationException("GARbro CGF format is unavailable.");
        return result;
    }

    private static int Cgf(string[] arguments)
    {
        if (arguments.Length == 0)
            throw new ArgumentException(
                "cgf needs list, extract, extract-localizable, build, build-localized, or build-localized-set.");
        string command = arguments[0].ToLowerInvariant();
        string[] rest = arguments.Skip(1).ToArray();
        switch (command)
        {
            case "list": CgfList(rest); break;
            case "extract": CgfExtract(rest); break;
            case "extract-localizable": CgfExtractLocalizable(rest); break;
            case "build": CgfBuild(rest); break;
            case "build-localized": CgfBuildLocalized(rest); break;
            case "build-localized-set": CgfBuildLocalizedSet(rest); break;
            default: throw new ArgumentException("Unknown cgf command: " + command);
        }
        return 0;
    }

    private static void CgfList(string[] arguments)
    {
        ParsedArguments parsed = Parse(
            arguments, new[] { "garbro-dir" }, new string[0], new[] { "json" });
        RequirePositionals(parsed, 1, "cgf list <archive>");
        string archivePath = Path.GetFullPath(parsed.Positionals[0]);
        using (ArcView view = new ArcView(archivePath))
        using (ArcFile archive = CgfFormat().TryOpen(view))
        {
            if (archive == null)
                throw new InvalidDataException("GARbro CgfOpener rejected " + parsed.Positionals[0]);
            if (parsed.Flags.Contains("json"))
            {
                List<Dictionary<string, object>> entries = new List<Dictionary<string, object>>();
                foreach (Entry entry in archive.Dir)
                {
                    Dictionary<string, object> item = new Dictionary<string, object>();
                    item["name"] = entry.Name;
                    item["offset"] = entry.Offset;
                    item["size"] = entry.Size;
                    item["type"] = string.IsNullOrEmpty(entry.Type) ? "" : entry.Type;
                    entries.Add(item);
                }
                Console.WriteLine(new JavaScriptSerializer().Serialize(entries));
                return;
            }
            Console.WriteLine("Entries: " + archive.Dir.Count);
            foreach (Entry entry in archive.Dir)
                Console.WriteLine("  {0,-28} {1,9} bytes  {2}",
                    entry.Name, entry.Size, string.IsNullOrEmpty(entry.Type) ? "binary" : entry.Type);
        }
    }

    private static void CgfExtract(string[] arguments)
    {
        ParsedArguments parsed = Parse(
            arguments, new[] { "garbro-dir", "suffix" }, new[] { "name" }, new[] { "no-images" });
        RequirePositionals(parsed, 2, "cgf extract <archive> <output>");
        string archivePath = Path.GetFullPath(parsed.Positionals[0]);
        string output = Path.GetFullPath(parsed.Positionals[1]);
        Directory.CreateDirectory(output);
        byte[] source = File.ReadAllBytes(archivePath);
        List<CgfRecord> raw = ReadCgfIndex(source);
        Dictionary<long, CgfRecord> byOffset = raw.ToDictionary(item => (long)item.Offset);
        HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> requested;
        if (parsed.Multiple.TryGetValue("name", out requested))
            foreach (string name in requested) selected.Add(name);

        List<Dictionary<string, object>> manifest = new List<Dictionary<string, object>>();
        string suffix = parsed.Value("suffix", "").Trim();
        if (suffix.Length > 0 && !suffix.StartsWith(".", StringComparison.Ordinal))
            suffix = "." + suffix;
        ArchiveFormat format = CgfFormat();
        using (ArcView view = new ArcView(archivePath))
        using (ArcFile archive = format.TryOpen(view))
        {
            if (archive == null)
                throw new InvalidDataException("GARbro CgfOpener rejected " + archivePath);
            foreach (Entry entry in archive.Dir)
            {
                if (selected.Count > 0 && !selected.Contains(entry.Name))
                    continue;
                CgfRecord record;
                string mode = "stored";
                bool flagged = false;
                if (byOffset.TryGetValue(entry.Offset, out record))
                {
                    mode = PayloadMode(source, record.Offset, record.Size);
                    flagged = record.Flagged;
                }
                bool image = string.Equals(entry.Type, "image", StringComparison.OrdinalIgnoreCase);
                string imageExtension = suffix.Length == 0 ? ".iaf" : ".IAF";
                string fileName = entry.Name + suffix + (image ? imageExtension : ".bin");
                using (Stream stream = format.OpenEntry(archive, entry))
                    File.WriteAllBytes(Path.Combine(output, fileName), ReadAll(stream));

                Dictionary<string, object> item = new Dictionary<string, object>();
                item["name"] = entry.Name;
                item["file"] = fileName;
                item["mode"] = mode;
                item["flagged"] = flagged;
                item["type"] = string.IsNullOrEmpty(entry.Type) ? "binary" : entry.Type;
                if (image && !parsed.Flags.Contains("no-images"))
                {
                    string preview = entry.Name + suffix + ".png";
                    int width, height;
                    using (Stream stream = format.OpenEntry(archive, entry))
                        DecodeIafStream(stream, entry.Name + ".iaf", Path.Combine(output, preview),
                            true, out width, out height);
                    item["preview"] = preview;
                    item["width"] = width;
                    item["height"] = height;
                }
                manifest.Add(item);
            }
        }
        Dictionary<string, object> root = new Dictionary<string, object>();
        root["format"] = "delta-cgf";
        root["entries"] = manifest;
        WriteJson(Path.Combine(output, ManifestName), root);
        Console.WriteLine("Extracted {0} entries to {1}", manifest.Count, output);
    }

    private static void CgfExtractLocalizable(string[] arguments)
    {
        ParsedArguments parsed = Parse(arguments, new string[0], new string[0], new string[0]);
        RequirePositionals(parsed, 2, "cgf extract-localizable <CG-folder> <ui-assets>");
        string cgDirectory = Path.GetFullPath(parsed.Positionals[0]);
        string assets = Path.GetFullPath(parsed.Positionals[1]);
        if (!Directory.Exists(cgDirectory))
            throw new DirectoryNotFoundException("CG folder was not found: " + cgDirectory);
        Directory.CreateDirectory(assets);

        string[] archives = OriginalResourceFiles(cgDirectory, ".CGF")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int extractedArchives = 0;
        foreach (string activeArchive in archives)
        {
            string name = Path.GetFileNameWithoutExtension(activeArchive);
            string archive = JapaneseCompanionOrBase(activeArchive);
            string output = Path.Combine(assets, name);
            try
            {
                CgfExtract(new[] { archive, output, "--suffix", "jp" });
                extractedArchives++;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    "warning: could not extract {0}: {1}", Path.GetFileName(archive), Describe(error));
            }
        }

        string looseOutput = Path.Combine(assets, "_loose");
        Directory.CreateDirectory(looseOutput);
        string[] looseImages = OriginalResourceFiles(cgDirectory, ".IAF").ToArray();
        int extractedImages = 0;
        foreach (string activeImage in looseImages)
        {
            string name = Path.GetFileNameWithoutExtension(activeImage);
            string source = JapaneseCompanionOrBase(activeImage);
            try
            {
                File.Copy(source, Path.Combine(looseOutput, name + ".jp.IAF"), true);
                int width, height;
                using (IBinaryStream binary = BinaryStream.FromFile(source))
                    DecodeIaf(binary, Path.GetFileName(source),
                        Path.Combine(looseOutput, name + ".jp.png"), true, out width, out height);
                extractedImages++;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    "warning: could not extract {0}: {1}", Path.GetFileName(source), Describe(error));
            }
        }
        if (extractedArchives == 0 && extractedImages == 0)
            throw new InvalidDataException("No CGF archives or IAF images could be extracted.");
        Console.WriteLine("CGF archives extracted: {0} of {1}", extractedArchives, archives.Length);
        Console.WriteLine("Loose IAF images extracted: {0} of {1}", extractedImages, looseImages.Length);
        Console.WriteLine("UI assets: " + assets);
        Console.WriteLine(
            "Translate the *.jp.png files and save matching *.ru.png or *.en.png files beside them. " +
            "Keep the pixel dimensions unchanged.");
    }

    private static void CgfBuild(string[] arguments)
    {
        ParsedArguments parsed = Parse(arguments, new[] { "mode" }, new string[0], new string[0]);
        RequirePositionals(parsed, 2, "cgf build <input> <archive>");
        string input = Path.GetFullPath(parsed.Positionals[0]);
        string output = Path.GetFullPath(parsed.Positionals[1]);
        Dictionary<string, object> root = ReadJson(Path.Combine(input, ManifestName));
        object[] entries = (object[])root["entries"];
        List<byte[]> payloads = new List<byte[]>();
        foreach (object value in entries)
        {
            Dictionary<string, object> item = (Dictionary<string, object>)value;
            string mode = parsed.Value("mode", Convert.ToString(item["mode"], CultureInfo.InvariantCulture));
            payloads.Add(PackPayload(
                File.ReadAllBytes(Path.Combine(input, Convert.ToString(item["file"]))), mode));
        }
        WriteCgfFromManifest(entries, payloads, output);
        Console.WriteLine("Wrote {0} entries to {1} ({2} bytes)",
            entries.Length, output, new FileInfo(output).Length);
    }

    private static void CgfBuildLocalized(string[] arguments)
    {
        ParsedArguments parsed = Parse(
            arguments, new[] { "language", "garbro-dir" }, new string[0], new string[0]);
        RequirePositionals(parsed, 3, "cgf build-localized <archive> <assets> <output> --language ru|en");
        string language = parsed.Value("language", "").ToLowerInvariant();
        if (language != "ru" && language != "en")
            throw new ArgumentException("--language must be ru or en");
        string sourcePath = Path.GetFullPath(parsed.Positionals[0]);
        string assets = Path.GetFullPath(parsed.Positionals[1]);
        string output = Path.GetFullPath(parsed.Positionals[2]);
        BuildLocalizedArchive(sourcePath, new[] { assets }, output, language, true);
    }

    private static void CgfBuildLocalizedSet(string[] arguments)
    {
        ParsedArguments parsed = Parse(arguments, new string[0], new string[0], new string[0]);
        RequirePositionals(parsed, 2, "cgf build-localized-set <CG-folder> <ui-assets>");
        string cgDirectory = Path.GetFullPath(parsed.Positionals[0]);
        string assets = Path.GetFullPath(parsed.Positionals[1]);
        if (!Directory.Exists(cgDirectory))
            throw new DirectoryNotFoundException("CG folder was not found: " + cgDirectory);
        if (!Directory.Exists(assets))
            throw new DirectoryNotFoundException("UI asset folder was not found: " + assets);

        int builtArchives = 0;
        foreach (string activeArchive in OriginalResourceFiles(cgDirectory, ".CGF")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileNameWithoutExtension(activeArchive);
            string archive = JapaneseCompanionOrBase(activeArchive);
            string archiveAssets = Path.Combine(assets, name);
            List<string> roots = new List<string> { assets };
            if (Directory.Exists(archiveAssets)) roots.Add(archiveAssets);
            bool archiveBuilt = false;
            foreach (string language in new[] { "ru", "en" })
            {
                string output = Path.Combine(cgDirectory, name + "." + language + ".CGF");
                if (BuildLocalizedArchive(archive, roots, output, language, false))
                {
                    builtArchives++;
                    archiveBuilt = true;
                }
            }
            if (archiveBuilt)
                EnsureJapaneseCompanion(activeArchive, archive);
        }
        int builtImages = BuildLocalizedLooseImages(
            cgDirectory, Path.Combine(assets, "_loose"));
        Console.WriteLine("Localized CGF archives built: " + builtArchives);
        Console.WriteLine("Localized loose IAF images built: " + builtImages);
        if (builtArchives == 0 && builtImages == 0)
            Console.WriteLine("No *.ru.png/*.ru.IAF or *.en.png/*.en.IAF resources matched a source resource.");
    }

    private static IEnumerable<string> OriginalResourceFiles(string directory, string extension)
    {
        return Directory.GetFiles(directory, "*" + extension, SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                return !stem.EndsWith(".jp", StringComparison.OrdinalIgnoreCase) &&
                    !stem.EndsWith(".ru", StringComparison.OrdinalIgnoreCase) &&
                    !stem.EndsWith(".en", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static string JapaneseCompanionOrBase(string active)
    {
        string directory = Path.GetDirectoryName(active);
        string name = Path.GetFileNameWithoutExtension(active);
        string japanese = Path.Combine(directory, name + ".jp" + Path.GetExtension(active));
        return File.Exists(japanese) ? japanese : active;
    }

    private static void EnsureJapaneseCompanion(string active, string source)
    {
        string directory = Path.GetDirectoryName(active);
        string name = Path.GetFileNameWithoutExtension(active);
        string japanese = Path.Combine(directory, name + ".jp" + Path.GetExtension(active));
        if (!File.Exists(japanese)) File.Copy(source, japanese, false);
    }

    private static int BuildLocalizedLooseImages(string cgDirectory, string assets)
    {
        if (!Directory.Exists(assets)) return 0;
        int built = 0;
        foreach (string language in new[] { "ru", "en" })
        {
            Dictionary<string, string> replacements = FindLocalizedImages(
                new[] { assets }, language);
            foreach (KeyValuePair<string, string> replacement in replacements)
            {
                string active = Path.Combine(cgDirectory, replacement.Key + ".IAF");
                string japanese = Path.Combine(cgDirectory, replacement.Key + ".jp.IAF");
                string source = File.Exists(japanese) ? japanese : active;
                if (!File.Exists(source))
                {
                    Console.Error.WriteLine(
                        "warning: localized loose image has no source IAF: " + replacement.Key);
                    continue;
                }
                byte[] bitmap = LocalizedImageToBmp(replacement.Value);
                string output = Path.Combine(
                    cgDirectory, replacement.Key + "." + language + ".IAF");
                File.WriteAllBytes(output, WrapBmp(bitmap));
                EnsureJapaneseCompanion(active, source);
                built++;
                Console.WriteLine("Built localized loose IAF: " + output);
            }
        }
        return built;
    }

    private static bool BuildLocalizedArchive(
        string sourcePath,
        IEnumerable<string> assetRoots,
        string output,
        string language,
        bool requireReplacement)
    {
        Dictionary<string, string> replacements = FindLocalizedImages(assetRoots, language);
        if (replacements.Count == 0)
        {
            if (requireReplacement)
                throw new InvalidDataException(
                    "No *." + language + ".png or *." + language + ".IAF resources were found.");
            return false;
        }

        byte[] source = File.ReadAllBytes(sourcePath);
        List<CgfRecord> records = ReadCgfIndex(source);
        ValidateArchiveOrder(sourcePath, records);
        Dictionary<string, byte[]> expected =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<byte[]> payloads = new List<byte[]>();
        foreach (CgfRecord record in records)
        {
            string replacement;
            if (!replacements.TryGetValue(record.Name, out replacement))
            {
                byte[] original = new byte[record.Size];
                Buffer.BlockCopy(source, record.Offset, original, 0, record.Size);
                payloads.Add(original);
                continue;
            }
            byte[] bitmap = LocalizedImageToBmp(replacement);
            expected[record.Name] = bitmap;
            payloads.Add(PackPayload(bitmap, "stored"));
            matched.Add(record.Name);
        }
        if (matched.Count == 0)
        {
            if (requireReplacement)
                throw new InvalidDataException(
                    "None of the localized resources exist in " + Path.GetFileName(sourcePath));
            return false;
        }

        string temporary = output + ".delta.tmp";
        try
        {
            WriteCgf(records, payloads, temporary);
            VerifyLocalized(temporary, records, expected);
            string parent = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (File.Exists(output)) File.Delete(output);
            File.Move(temporary, output);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        Console.WriteLine("Built localized UI archive with {0} replacement(s): {1}",
            matched.Count, output);
        return true;
    }

    private static Dictionary<string, string> FindLocalizedImages(
        IEnumerable<string> assetRoots, string language)
    {
        string suffixIaf = "." + language + ".iaf";
        string suffixPng = "." + language + ".png";
        Dictionary<string, string> replacements =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in assetRoots.Where(Directory.Exists))
        {
            foreach (string path in Directory.GetFiles(root, "*.*", SearchOption.TopDirectoryOnly))
            {
                string file = Path.GetFileName(path);
                string suffix = file.EndsWith(suffixPng, StringComparison.OrdinalIgnoreCase)
                    ? suffixPng
                    : file.EndsWith(suffixIaf, StringComparison.OrdinalIgnoreCase) ? suffixIaf : null;
                if (suffix == null) continue;
                string name = file.Substring(0, file.Length - suffix.Length);
                string previous;
                // A PNG is the editable source of truth when both companions exist.
                if (!replacements.TryGetValue(name, out previous) ||
                    path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    !previous.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    replacements[name] = path;
            }
        }
        return replacements;
    }

    private static byte[] LocalizedImageToBmp(string path)
    {
        if (path.EndsWith(".IAF", StringComparison.OrdinalIgnoreCase))
            return DecodeIafToBmp(path);
        using (FileStream input = File.OpenRead(path))
        using (MemoryStream output = new MemoryStream())
        {
            BitmapDecoder decoder = BitmapDecoder.Create(
                input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            System.Windows.Media.Imaging.BitmapSource source = decoder.Frames[0];
            System.Windows.Media.PixelFormat target = source.Format.BitsPerPixel == 32
                ? System.Windows.Media.PixelFormats.Bgra32
                : System.Windows.Media.PixelFormats.Bgr24;
            FormatConvertedBitmap bitmap = new FormatConvertedBitmap(source, target, null, 0.0);
            BmpBitmapEncoder encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(output);
            return output.ToArray();
        }
    }

    private static void ValidateArchiveOrder(string path, List<CgfRecord> records)
    {
        using (ArcView view = new ArcView(path))
        using (ArcFile archive = CgfFormat().TryOpen(view))
        {
            if (archive == null) throw new InvalidDataException("GARbro rejected " + path);
            List<Entry> entries = archive.Dir.ToList();
            if (entries.Count != records.Count)
                throw new InvalidDataException("GARbro and the raw CGF index disagree about entry count");
            for (int i = 0; i < records.Count; i++)
                if (!string.Equals(entries[i].Name, records[i].Name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("GARbro and the raw CGF index disagree about entry order");
        }
    }

    private static void VerifyLocalized(
        string path, List<CgfRecord> records, Dictionary<string, byte[]> expected)
    {
        ArchiveFormat format = CgfFormat();
        using (ArcView view = new ArcView(path))
        using (ArcFile archive = format.TryOpen(view))
        {
            if (archive == null) throw new InvalidDataException("GARbro rejected the localized CGF");
            List<Entry> entries = archive.Dir.ToList();
            if (entries.Count != records.Count)
                throw new InvalidDataException("Localized CGF entry count changed");
            for (int index = 0; index < records.Count; index++)
            {
                byte[] wanted;
                if (!expected.TryGetValue(records[index].Name, out wanted)) continue;
                byte[] actual;
                using (Stream stream = format.OpenEntry(archive, entries[index]))
                using (IBinaryStream binary = BinaryStream.FromStream(stream, records[index].Name + ".iaf"))
                    actual = DecodeIafToBmp(binary, records[index].Name + ".iaf");
                if (!BytesEqual(actual, wanted))
                    throw new InvalidDataException("Localized CGF verification failed for " + records[index].Name);
            }
        }
    }

    private static int Iaf(string[] arguments)
    {
        if (arguments.Length == 0) throw new ArgumentException("iaf needs wrap or unwrap.");
        string command = arguments[0].ToLowerInvariant();
        ParsedArguments parsed = Parse(arguments.Skip(1), new[] { "garbro-dir" }, new string[0], new string[0]);
        RequirePositionals(parsed, 2, "iaf " + command + " <input> <output>");
        string input = Path.GetFullPath(parsed.Positionals[0]);
        string output = Path.GetFullPath(parsed.Positionals[1]);
        EnsureParent(output);
        if (command == "wrap")
        {
            byte[] wrapped = WrapBmp(File.ReadAllBytes(input));
            File.WriteAllBytes(output, wrapped);
            Console.WriteLine("Wrote {0} bytes to {1}", wrapped.Length, output);
        }
        else if (command == "unwrap")
        {
            int width, height;
            using (IBinaryStream binary = BinaryStream.FromFile(input))
                DecodeIaf(binary, input, output, false, out width, out height);
            Console.WriteLine("Decoded {0}x{1} IAF to {2}", width, height, output);
        }
        else throw new ArgumentException("Unknown iaf command: " + command);
        return 0;
    }

    private static int Mhu(string[] arguments)
    {
        if (arguments.Length == 0) throw new ArgumentException("mhu needs encode or decode.");
        string command = arguments[0].ToLowerInvariant();
        ParsedArguments parsed = Parse(arguments.Skip(1), new[] { "mode" }, new string[0], new string[0]);
        RequirePositionals(parsed, 2, "mhu " + command + " <input> <output>");
        byte[] data = File.ReadAllBytes(parsed.Positionals[0]);
        byte[] result;
        if (command == "encode")
        {
            string mode = parsed.Value("mode", "stored");
            byte[] packed = PackPayload(data, mode);
            result = packed.Skip(4).ToArray();
        }
        else if (command == "decode")
        {
            byte[] container = new byte[data.Length + 4];
            Buffer.BlockCopy(data, 0, container, 4, data.Length);
            result = UnpackPayload(container).Item1;
        }
        else throw new ArgumentException("Unknown mhu command: " + command);
        EnsureParent(parsed.Positionals[1]);
        File.WriteAllBytes(parsed.Positionals[1], result);
        Console.WriteLine("Wrote {0} bytes to {1}", result.Length, Path.GetFullPath(parsed.Positionals[1]));
        return 0;
    }

    private static void RequirePositionals(ParsedArguments parsed, int count, string usage)
    {
        if (parsed.Positionals.Count != count) throw new ArgumentException("Usage: " + usage);
    }

    private static List<CgfRecord> ReadCgfIndex(byte[] data)
    {
        if (data.Length < 4) throw new InvalidDataException("Not a Delta CGF archive");
        int count = checked((int)ReadUInt32(data, 0));
        if (count == 0 || 4L + (long)count * EntryRecord > data.Length)
            throw new InvalidDataException("Not a Delta CGF archive");
        List<CgfRecord> records = new List<CgfRecord>();
        for (int index = 0; index < count; index++)
        {
            int start = 4 + index * EntryRecord;
            int length = 0;
            while (length < NameField && data[start + length] != 0) length++;
            uint value = ReadUInt32(data, start + NameField);
            records.Add(new CgfRecord
            {
                Name = Encoding.GetEncoding(932).GetString(data, start, length),
                Flagged = (value & IndexFlag) != 0,
                Offset = checked((int)(value & OffsetMask))
            });
        }
        for (int index = 0; index < records.Count; index++)
        {
            int end = index + 1 < records.Count ? records[index + 1].Offset : data.Length;
            if (records[index].Offset < 4 + count * EntryRecord || end < records[index].Offset || end > data.Length)
                throw new InvalidDataException("Invalid CGF entry offsets");
            records[index].Size = end - records[index].Offset;
        }
        return records;
    }

    private static string PayloadMode(byte[] data, int offset, int length)
    {
        if (length < 8) return "raw";
        uint packed = ReadUInt32(data, offset);
        uint sized = ReadUInt32(data, offset + 4);
        if (packed == 0 && sized == 0) return "raw";
        if ((sized & RleFlag) != 0) return "rle";
        if ((sized & StoredFlag) != 0) return "stored";
        return "lzss";
    }

    private static byte[] PackPayload(byte[] data, string mode)
    {
        if (string.Equals(mode, "raw", StringComparison.OrdinalIgnoreCase)) return data;
        byte[] body;
        uint sized;
        if (string.Equals(mode, "stored", StringComparison.OrdinalIgnoreCase))
        {
            body = data;
            sized = StoredFlag | checked((uint)data.Length);
        }
        else if (string.Equals(mode, "rle", StringComparison.OrdinalIgnoreCase))
        {
            body = CompressRle(data);
            sized = RleFlag | checked((uint)data.Length);
        }
        else if (string.Equals(mode, "lzss", StringComparison.OrdinalIgnoreCase))
        {
            body = CompressLzss(data);
            sized = checked((uint)data.Length);
        }
        else throw new ArgumentException("Unknown compression mode: " + mode);
        using (MemoryStream output = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(output))
        {
            writer.Write(checked((uint)body.Length));
            writer.Write(sized);
            writer.Write(body);
            return output.ToArray();
        }
    }

    private static Tuple<byte[], string> UnpackPayload(byte[] blob)
    {
        if (blob.Length < 8) return Tuple.Create(blob, "raw");
        uint packed = ReadUInt32(blob, 0);
        uint sized = ReadUInt32(blob, 4);
        if (packed == 0 && sized == 0) return Tuple.Create(blob, "raw");
        int available = Math.Max(0, Math.Min(checked((int)packed), blob.Length - 8));
        byte[] body = new byte[available];
        Buffer.BlockCopy(blob, 8, body, 0, available);
        int size = checked((int)(sized & SizeMask));
        if ((sized & RleFlag) != 0) return Tuple.Create(DecompressRle(body, size), "rle");
        if ((sized & StoredFlag) != 0) return Tuple.Create(body.Take(size).ToArray(), "stored");
        return Tuple.Create(DecompressLzss(body, checked((int)sized)), "lzss");
    }

    private static byte[] DecompressLzss(byte[] source, int size)
    {
        byte[] ring = new byte[RingSize];
        int cursor = RingSize - Window, position = 0, flags = 0;
        List<byte> output = new List<byte>(size);
        while (output.Count < size && position < source.Length)
        {
            flags >>= 1;
            if ((flags & 0x100) == 0) flags = source[position++] | 0xFF00;
            if ((flags & 1) != 0)
            {
                if (position >= source.Length) break;
                byte value = source[position++];
                output.Add(value); ring[cursor] = value; cursor = (cursor + 1) & (RingSize - 1);
            }
            else
            {
                if (position + 1 >= source.Length) break;
                int low = source[position++], high = source[position++];
                int start = low | ((high & 0xF0) << 4);
                int length = (high & 0x0F) + Threshold;
                for (int step = 0; step <= length && output.Count < size; step++)
                {
                    byte value = ring[(start + step) & (RingSize - 1)];
                    output.Add(value); ring[cursor] = value; cursor = (cursor + 1) & (RingSize - 1);
                }
            }
        }
        return output.ToArray();
    }

    private static byte[] CompressLzss(byte[] data)
    {
        byte[] ring = new byte[RingSize];
        int cursor = RingSize - Window, position = 0;
        List<byte> output = new List<byte>();
        while (position < data.Length)
        {
            int flags = 0;
            List<byte> chunk = new List<byte>();
            for (int flag = 0; flag < 8 && position < data.Length; flag++)
            {
                int bestLength = 0, bestStart = 0;
                int limit = Math.Min(Window - 1, data.Length - position);
                if (limit > Threshold)
                {
                    for (int start = 0; start < RingSize; start++)
                    {
                        int length = 0;
                        while (length < limit)
                        {
                            int ringIndex = (start + length) & (RingSize - 1);
                            int offset = (ringIndex - cursor) & (RingSize - 1);
                            byte value = offset < length ? data[position + offset] : ring[ringIndex];
                            if (value != data[position + length]) break;
                            length++;
                        }
                        if (length > bestLength)
                        {
                            bestLength = length; bestStart = start;
                            if (bestLength == limit) break;
                        }
                    }
                }
                int emitted;
                if (bestLength > Threshold)
                {
                    chunk.Add((byte)(bestStart & 0xFF));
                    chunk.Add((byte)(((bestStart >> 4) & 0xF0) |
                        ((bestLength - Threshold - 1) & 0x0F)));
                    emitted = bestLength;
                }
                else
                {
                    flags |= 1 << flag; chunk.Add(data[position]); emitted = 1;
                }
                for (int step = 0; step < emitted; step++)
                {
                    ring[cursor] = data[position + step];
                    cursor = (cursor + 1) & (RingSize - 1);
                }
                position += emitted;
            }
            output.Add((byte)flags); output.AddRange(chunk);
        }
        return output.ToArray();
    }

    private static byte[] DecompressRle(byte[] source, int size)
    {
        List<byte> output = new List<byte>(size);
        int position = 0;
        while (output.Count < size && position < source.Length)
        {
            int count = source[position++];
            if (count != 0)
            {
                if (position >= source.Length) break;
                byte value = source[position++];
                for (int i = 0; i < count; i++) output.Add(value);
            }
            else
            {
                if (position >= source.Length) break;
                int literal = source[position++];
                for (int i = 0; i < literal && position < source.Length; i++)
                    output.Add(source[position++]);
            }
        }
        return output.Take(size).ToArray();
    }

    private static byte[] CompressRle(byte[] data)
    {
        List<byte> output = new List<byte>();
        int position = 0;
        while (position < data.Length)
        {
            int run = 1;
            while (run < 255 && position + run < data.Length && data[position + run] == data[position]) run++;
            if (run > 1)
            {
                output.Add((byte)run); output.Add(data[position]); position += run; continue;
            }
            int start = position;
            while (position < data.Length && position - start < 255 &&
                   !(position + 1 < data.Length && data[position + 1] == data[position])) position++;
            int length = Math.Max(1, position - start);
            output.Add(0); output.Add((byte)length);
            for (int i = 0; i < length; i++) output.Add(data[start + i]);
            position = start + length;
        }
        return output.ToArray();
    }

    private static void WriteCgfFromManifest(object[] entries, List<byte[]> payloads, string output)
    {
        List<CgfRecord> records = new List<CgfRecord>();
        foreach (object value in entries)
        {
            Dictionary<string, object> item = (Dictionary<string, object>)value;
            records.Add(new CgfRecord
            {
                Name = Convert.ToString(item["name"]),
                Flagged = item.ContainsKey("flagged") && Convert.ToBoolean(item["flagged"])
            });
        }
        WriteCgf(records, payloads, output);
    }

    private static void WriteCgf(List<CgfRecord> records, List<byte[]> payloads, string output)
    {
        EnsureParent(output);
        using (FileStream stream = File.Create(output))
        using (BinaryWriter writer = new BinaryWriter(stream, Encoding.GetEncoding(932)))
        {
            writer.Write(records.Count);
            int offset = 4 + records.Count * EntryRecord;
            for (int index = 0; index < records.Count; index++)
            {
                byte[] name = Encoding.GetEncoding(932).GetBytes(records[index].Name);
                if (name.Length >= NameField)
                    throw new InvalidDataException("Entry name does not fit in 28 bytes: " + records[index].Name);
                writer.Write(name);
                writer.Write(new byte[NameField - name.Length]);
                uint value = checked((uint)offset) | (records[index].Flagged ? IndexFlag : 0);
                writer.Write(value);
                offset += payloads[index].Length;
            }
            foreach (byte[] payload in payloads) writer.Write(payload);
        }
    }

    internal static byte[] WrapBmp(byte[] bitmap)
    {
        if (bitmap.Length < 2 || bitmap[0] != (byte)'B' || bitmap[1] != (byte)'M')
            throw new InvalidDataException("Expected a BMP file");
        using (MemoryStream output = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(output))
        {
            writer.Write((byte)0); writer.Write(bitmap.Length); writer.Write(bitmap);
            writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            writer.Write(StoredFlag | checked((uint)bitmap.Length));
            return output.ToArray();
        }
    }

    private static byte[] DecodeIafToBmp(string path)
    {
        using (IBinaryStream binary = BinaryStream.FromFile(path))
            return DecodeIafToBmp(binary, Path.GetFileName(path));
    }

    private static byte[] DecodeIafToBmp(IBinaryStream binary, string name)
    {
        IafFormat format = new IafFormat();
        ImageMetaData metadata = format.ReadMetaData(binary);
        if (metadata == null) throw new InvalidDataException("GARbro IafFormat rejected " + name);
        binary.Position = 0;
        ImageData image = format.Read(binary, metadata);
        FormatConvertedBitmap bitmap = new FormatConvertedBitmap(
            image.Bitmap, System.Windows.Media.PixelFormats.Bgr24, null, 0.0);
        BmpBitmapEncoder encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (MemoryStream output = new MemoryStream())
        {
            encoder.Save(output);
            return output.ToArray();
        }
    }

    private static void DecodeIafStream(
        Stream stream, string name, string output, bool png, out int width, out int height)
    {
        using (IBinaryStream binary = BinaryStream.FromStream(stream, name))
            DecodeIaf(binary, name, output, png, out width, out height);
    }

    private static void DecodeIaf(
        IBinaryStream binary, string name, string output, bool png, out int width, out int height)
    {
        IafFormat format = new IafFormat();
        ImageMetaData metadata = format.ReadMetaData(binary);
        if (metadata == null) throw new InvalidDataException("GARbro IafFormat rejected " + name);
        binary.Position = 0;
        ImageData image = format.Read(binary, metadata);
        System.Windows.Media.Imaging.BitmapSource bitmap = image.Bitmap;
        BitmapEncoder encoder;
        if (png) encoder = new PngBitmapEncoder();
        else
        {
            bitmap = new FormatConvertedBitmap(
                image.Bitmap, System.Windows.Media.PixelFormats.Bgr24, null, 0.0);
            encoder = new BmpBitmapEncoder();
        }
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        EnsureParent(output);
        using (FileStream destination = File.Create(output)) encoder.Save(destination);
        width = checked((int)metadata.Width); height = checked((int)metadata.Height);
    }

    private static byte[] ReadAll(Stream stream)
    {
        using (MemoryStream output = new MemoryStream())
        {
            stream.CopyTo(output); return output.ToArray();
        }
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length) throw new InvalidDataException("Truncated data");
        return (uint)(data[offset] | data[offset + 1] << 8 |
            data[offset + 2] << 16 | data[offset + 3] << 24);
    }

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }

    private static Dictionary<string, object> ReadJson(string path)
    {
        return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(
            File.ReadAllText(path, Encoding.UTF8));
    }

    private static void WriteJson(string path, object value)
    {
        File.WriteAllText(path, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
    }

    private static void EnsureParent(string path)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
    }

}

internal static class DeltaFitServer
{
    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct LogFont
    {
        public int Height;
        public int Width;
        public int Escapement;
        public int Orientation;
        public int Weight;
        public byte Italic;
        public byte Underline;
        public byte StrikeOut;
        public byte CharSet;
        public byte OutPrecision;
        public byte ClipPrecision;
        public byte Quality;
        public byte PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Abc
    {
        public int A;
        public uint B;
        public int C;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontIndirectW(ref LogFont font);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetTextFaceW(IntPtr dc, int count, StringBuilder name);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetCharABCWidthsW(IntPtr dc, uint first, uint last, out Abc widths);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(
        IntPtr dc, string text, int count, out NativeSize size);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    public static int Run(string[] arguments)
    {
        if (arguments.Length == 0 ||
            !string.Equals(arguments[0], "server", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Usage: fit server --font-height N --letter-spacing N --face NAME");
        int height = 20;
        int spacing = 0;
        string face = "Arial Narrow";
        for (int index = 1; index < arguments.Length; index++)
        {
            string name = arguments[index];
            if (++index >= arguments.Length)
                throw new ArgumentException(name + " needs a value");
            string value = arguments[index];
            if (name == "--font-height") height = int.Parse(value, CultureInfo.InvariantCulture);
            else if (name == "--letter-spacing") spacing = int.Parse(value, CultureInfo.InvariantCulture);
            else if (name == "--face") face = value;
            else throw new ArgumentException("Unknown fit option: " + name);
        }

        using (Measurer measurer = new Measurer(height, spacing, face))
        {
            Console.WriteLine("READY");
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                if (line == "QUIT") break;
                int separator = line.IndexOf('\t');
                if (separator < 0)
                    throw new InvalidDataException("Fit request needs minimum-step and Base64 text.");
                int minimum = int.Parse(line.Substring(0, separator), CultureInfo.InvariantCulture);
                string text = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(separator + 1)));
                Console.WriteLine(measurer.Width(text, minimum).ToString(CultureInfo.InvariantCulture));
            }
        }
        return 0;
    }

    private sealed class Measurer : IDisposable
    {
        private readonly IntPtr dc;
        private readonly IntPtr font;
        private readonly IntPtr previous;
        private readonly int letterSpacing;
        private readonly Dictionary<char, int> cache = new Dictionary<char, int>();

        public Measurer(int height, int spacing, string face)
        {
            letterSpacing = spacing;
            dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed.");
            LogFont info = new LogFont
            {
                Height = height,
                Weight = 400,
                CharSet = 1,
                FaceName = face
            };
            font = CreateFontIndirectW(ref info);
            if (font == IntPtr.Zero)
            {
                DeleteDC(dc);
                throw new InvalidOperationException("CreateFontIndirectW failed.");
            }
            previous = SelectObject(dc, font);
            StringBuilder actual = new StringBuilder(64);
            GetTextFaceW(dc, actual.Capacity, actual);
            if (!string.Equals(actual.ToString(), face, StringComparison.OrdinalIgnoreCase))
            {
                Dispose();
                throw new InvalidOperationException(
                    face + " is not installed; GDI substituted '" + actual + "'.");
            }
        }

        public int Width(string text, int minimumStep)
        {
            int width = 0;
            foreach (char character in text)
                width += Math.Max(Step(character), minimumStep) + letterSpacing;
            return width;
        }

        private int Step(char character)
        {
            int cached;
            if (cache.TryGetValue(character, out cached)) return cached;
            Abc abc;
            int step;
            if (GetCharABCWidthsW(dc, character, character, out abc))
            {
                int advance = abc.A + (int)abc.B + abc.C;
                int inkOffset = Math.Max(-abc.A, 0);
                int inkRight = abc.A + (int)abc.B + inkOffset;
                step = Math.Max(advance, inkRight + 1);
            }
            else
            {
                NativeSize size;
                step = GetTextExtentPoint32W(dc, character.ToString(), 1, out size)
                    ? size.Width : 1;
            }
            step = Math.Max(step, 1);
            cache[character] = step;
            return step;
        }

        public void Dispose()
        {
            if (dc != IntPtr.Zero && previous != IntPtr.Zero) SelectObject(dc, previous);
            if (font != IntPtr.Zero) DeleteObject(font);
            if (dc != IntPtr.Zero) DeleteDC(dc);
        }
    }
}

internal static class DeltaMenuResources
{
    private static readonly Regex Allowed = new Regex(
        @"[\u3000-\u30ff\u3400-\u9fffA-Za-z0-9_ Ａ-Ｚａ-ｚ０-９ー・/+'""()!?.,:;…+\-]{2,100}",
        RegexOptions.CultureInvariant);
    private static readonly string[] Hints =
    {
        "ファイル", "ﾌｧｲﾙ", "メッセージ", "セーブ", "ロード", "クイック",
        "スキップ", "オプション", "画面", "ヘルプ", "終了", "メニュー",
        "設定", "音響", "操作", "BMP", "Route", "web site"
    };

    public static int Run(string[] arguments)
    {
        if (arguments.Length < 3)
            throw new ArgumentException(
                "Usage: menu extract EXE CATALOG | menu runtime CATALOG OUTPUT --target-lang RU|EN");
        string command = arguments[0].ToLowerInvariant();
        if (command == "extract") return Extract(arguments[1], arguments[2]);
        if (command == "runtime")
        {
            string language = Option(arguments, "--target-lang");
            if (language != "RU" && language != "EN")
                throw new ArgumentException("Menu target language must be RU or EN.");
            return Runtime(arguments[1], arguments[2], language.ToLowerInvariant());
        }
        throw new ArgumentException("Unknown menu command: " + command);
    }

    private static int Extract(string executable, string output)
    {
        Encoding utf16 = Encoding.GetEncoding(
            1200, EncoderFallback.ReplacementFallback, new DecoderReplacementFallback(""));
        string decoded = utf16.GetString(File.ReadAllBytes(executable));
        SortedSet<string> sources = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string chunk in decoded.Split('\0'))
        {
            foreach (Match match in Allowed.Matches(chunk))
            {
                string value = NormalizeCandidate(match.Value.Trim());
                if (value.Length == 0 || value.Length > 80) continue;
                if (!HasJapanese(value) && !Hints.Any(hint => value.Contains(hint))) continue;
                sources.Add(value);
            }
        }

        Dictionary<string, object> old = File.Exists(output) ? ReadCatalog(output) : null;
        Dictionary<string, Dictionary<string, object>> previous = PreviousEntries(old);
        List<object> entries = new List<object>();
        foreach (string source in sources)
        {
            Dictionary<string, object> item;
            if (!previous.TryGetValue(source, out item))
                item = new Dictionary<string, object>(StringComparer.Ordinal);
            else
                item = new Dictionary<string, object>(item, StringComparer.Ordinal);
            item["source"] = source;
            if (!item.ContainsKey("ru")) item["ru"] = "";
            if (!item.ContainsKey("en")) item["en"] = "";
            entries.Add(item);
        }
        Dictionary<string, object> catalog = new Dictionary<string, object>();
        catalog["format"] = 1;
        catalog["entries"] = entries;
        WritePrettyJson(output, catalog);
        Console.WriteLine("Menu candidates: " + sources.Count);
        Console.WriteLine("Catalog: " + Path.GetFullPath(output));
        return 0;
    }

    private static int Runtime(string catalogPath, string output, string language)
    {
        Dictionary<string, object> catalog = ReadCatalog(catalogPath);
        object rawEntries;
        if (!catalog.TryGetValue("entries", out rawEntries) || !(rawEntries is IEnumerable))
            throw new InvalidDataException("Menu catalog must contain an entries array.");
        List<string> lines = new List<string>();
        foreach (object raw in (IEnumerable)rawEntries)
        {
            Dictionary<string, object> item = raw as Dictionary<string, object>;
            if (item == null) continue;
            string source = Text(item, "source").Trim();
            string translation = Text(item, language).Trim();
            if (source.Length == 0 || translation.Length == 0) continue;
            lines.Add(Base64(source) + "\t" + Base64(translation));
        }
        EnsureParent(output);
        string text = string.Join(Environment.NewLine, lines.ToArray());
        if (lines.Count != 0) text += Environment.NewLine;
        File.WriteAllText(output, text, new UTF8Encoding(false));
        Console.WriteLine("Runtime menu map: " + Path.GetFullPath(output));
        return 0;
    }

    private static string Option(string[] arguments, string name)
    {
        for (int index = 3; index + 1 < arguments.Length; index++)
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                return arguments[index + 1].ToUpperInvariant();
        throw new ArgumentException(name + " is required.");
    }

    private static string NormalizeCandidate(string value)
    {
        int tab = value.IndexOf('\t');
        if (tab >= 0) value = value.Substring(0, tab);
        value = value.Trim();
        if (Hints.Any(hint => value.StartsWith(hint, StringComparison.Ordinal))) return value;
        if (value.Length > 1 && Hints.Any(
            hint => value.Substring(1).StartsWith(hint, StringComparison.Ordinal)))
            return value.Substring(1);
        return "";
    }

    private static bool HasJapanese(string value)
    {
        foreach (char character in value)
            if ((character >= '\u3040' && character <= '\u30ff') ||
                (character >= '\u3400' && character <= '\u4dbf') ||
                (character >= '\u4e00' && character <= '\u9fff'))
                return true;
        return false;
    }

    private static Dictionary<string, Dictionary<string, object>> PreviousEntries(
        Dictionary<string, object> catalog)
    {
        Dictionary<string, Dictionary<string, object>> result =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        if (catalog == null) return result;
        object rawEntries;
        if (!catalog.TryGetValue("entries", out rawEntries) || !(rawEntries is IEnumerable))
            return result;
        foreach (object raw in (IEnumerable)rawEntries)
        {
            Dictionary<string, object> item = raw as Dictionary<string, object>;
            if (item == null) continue;
            string source = Text(item, "source");
            if (source.Length != 0) result[source] = item;
        }
        return result;
    }

    private static Dictionary<string, object> ReadCatalog(string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8).TrimStart('\ufeff');
        Dictionary<string, object> value =
            new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
        object entries;
        if (value == null || !value.TryGetValue("entries", out entries) || !(entries is IEnumerable))
            throw new InvalidDataException("Menu catalog must contain an entries array.");
        return value;
    }

    private static string Text(Dictionary<string, object> item, string name)
    {
        object value;
        return item.TryGetValue(name, out value) && value != null ? Convert.ToString(value) : "";
    }

    private static string Base64(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static void EnsureParent(string path)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
    }

    private static void WritePrettyJson(string path, object value)
    {
        EnsureParent(path);
        string compact = new JavaScriptSerializer().Serialize(value);
        StringBuilder output = new StringBuilder(compact.Length + 256);
        int indent = 0;
        bool quoted = false;
        bool escaped = false;
        foreach (char character in compact)
        {
            if (quoted)
            {
                output.Append(character);
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') quoted = false;
                continue;
            }
            if (character == '"')
            {
                quoted = true;
                output.Append(character);
            }
            else if (character == '{' || character == '[')
            {
                output.Append(character).Append(Environment.NewLine);
                indent++;
                output.Append(' ', indent * 2);
            }
            else if (character == '}' || character == ']')
            {
                output.Append(Environment.NewLine);
                indent--;
                output.Append(' ', indent * 2).Append(character);
            }
            else if (character == ',')
            {
                output.Append(character).Append(Environment.NewLine).Append(' ', indent * 2);
            }
            else if (character == ':') output.Append(": ");
            else output.Append(character);
        }
        output.Append(Environment.NewLine);
        File.WriteAllText(path, output.ToString(), new UTF8Encoding(false));
    }
}
