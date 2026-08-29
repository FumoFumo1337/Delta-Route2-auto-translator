using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Reflection;
using System.Windows.Forms;
using PythonRuntime = DeltaPython.PythonRuntime;
using Context = DeltaProject;

// The same binary is also installed beside a game as DeltaLauncher.exe,
// so the description has to cover both roles.
[assembly: AssemblyTitle("Delta VN Translator")]
[assembly: AssemblyDescription("Translation toolset for Delta/Route2 visual novels, and the language picker installed beside a game as DeltaLauncher.exe.")]

internal static class DeltaDiagnosticLog
{
    private static readonly object Sync = new object();
    private static readonly string LogPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "delta_translator.log");

    internal static void Start()
    {
        StringBuilder header = new StringBuilder();
        header.AppendLine();
        header.Append("=== Delta VN Translator session ")
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .AppendLine(" ===");
        header.Append("Executable: ").AppendLine(Application.ExecutablePath);
        header.Append("Version: ").AppendLine(
            Assembly.GetExecutingAssembly().GetName().Version.ToString());
        header.Append("Working directory: ").AppendLine(Environment.CurrentDirectory);
        header.Append("OS: ").AppendLine(Environment.OSVersion.ToString());
        Append(header.ToString());
    }

    internal static void Write(string message)
    {
        string entry = DateTime.Now.ToString(
            "yyyy-MM-dd HH:mm:ss.fff ", CultureInfo.InvariantCulture) + message + Environment.NewLine;
        Append(entry);
    }

    internal static void Write(Exception error)
    {
        Write("Exception:" + Environment.NewLine + error);
    }

    private static void Append(string text)
    {
        try
        {
            lock (Sync)
                File.AppendAllText(LogPath, text, new UTF8Encoding(false));
        }
        catch
        {
            // Diagnostics must never make the translator itself fail.
        }
    }
}

internal sealed class DeltaTranslatorForm : Form
{
    private readonly string toolsDirectory;
    private readonly string settingsPath;
    private readonly TextBox gameDirectoryBox;
    private readonly TextBox executableBox;
    private readonly TextBox sourceBox;
    private readonly TextBox tokenBox;
    private readonly ComboBox profileBox;
    private readonly ComboBox languageBox;
    // One project setting seen from several pages. Translating a game into
    // both languages means walking the pipeline twice, and the pages that act
    // on one language carry the picker so the second walk does not have to
    // start back on Project setup.
    private readonly List<ComboBox> languageBoxes = new List<ComboBox>();
    private bool languageSyncInProgress;
    private readonly NumericUpDown dialogLimitBox;
    private readonly CheckBox overwriteBox;
    private readonly TextBox logBox;
    private readonly Label statusLabel;
    private readonly Label usageLabel;
    private readonly Label dialogWindowsLabel;
    private readonly TextBox proofreadWarningLabel;
    private readonly TextBox overlayWarningLabel;
    private readonly Label pythonStatusLabel;
    private readonly Button checkTokenButton;
    private readonly Label tokenUsageLabel;
    private readonly LinkLabel extractWorkbookLink;
    private readonly LinkLabel proofreadRulesLink;
    private readonly LinkLabel overlayOutputLink;
    private readonly Label localizedArchivesStatusLabel;
    private readonly Label menuStatusLabel;
    private readonly LinkLabel menuCatalogLink;
    private readonly LinkLabel launcherOutputLink;
    private readonly Button openExtractButton;
    private readonly Button openOverlayButton;
    private readonly Button openLocalizedArchivesButton;
    private readonly Button openLauncherButton;
    private readonly Button stopButton;
    private readonly ToolTip tips;
    private readonly List<Button> actionButtons = new List<Button>();
    private readonly List<Button> navigationButtons = new List<Button>();
    private readonly Dictionary<Button, Control> navigationPages = new Dictionary<Button, Control>();
    private readonly Panel pageHost;
    private Button generateLauncherButton;
    private Button extractLocalizedArchivesButton;
    private Button buildLocalizedArchivesButton;
    private Button activeNavigationButton;
    private bool launcherAvailable;
    private string pythonExecutable;
    private Version pythonVersion;
    private string pythonDetectionMessage;
    private long? estimateCharacters;
    private long? estimateAvailableCharacters;
    private string estimateAvailabilityMessage;
    private int estimateRequestVersion;
    private readonly object cancellationSync = new object();
    private string activeCancelFile;
    private volatile bool translationStopRequested;
    private volatile bool runHasWarning;

    private const string DeepLAccountUrl = "https://www.deepl.com/en/your-account/keys";
    // Both are measured for one language, so switching language puts them back.
    private const string EstimateUncalculated = "API estimate: not calculated";
    private const string DialogWindowsUncalculated = "Dialog windows sent: not calculated";
    private sealed class TranslatorSettings
    {
        public int ProfileIndex;
        public int LanguageIndex;
        public string GameDirectory;
        public string Executable;
        public string SourceArchive;
        public string DeepLTokenProtected;
    }

    private sealed class CommandSpec
    {
        public string Arguments;
        // The step may call DeepL, so forward the token if one was entered.
        // Whether the call actually happens depends on the cache, which is why
        // this is not a precondition.
        public bool UsesDeepL;
        public bool NativeResource;

        public CommandSpec(string arguments, bool usesDeepL, bool nativeResource = false)
        {
            Arguments = arguments;
            UsesDeepL = usesDeepL;
            NativeResource = nativeResource;
        }
    }

    public DeltaTranslatorForm()
    {
        toolsDirectory = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')).FullName;
        settingsPath = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory,
            "delta_translator.settings.json");
        Text = "Delta VN Translator";
        ClientSize = new Size(980, 680);
        MinimumSize = new Size(860, 600);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(12, 13, 16);

        try
        {
            string iconPath = Path.Combine(toolsDirectory, "DeltaTranslator.ico");
            Icon = File.Exists(iconPath)
                ? new Icon(iconPath)
                : Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
        }

        tips = new ToolTip
        {
            AutoPopDelay = 9000,
            InitialDelay = 300,
            ReshowDelay = 150,
            ShowAlways = true
        };

        profileBox = NewComboBox();
        profileBox.Items.AddRange(new object[] { "Auto-detect", "Reika / RSA", "Generic Delta" });
        profileBox.SelectedIndex = 0;
        languageBox = NewLanguageBox();
        gameDirectoryBox = NewPathBox("Game folder");
        executableBox = NewPathBox("Game executable");
        sourceBox = NewPathBox("Source archive");
        tokenBox = NewPathBox("DeepL API token");
        tokenBox.UseSystemPasswordChar = true;
        dialogLimitBox = NewNumberBox();
        overwriteBox = new CheckBox
        {
            Text = "Translate again and ignore cache",
            AutoSize = true,
            Checked = false,
            ForeColor = Color.White,
            BackColor = Color.Transparent
        };
        LoadSettings();

        PythonRuntime detectedPython = DeltaPython.Detect();
        if (detectedPython == null)
        {
            pythonExecutable = string.Empty;
            pythonVersion = null;
            pythonDetectionMessage = "Python 3.10 or newer was not found in system PATH, the Windows launcher, or HOME.";
        }
        else
        {
            pythonExecutable = detectedPython.Executable;
            pythonVersion = detectedPython.Version;
            pythonDetectionMessage = pythonVersion.CompareTo(DeltaPython.MinimumVersion) >= 0
                ? "Python " + pythonVersion + " detected via " + detectedPython.Source + ": " + pythonExecutable
                : "Python " + pythonVersion + " was found via " + detectedPython.Source +
                    ", but Python " + DeltaPython.MinimumVersion + " or newer is required.";
        }
        pythonStatusLabel = new Label
        {
            Text = pythonDetectionMessage,
            AutoSize = false,
            Width = 540,
            Height = 42,
            ForeColor = HasCompatiblePython
                ? Color.FromArgb(121, 206, 143)
                : Color.FromArgb(238, 151, 91),
            Margin = new Padding(0, 8, 0, 0)
        };

        Panel header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 92,
            BackColor = Color.Black
        };
        Label title = new Label
        {
            Text = "Delta VN Translator",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 19F),
            AutoSize = true,
            Location = new Point(24, 10)
        };
        Label subtitle = new Label
        {
            Text = "Extract, translate, proofread, and build a Delta overlay",
            ForeColor = Color.FromArgb(207, 222, 237),
            AutoSize = true,
            Location = new Point(27, 48)
        };
        header.Controls.Add(title);
        header.Controls.Add(subtitle);

        TableLayoutPanel projectForm = NewFormTable(6, 500, 250);
        AddRow(projectForm, 0, "Profile", profileBox, null);
        AddRow(projectForm, 1, "Language", languageBox, null);
        AddRow(projectForm, 2, "Game folder", gameDirectoryBox, BrowseButton(gameDirectoryBox, true), true);
        AddRow(projectForm, 3, "Executable", executableBox, BrowseButton(executableBox, false), true);
        AddRow(projectForm, 4, "Source archive", sourceBox, BrowseButton(sourceBox, false), true);
        AddRow(projectForm, 5, "DeepL token", tokenBox, TokenHelpButton());

        FlowLayoutPanel projectPage = CreatePage(
            "Project setup",
            "Shared engine, language, source, and API settings. Python is detected automatically.");
        projectPage.Controls.Add(pythonStatusLabel);
        projectPage.Controls.Add(projectForm);
        checkTokenButton = NewDarkButton("Check token", 180);
        checkTokenButton.Height = 34;
        checkTokenButton.Margin = new Padding(0, 10, 0, 0);
        checkTokenButton.Click += delegate { CheckDeepLToken(); };
        tips.SetToolTip(checkTokenButton, "Validate the saved key and read the current DeepL API character usage.");
        projectPage.Controls.Add(checkTokenButton);
        tokenUsageLabel = new Label
        {
            Text = "Token usage has not been checked.",
            AutoSize = false,
            Width = 540,
            Height = 46,
            ForeColor = Color.FromArgb(150, 154, 162),
            Margin = new Padding(0, 8, 0, 0)
        };
        projectPage.Controls.Add(tokenUsageLabel);

        FlowLayoutPanel extractPage = CreatePage(
            "Extract text",
            "Read the selected Delta archive and create the editable source workbook.");
        AddPageAction(extractPage, "Extract text", delegate { RunSingle(BuildExtract()); });
        extractPage.Controls.Add(new Label
        {
            Text = "Output workbook",
            AutoSize = false,
            Width = 540,
            Height = 28,
            ForeColor = Color.FromArgb(190, 194, 202),
            Font = new Font("Segoe UI Semibold", 10F),
            Margin = new Padding(0, 18, 0, 0)
        });
        extractWorkbookLink = new LinkLabel
        {
            Text = "The source workbook has not been created yet.",
            AutoSize = false,
            Width = 540,
            Height = 42,
            AutoEllipsis = true,
            LinkColor = Color.FromArgb(214, 91, 157),
            ActiveLinkColor = Color.FromArgb(244, 138, 194),
            VisitedLinkColor = Color.FromArgb(214, 91, 157),
            DisabledLinkColor = Color.FromArgb(125, 129, 138),
            LinkBehavior = LinkBehavior.HoverUnderline,
            Enabled = false,
            Margin = new Padding(0, 2, 0, 4)
        };
        extractWorkbookLink.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs args)
        {
            OpenExtractWorkbook();
        };
        extractPage.Controls.Add(extractWorkbookLink);
        openExtractButton = NewDarkButton("Open in Explorer", 210);
        openExtractButton.Height = 34;
        openExtractButton.Margin = new Padding(0, 10, 0, 0);
        openExtractButton.Click += delegate { RevealExtractWorkbook(); };
        extractPage.Controls.Add(openExtractButton);
        openExtractButton.Enabled = false;

        usageLabel = new Label
        {
            Text = EstimateUncalculated,
            AutoSize = true,
            ForeColor = Color.FromArgb(190, 194, 202),
            Margin = new Padding(3, 12, 3, 4)
        };
        dialogWindowsLabel = new Label
        {
            Text = DialogWindowsUncalculated,
            AutoSize = true,
            ForeColor = Color.FromArgb(190, 194, 202),
            Margin = new Padding(3, 0, 3, 4)
        };

        TableLayoutPanel translateSettings = NewFormTable(3, 500, 130);
        AddRow(translateSettings, 0, "Language", NewLanguageBox(), null);
        AddRow(translateSettings, 1, "Dialog windows", dialogLimitBox, NewHintLabel("0 = all remaining windows"));
        AddRow(translateSettings, 2, "Existing translations", overwriteBox, null);
        FlowLayoutPanel translatePage = CreatePage(
            "Translate",
            "Translate empty cells as complete dialog windows with the DeepL key from Project setup.");
        translatePage.Controls.Add(translateSettings);
        FlowLayoutPanel translateActions = new FlowLayoutPanel
        {
            Width = 540,
            // 8 of margin above a 34-pixel button; the rest was empty strip,
            // and this page has no height to spend on one.
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0),
            BackColor = Color.FromArgb(12, 13, 16)
        };
        Button estimateButton = AddPageAction(translateActions, "Calculate estimate", delegate { CalculateEstimate(); });
        estimateButton.Width = 190;
        estimateButton.Margin = new Padding(0, 8, 10, 0);
        Button translateButton = AddPageAction(translateActions, "Translate", delegate { RunSingle(BuildTranslate()); });
        translateButton.Width = 150;
        translateButton.Margin = new Padding(0, 8, 10, 0);
        stopButton = NewDarkButton("Stop", 100);
        stopButton.Height = 34;
        stopButton.Margin = new Padding(0, 8, 0, 0);
        stopButton.Enabled = false;
        stopButton.Click += delegate { RequestTranslationStop(); };
        translateActions.Controls.Add(stopButton);
        translatePage.Controls.Add(translateActions);
        translatePage.Controls.Add(usageLabel);
        translatePage.Controls.Add(dialogWindowsLabel);
        TableLayoutPanel translationLog = new TableLayoutPanel
        {
            Width = 540,
            Height = 190,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.FromArgb(12, 13, 16),
            Margin = new Padding(0, 18, 0, 0)
        };
        translationLog.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        translationLog.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        translationLog.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        translationLog.Controls.Add(new Label
        {
            Text = "Translation output",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(190, 194, 202),
            Font = new Font("Segoe UI Semibold", 10F),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(28, 35, 43),
            ForeColor = Color.FromArgb(225, 234, 242),
            Font = new Font("Consolas", 9F),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0)
        };
        translationLog.Controls.Add(logBox, 0, 1);
        translatePage.Controls.Add(translationLog);

        FlowLayoutPanel proofreadPage = CreatePage(
            "Proofread",
            "Apply the selected profile rules and write a separate proofread workbook.");
        TableLayoutPanel proofreadSettings = NewFormTable(1, 500, 50);
        AddRow(proofreadSettings, 0, "Language", NewLanguageBox(), null);
        proofreadPage.Controls.Add(proofreadSettings);
        AddPageAction(proofreadPage, "Apply proofread rules", delegate { RunSingle(BuildProofread()); });
        proofreadWarningLabel = NewRunWarningTextBox();
        proofreadPage.Controls.Add(proofreadWarningLabel);
        proofreadPage.Controls.Add(new Label
        {
            Text = "Project rules file",
            AutoSize = false,
            Width = 540,
            Height = 28,
            ForeColor = Color.FromArgb(190, 194, 202),
            Font = new Font("Segoe UI Semibold", 10F),
            Margin = new Padding(0, 18, 0, 0)
        });
        proofreadRulesLink = new LinkLabel
        {
            Text = "Complete Project setup to create a rules file.",
            AutoSize = false,
            Width = 540,
            Height = 42,
            AutoEllipsis = true,
            LinkColor = Color.FromArgb(214, 91, 157),
            ActiveLinkColor = Color.FromArgb(244, 138, 194),
            VisitedLinkColor = Color.FromArgb(214, 91, 157),
            DisabledLinkColor = Color.FromArgb(125, 129, 138),
            LinkBehavior = LinkBehavior.HoverUnderline,
            Enabled = false,
            Margin = new Padding(0, 2, 0, 4)
        };
        proofreadRulesLink.LinkClicked += delegate { OpenProofreadRules(); };
        proofreadPage.Controls.Add(proofreadRulesLink);
        proofreadPage.Controls.Add(new Label
        {
            Text = "The link creates a commented JSON template for this game. Empty rules leave the workbook unchanged.",
            AutoSize = false,
            Width = 540,
            Height = 42,
            ForeColor = Color.FromArgb(145, 151, 162),
            Margin = new Padding(0, 0, 0, 0)
        });

        FlowLayoutPanel overlayPage = CreatePage(
            "Build overlay",
            "Compile the current language workbook into the runtime translation overlay.");
        TableLayoutPanel overlaySettings = NewFormTable(1, 500, 50);
        AddRow(overlaySettings, 0, "Language", NewLanguageBox(), null);
        overlayPage.Controls.Add(overlaySettings);
        AddPageAction(overlayPage, "Build overlay", delegate { RunSingle(BuildOverlay()); });
        overlayWarningLabel = NewRunWarningTextBox();
        overlayPage.Controls.Add(overlayWarningLabel);
        overlayPage.Controls.Add(new Label
        {
            Text = "Runtime overlay",
            AutoSize = false,
            Width = 540,
            Height = 28,
            ForeColor = Color.FromArgb(190, 194, 202),
            Font = new Font("Segoe UI Semibold", 10F),
            Margin = new Padding(0, 18, 0, 0)
        });
        overlayOutputLink = new LinkLabel
        {
            Text = "Complete Project setup to determine the overlay path.",
            AutoSize = false,
            Width = 540,
            Height = 42,
            AutoEllipsis = true,
            LinkColor = Color.FromArgb(214, 91, 157),
            ActiveLinkColor = Color.FromArgb(244, 138, 194),
            VisitedLinkColor = Color.FromArgb(214, 91, 157),
            DisabledLinkColor = Color.FromArgb(125, 129, 138),
            LinkBehavior = LinkBehavior.HoverUnderline,
            Enabled = false,
            Margin = new Padding(0, 2, 0, 4)
        };
        overlayOutputLink.LinkClicked += delegate { RevealOverlayOutput(); };
        overlayPage.Controls.Add(overlayOutputLink);
        openOverlayButton = NewDarkButton("Open in Explorer", 210);
        openOverlayButton.Height = 34;
        openOverlayButton.Margin = new Padding(0, 10, 0, 0);
        openOverlayButton.Click += delegate { RevealOverlayOutput(); };
        openOverlayButton.Enabled = false;
        overlayPage.Controls.Add(openOverlayButton);

        FlowLayoutPanel menuPage = CreatePage(
            "Menu translation",
            "Extract Win32 menu captions, translate them, and write the runtime maps.");
        // Three steps rather than one action: extraction needs no DeepL, and the
        // catalog can be filled in by hand between the second and third.
        FlowLayoutPanel menuActions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0),
            BackColor = Color.FromArgb(12, 13, 16)
        };
        Button menuExtractButton = AddPageAction(menuActions, "1. Extract captions",
            delegate { RunSingle(BuildMenuExtract()); });
        menuExtractButton.Margin = new Padding(0, 8, 10, 0);
        Button menuTranslateButton = AddPageAction(menuActions, "2. Translate",
            delegate { RunMenuStep(BuildMenuTranslate); });
        menuTranslateButton.Margin = new Padding(0, 8, 10, 0);
        Button menuRuntimeButton = AddPageAction(menuActions, "3. Write runtime maps",
            delegate { RunMenuStep(BuildMenuRuntime); });
        menuRuntimeButton.Margin = new Padding(0, 8, 0, 0);
        menuPage.Controls.Add(menuActions);
        AddPageAction(menuPage, "Run all three", delegate { RunMenuPipeline(); });
        menuStatusLabel = new Label
        {
            Text = "Complete Project setup to read the menu catalog.",
            AutoSize = false,
            Width = 540,
            Height = 44,
            ForeColor = Color.FromArgb(190, 194, 202),
            Margin = new Padding(0, 12, 0, 0)
        };
        menuPage.Controls.Add(menuStatusLabel);
        menuCatalogLink = new LinkLabel
        {
            Text = "Complete Project setup to determine the catalog path.",
            AutoSize = false,
            Width = 540,
            Height = 42,
            AutoEllipsis = true,
            LinkColor = Color.FromArgb(214, 91, 157),
            ActiveLinkColor = Color.FromArgb(244, 138, 194),
            VisitedLinkColor = Color.FromArgb(214, 91, 157),
            DisabledLinkColor = Color.FromArgb(125, 129, 138),
            LinkBehavior = LinkBehavior.HoverUnderline,
            Enabled = false,
            Margin = new Padding(0, 2, 0, 4)
        };
        menuCatalogLink.LinkClicked += delegate { OpenMenuCatalog(); };
        menuPage.Controls.Add(menuCatalogLink);
        menuPage.Controls.Add(new Label
        {
            Text = "Step 2 needs a DeepL token only for captions the cache does not " +
                "already cover. Without one, open the catalog and fill in the \"ru\" " +
                "and \"en\" fields by hand, then run step 3.",
            AutoSize = false,
            Width = 540,
            Height = 58,
            ForeColor = Color.FromArgb(150, 154, 162),
            Margin = new Padding(0, 0, 0, 4)
        });

        FlowLayoutPanel localizedArchivesPage = CreatePage(
            "Localized UI resources",
            "Extract every source CGF/IAF image, then package only explicitly translated resources.");
        extractLocalizedArchivesButton = AddPageAction(
            localizedArchivesPage,
            "Extract Japanese UI resources",
            delegate { ExtractLocalizedUiResources(); });
        buildLocalizedArchivesButton = AddPageAction(
            localizedArchivesPage,
            "Build localized UI archives",
            delegate { BuildLocalizedUiArchives(); });
        localizedArchivesStatusLabel = new Label
        {
            Text = "Complete Project setup to inspect the localized archives.",
            AutoSize = false,
            Width = 540,
            Height = 96,
            ForeColor = Color.FromArgb(190, 194, 202),
            Margin = new Padding(0, 18, 0, 0)
        };
        localizedArchivesPage.Controls.Add(localizedArchivesStatusLabel);
        openLocalizedArchivesButton = NewDarkButton("Open UI assets", 210);
        openLocalizedArchivesButton.Height = 34;
        openLocalizedArchivesButton.Margin = new Padding(0, 10, 0, 0);
        openLocalizedArchivesButton.Click += delegate { RevealLocalizedUiAssets(); };
        openLocalizedArchivesButton.Enabled = false;
        localizedArchivesPage.Controls.Add(openLocalizedArchivesButton);
        localizedArchivesPage.Controls.Add(new Label
        {
            Text = "Edit a *.jp.png without changing its pixel dimensions, then save it beside the source " +
                "as *.ru.png and/or *.en.png. Build packages only those explicitly suffixed files; " +
                "generating the launcher does not rebuild them.",
            AutoSize = false,
            Width = 540,
            Height = 58,
            ForeColor = Color.FromArgb(150, 154, 162),
            Margin = new Padding(0, 8, 0, 4)
        });

        FlowLayoutPanel launcherPage = CreatePage(
            "Game launcher",
            "Generate a compact language launcher directly in the selected game folder.");
        generateLauncherButton = AddPageAction(launcherPage, "Generate game launcher", delegate { GenerateGameLauncher(); });
        generateLauncherButton.Enabled = false;
        launcherPage.Controls.Add(new Label
        {
            Text = "Generated launcher",
            AutoSize = false,
            Width = 540,
            Height = 28,
            ForeColor = Color.FromArgb(190, 194, 202),
            Font = new Font("Segoe UI Semibold", 10F),
            Margin = new Padding(0, 18, 0, 0)
        });
        launcherOutputLink = new LinkLabel
        {
            Text = "Complete Project setup to determine the launcher path.",
            AutoSize = false,
            Width = 540,
            Height = 42,
            AutoEllipsis = true,
            LinkColor = Color.FromArgb(214, 91, 157),
            ActiveLinkColor = Color.FromArgb(244, 138, 194),
            VisitedLinkColor = Color.FromArgb(214, 91, 157),
            DisabledLinkColor = Color.FromArgb(125, 129, 138),
            LinkBehavior = LinkBehavior.HoverUnderline,
            Enabled = false,
            Margin = new Padding(0, 2, 0, 4)
        };
        launcherOutputLink.LinkClicked += delegate { RevealLauncherOutput(); };
        launcherPage.Controls.Add(launcherOutputLink);
        openLauncherButton = NewDarkButton("Open in Explorer", 210);
        openLauncherButton.Height = 34;
        openLauncherButton.Margin = new Padding(0, 10, 0, 0);
        openLauncherButton.Click += delegate { RevealLauncherOutput(); };
        openLauncherButton.Enabled = false;
        launcherPage.Controls.Add(openLauncherButton);

        statusLabel = new Label
        {
            Text = "Ready",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(190, 194, 202),
            BackColor = Color.FromArgb(8, 9, 11),
            Padding = new Padding(16, 6, 8, 0)
        };

        pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(12, 13, 16) };

        FlowLayoutPanel navigation = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(12, 18, 12, 18),
            BackColor = Color.FromArgb(6, 7, 9)
        };
        AddNavigation(navigation, "1. Project setup", projectPage);
        AddNavigation(navigation, "2. Extract text", extractPage);
        AddNavigation(navigation, "3. Translate", translatePage);
        AddNavigation(navigation, "4. Proofread", proofreadPage);
        AddNavigation(navigation, "5. Menu translation", menuPage);
        AddNavigation(navigation, "6. Build overlay", overlayPage);
        AddNavigation(navigation, "7. Localized UI resources", localizedArchivesPage);
        AddNavigation(navigation, "8. Game launcher", launcherPage);

        TableLayoutPanel content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.FromArgb(12, 13, 16)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        content.Controls.Add(pageHost, 0, 0);
        content.Controls.Add(statusLabel, 0, 1);

        TableLayoutPanel body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.FromArgb(6, 7, 9)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210F));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        body.Controls.Add(navigation, 0, 0);
        body.Controls.Add(content, 1, 0);
        Controls.Add(body);
        Controls.Add(header);

        tips.SetToolTip(profileBox, "Auto-detect uses RSA.EXE/RSAN.SD when present. Generic Delta skips Reika-specific name rules.");
        tips.SetToolTip(tokenBox, "Your DeepL API key is protected for the current Windows user before it is saved.");
        tips.SetToolTip(overwriteBox, "Retranslate every selected row from scratch. Leave unchecked to resume from the cache after an interruption.");
        profileBox.SelectedIndexChanged += delegate { AutoFillGameFiles(); RefreshLauncherAvailability(); RefreshNavigationAvailability(); RefreshProofreadRulesLink(); RefreshMenuStatus(); RefreshOverlayOutput(); RefreshLocalizedArchivesOutput(); RefreshLauncherOutput(); };
        // Attached here rather than in NewLanguageBox: LoadSettings moves the
        // first picker while most of the window it would refresh is still null.
        foreach (ComboBox picker in languageBoxes)
        {
            ComboBox moved = picker;
            moved.SelectedIndexChanged += delegate { LanguageChanged(moved); };
        }
        gameDirectoryBox.TextChanged += delegate { AutoFillGameFiles(); RefreshLauncherAvailability(); RefreshNavigationAvailability(); RefreshExtractOutput(); RefreshProofreadRulesLink(); RefreshMenuStatus(); RefreshOverlayOutput(); RefreshLocalizedArchivesOutput(); RefreshLauncherOutput(); };
        executableBox.TextChanged += delegate { RefreshLauncherAvailability(); RefreshNavigationAvailability(); RefreshExtractOutput(); RefreshProofreadRulesLink(); RefreshMenuStatus(); RefreshOverlayOutput(); RefreshLocalizedArchivesOutput(); RefreshLauncherOutput(); };
        sourceBox.TextChanged += delegate { RefreshNavigationAvailability(); RefreshExtractOutput(); RefreshProofreadRulesLink(); RefreshMenuStatus(); RefreshOverlayOutput(); RefreshLocalizedArchivesOutput(); RefreshLauncherOutput(); };
        FormClosing += delegate { SaveSettings(); };
        ShowPage(navigationButtons[0]);
        RefreshNavigationAvailability();
        RefreshLauncherAvailability();
        RefreshExtractOutput();
        RefreshProofreadRulesLink();
        RefreshMenuStatus();
        RefreshOverlayOutput();
        RefreshLocalizedArchivesOutput();
        RefreshLauncherOutput();
        Shown += delegate
        {
            if (!HasCompatiblePython)
                MessageBox.Show(this, pythonDetectionMessage, "Python requirement", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
    }

    private bool HasCompatiblePython
    {
        get
        {
            return !string.IsNullOrWhiteSpace(pythonExecutable) &&
                pythonVersion != null && pythonVersion.CompareTo(DeltaPython.MinimumVersion) >= 0;
        }
    }

    private TextBox NewPathBox(string tooltip)
    {
        TextBox box = new TextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(31, 33, 39),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        tips.SetToolTip(box, tooltip);
        return box;
    }

    private static ComboBox NewComboBox()
    {
        return new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(31, 33, 39),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
    }

    // Every language picker is one view of the same project setting: whichever
    // one the user reaches for, the rest follow it.
    private ComboBox NewLanguageBox()
    {
        ComboBox box = NewComboBox();
        box.Items.AddRange(new object[] { "Russian (RU)", "English (EN)" });
        box.SelectedIndex = languageBoxes.Count == 0 ? 0 : languageBoxes[0].SelectedIndex;
        tips.SetToolTip(box, "Target language of the whole project, the same setting on every page. " +
            "Selects the translation workbook, the proofread rules file and the overlay that are built.");
        languageBoxes.Add(box);
        return box;
    }

    private void LanguageChanged(ComboBox origin)
    {
        // Moving the other pickers raises this again from each of them.
        if (languageSyncInProgress)
            return;
        languageSyncInProgress = true;
        try
        {
            foreach (ComboBox picker in languageBoxes)
                if (picker != origin)
                    picker.SelectedIndex = origin.SelectedIndex;
        }
        finally
        {
            languageSyncInProgress = false;
        }
        ResetEstimate();
        RefreshLauncherAvailability();
        RefreshNavigationAvailability();
        RefreshProofreadRulesLink();
        RefreshMenuStatus();
        RefreshOverlayOutput();
        RefreshLocalizedArchivesOutput();
        RefreshLauncherOutput();
    }

    // The estimate and the window count describe the language that was selected
    // a moment ago, and neither carries over to the other one. The version bump
    // drops the usage reply still in flight for the estimate being cleared.
    private void ResetEstimate()
    {
        Interlocked.Increment(ref estimateRequestVersion);
        estimateCharacters = null;
        estimateAvailableCharacters = null;
        estimateAvailabilityMessage = null;
        usageLabel.Text = EstimateUncalculated;
        dialogWindowsLabel.Text = DialogWindowsUncalculated;
    }

    private static NumericUpDown NewNumberBox()
    {
        return new NumericUpDown
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = 1000000,
            Value = 0,
            BackColor = Color.FromArgb(31, 33, 39),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
    }

    private static TableLayoutPanel NewFormTable(int rows, int width, int height)
    {
        TableLayoutPanel table = new TableLayoutPanel
        {
            Width = width,
            Height = height,
            ColumnCount = 3,
            RowCount = rows,
            BackColor = Color.FromArgb(12, 13, 16),
            Margin = new Padding(0, 12, 0, 4)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        for (int row = 0; row < rows; row++)
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
        return table;
    }

    private static Label NewHintLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(145, 151, 162)
        };
    }

    private static FlowLayoutPanel CreatePage(string title, string subtitle)
    {
        FlowLayoutPanel page = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(28, 24, 28, 24),
            BackColor = Color.FromArgb(12, 13, 16)
        };
        // The heading sizes to its text rather than to a fixed box. The two
        // boxes were 38 and 42 pixels tall around a 31-pixel line and a
        // 15-pixel one, and that empty remainder is what put Translate over
        // the height of the window once it gained a language row. MaximumSize
        // keeps the wrapping width, so a subtitle longer than the panel grows
        // a second line instead of losing half of the first.
        page.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 17F),
            Margin = new Padding(0)
        });
        page.Controls.Add(new Label
        {
            Text = subtitle,
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            ForeColor = Color.FromArgb(166, 171, 181),
            Margin = new Padding(0, 2, 0, 4)
        });
        return page;
    }

    private void AddRow(TableLayoutPanel table, int row, string label, Control control, Control action)
    {
        AddRow(table, row, label, control, action, false);
    }

    private void AddRow(
        TableLayoutPanel table, int row, string label, Control control, Control action, bool required)
    {
        Label text = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(205, 208, 214)
        };
        control.Margin = new Padding(0, 6, 8, 6);
        if (required)
            table.Controls.Add(RequiredCaption(text), 0, row);
        else
            table.Controls.Add(text, 0, row);

        table.Controls.Add(control, 1, row);
        if (action != null)
        {
            action.Margin = new Padding(0, 6, 0, 6);
            table.Controls.Add(action, 2, row);
        }
    }

    // A two-cell strip rather than one label, so only the asterisk is red while
    // both halves keep the vertical centring the table gives a docked label.
    private static Control RequiredCaption(Label caption)
    {
        TableLayoutPanel strip = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        caption.Dock = DockStyle.Fill;
        caption.AutoSize = true;
        caption.Margin = new Padding(0);
        Label asterisk = new Label
        {
            Text = "*",
            Dock = DockStyle.Fill,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(232, 74, 106),
            Font = new Font("Segoe UI Semibold", 9F),
            Margin = new Padding(2, 0, 0, 0)
        };
        strip.Controls.Add(caption, 0, 0);
        strip.Controls.Add(asterisk, 1, 0);
        return strip;
    }

    private Button BrowseButton(TextBox target, bool folder)
    {
        Button button = NewDarkButton("Browse...", 88);
        button.Click += delegate
        {
            if (folder)
            {
                using (OpenFileDialog dialog = new OpenFileDialog())
                {
                    dialog.Title = "Select Delta game folder";
                    dialog.ValidateNames = false;
                    dialog.CheckFileExists = false;
                    dialog.CheckPathExists = true;
                    dialog.FileName = "Select this folder";
                    dialog.Filter = "Folders|*.folder";
                    string initialDirectory = Directory.Exists(target.Text)
                        ? target.Text
                        : Environment.CurrentDirectory;
                    if (!Directory.Exists(initialDirectory))
                        initialDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    dialog.InitialDirectory = initialDirectory;
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                        target.Text = Path.GetDirectoryName(dialog.FileName);
                }
            }
            else
            {
                using (OpenFileDialog dialog = new OpenFileDialog())
                {
                    dialog.Filter = "Executable or data files|*.exe;*.sd;*.dat|All files|*.*";
                    if (File.Exists(target.Text))
                        dialog.FileName = target.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                        target.Text = dialog.FileName;
                }
            }
        };
        return button;
    }

    private Button TokenHelpButton()
    {
        Button button = NewDarkButton("?", 34);
        tips.SetToolTip(button, "Where do I get a DeepL token?");
        button.Click += delegate
        {
            try { Process.Start(new ProcessStartInfo(DeepLAccountUrl) { UseShellExecute = true }); }
            catch { MessageBox.Show(this, DeepLAccountUrl, "DeepL API keys", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        return button;
    }

    private static Button NewDarkButton(string caption, int width)
    {
        Button button = new Button
        {
            Text = caption,
            Height = 28,
            Width = width,
            BackColor = Color.FromArgb(12, 14, 18),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(142, 0, 77);
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(72, 24, 52);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(112, 0, 61);
        return button;
    }

    private Button AddPageAction(FlowLayoutPanel panel, string caption, EventHandler handler)
    {
        Button button = NewDarkButton(caption, 210);
        button.Height = 34;
        button.Margin = new Padding(0, 14, 0, 0);
        button.Click += delegate(object sender, EventArgs arguments)
        {
            try { handler(sender, arguments); }
            catch (Exception error) { ShowError(error); }
        };
        panel.Controls.Add(button);
        actionButtons.Add(button);
        return button;
    }

    private Button AddNavigation(FlowLayoutPanel panel, string caption, Control page)
    {
        Button button = NewDarkButton(caption, 184);
        button.Height = 36;
        button.Margin = new Padding(0, 0, 0, 7);
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.Padding = new Padding(12, 0, 0, 0);
        button.Click += delegate { ShowPage(button); };
        panel.Controls.Add(button);
        navigationButtons.Add(button);
        navigationPages[button] = page;
        page.Visible = false;
        pageHost.Controls.Add(page);
        return button;
    }

    // Every stage past setup needs the game folder, the executable and the
    // script archive. Without them GetContext throws, and it used to throw from
    // inside a click handler, which Windows reports as an unhandled exception
    // rather than as the missing setting it is.
    private bool ProjectIsReady()
    {
        string game = gameDirectoryBox.Text.Trim();
        return game.Length > 0 && Directory.Exists(game) &&
            File.Exists(executableBox.Text.Trim()) &&
            File.Exists(sourceBox.Text.Trim());
    }

    private void RefreshNavigationAvailability()
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)RefreshNavigationAvailability);
            return;
        }
        bool ready = ProjectIsReady();
        for (int index = 1; index < navigationButtons.Count; index++)
        {
            Button button = navigationButtons[index];
            button.Enabled = ready;
            tips.SetToolTip(button, ready
                ? string.Empty
                : "Set the game folder, executable and source archive in Project setup first.");
        }
        // Falling back to setup beats leaving a page open that cannot act.
        if (!ready && navigationButtons.Count > 0 && activeNavigationButton != navigationButtons[0])
            ShowPage(navigationButtons[0]);
    }

    private void ShowPage(Button navigationButton)
    {
        Control page;
        if (!navigationPages.TryGetValue(navigationButton, out page))
            return;
        if (!navigationButton.Enabled)
            return;
        foreach (Control candidate in pageHost.Controls)
            candidate.Visible = candidate == page;
        page.BringToFront();
        activeNavigationButton = navigationButton;
        foreach (Button button in navigationButtons)
            button.BackColor = button == activeNavigationButton
                ? Color.FromArgb(104, 0, 57)
                : Color.FromArgb(12, 14, 18);
    }

    private void RefreshExtractOutput()
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)RefreshExtractOutput);
            return;
        }
        string workbook = null;
        try { workbook = GetContext().Workbook; }
        catch { }
        bool available = !string.IsNullOrWhiteSpace(workbook) && File.Exists(workbook);
        extractWorkbookLink.Links.Clear();
        extractWorkbookLink.Text = string.IsNullOrWhiteSpace(workbook)
            ? "Complete Project setup to determine the output workbook path."
            : workbook;
        extractWorkbookLink.Enabled = available;
        if (available)
            extractWorkbookLink.Links.Add(0, extractWorkbookLink.Text.Length, workbook);
        openExtractButton.Enabled = available;
    }

    private void OpenExtractWorkbook()
    {
        try
        {
            string workbook = GetContext().Workbook;
            if (!File.Exists(workbook))
                throw new FileNotFoundException("The extracted workbook does not exist yet.", workbook);
            Process.Start(new ProcessStartInfo { FileName = workbook, UseShellExecute = true });
            extractWorkbookLink.LinkVisited = true;
        }
        catch (Exception error) { ShowError(error); }
    }

    private void RevealExtractWorkbook()
    {
        try
        {
            string workbook = GetContext().Workbook;
            if (!File.Exists(workbook))
                throw new FileNotFoundException("The extracted workbook does not exist yet.", workbook);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select," + Quote(workbook),
                UseShellExecute = true
            });
        }
        catch (Exception error) { ShowError(error); }
    }

    private void RefreshProofreadRulesLink()
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)RefreshProofreadRulesLink);
            return;
        }
        string rules = null;
        try { rules = GetContext().Rules; }
        catch { }
        proofreadRulesLink.Links.Clear();
        proofreadRulesLink.Text = string.IsNullOrWhiteSpace(rules)
            ? "Complete Project setup to create a rules file."
            : (File.Exists(rules) ? rules : "Create from template: " + rules);
        proofreadRulesLink.Enabled = !string.IsNullOrWhiteSpace(rules);
        if (proofreadRulesLink.Enabled)
            proofreadRulesLink.Links.Add(0, proofreadRulesLink.Text.Length, rules);
    }

    private void OpenProofreadRules()
    {
        try
        {
            Context context = GetContext();
            Directory.CreateDirectory(context.WorkDirectory);
            if (!File.Exists(context.Rules))
            {
                string seed = context.RulesSeed(toolsDirectory);
                if (!File.Exists(seed))
                    throw new FileNotFoundException("The proofread rules template is missing.", seed);
                File.Copy(seed, context.Rules, false);
            }
            Process.Start(new ProcessStartInfo { FileName = context.Rules, UseShellExecute = true });
            proofreadRulesLink.LinkVisited = true;
            RefreshProofreadRulesLink();
        }
        catch (Exception error) { ShowError(error); }
    }

    // The menu catalog is built from the executable, not from RSAN.SD, so its
    // progress is independent of the scenario workbook and worth showing on its
    // own page.
    private void RefreshMenuStatus()
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)RefreshMenuStatus);
            return;
        }

        string catalog = null;
        try { catalog = GetContext().MenuCatalog; }
        catch { }

        menuCatalogLink.Links.Clear();
        bool present = !string.IsNullOrWhiteSpace(catalog) && File.Exists(catalog);
        menuCatalogLink.Text = string.IsNullOrWhiteSpace(catalog)
            ? "Complete Project setup to determine the catalog path."
            : catalog;
        menuCatalogLink.Enabled = present;
        if (present)
            menuCatalogLink.Links.Add(0, menuCatalogLink.Text.Length, catalog);

        if (string.IsNullOrWhiteSpace(catalog))
        {
            menuStatusLabel.Text = "Complete Project setup to read the menu catalog.";
            return;
        }
        if (!File.Exists(catalog))
        {
            menuStatusLabel.Text = "No catalog yet. Generate menu translation reads the captions " +
                "out of the executable and writes " + Path.GetFileName(catalog) + ".";
            return;
        }

        try
        {
            int total, russian, english;
            CountMenuEntries(catalog, out total, out russian, out english);
            if (total == 0)
            {
                menuStatusLabel.Text = "The catalog is empty: no menu captions were found in the executable.";
                return;
            }
            menuStatusLabel.Text = string.Format(
                "{0} captions in {1}{2}RU {3}/{0} translated{4}    EN {5}/{0} translated{6}",
                total, Path.GetFileName(catalog), Environment.NewLine,
                russian, russian == total ? "" : " (" + (total - russian) + " left)",
                english, english == total ? "" : " (" + (total - english) + " left)");
        }
        catch (Exception error)
        {
            menuStatusLabel.Text = "Could not read " + Path.GetFileName(catalog) + ": " + error.Message;
        }
    }

    private static void CountMenuEntries(string path, out int total, out int russian, out int english)
    {
        JavaScriptSerializer reader = new JavaScriptSerializer();
        Dictionary<string, object> catalog =
            (Dictionary<string, object>)reader.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
        object entries;
        total = russian = english = 0;
        if (!catalog.TryGetValue("entries", out entries) || !(entries is object[]))
            return;

        foreach (object item in (object[])entries)
        {
            Dictionary<string, object> entry = item as Dictionary<string, object>;
            if (entry == null)
                continue;
            total++;
            if (HasText(entry, "ru")) russian++;
            if (HasText(entry, "en")) english++;
        }
    }

    private static bool HasText(Dictionary<string, object> entry, string key)
    {
        object value;
        return entry.TryGetValue(key, out value) && value != null &&
            !string.IsNullOrWhiteSpace(value.ToString());
    }

    private void RefreshOverlayOutput()
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)RefreshOverlayOutput);
            return;
        }
        string overlay = null;
        try { overlay = GetContext().Overlay; }
        catch { }
        bool available = !string.IsNullOrWhiteSpace(overlay) && File.Exists(overlay);
        overlayOutputLink.Links.Clear();
        overlayOutputLink.Text = string.IsNullOrWhiteSpace(overlay)
            ? "Complete Project setup to determine the overlay path."
            : overlay;
        overlayOutputLink.Enabled = available;
        if (available)
            overlayOutputLink.Links.Add(0, overlayOutputLink.Text.Length, overlay);
        openOverlayButton.Enabled = available;
    }

    private void RevealOverlayOutput()
    {
        try
        {
            string overlay = GetContext().Overlay;
            if (!File.Exists(overlay))
                throw new FileNotFoundException("The runtime overlay does not exist yet.", overlay);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select," + Quote(overlay),
                UseShellExecute = true
            });
        }
        catch (Exception error) { ShowError(error); }
    }

    private void RefreshLocalizedArchivesOutput()
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)RefreshLocalizedArchivesOutput);
            return;
        }
        try
        {
            Context context = GetContext();
            string cgDirectory = Path.Combine(context.GameDirectory, "CG");
            string assetsDirectory = Path.Combine(context.WorkDirectory, "ui_assets");
            bool supported = Directory.Exists(cgDirectory);
            int extracted = Directory.Exists(assetsDirectory)
                ? Directory.GetFiles(assetsDirectory, "*.jp.png", SearchOption.AllDirectories).Length
                : 0;
            int russian = supported
                ? Directory.GetFiles(cgDirectory, "*.ru.CGF", SearchOption.TopDirectoryOnly).Length +
                    Directory.GetFiles(cgDirectory, "*.ru.IAF", SearchOption.TopDirectoryOnly).Length
                : 0;
            int english = supported
                ? Directory.GetFiles(cgDirectory, "*.en.CGF", SearchOption.TopDirectoryOnly).Length +
                    Directory.GetFiles(cgDirectory, "*.en.IAF", SearchOption.TopDirectoryOnly).Length
                : 0;
            localizedArchivesStatusLabel.Text = supported
                ? string.Format(
                    "Editable images: {0}{1}Extracted Japanese previews: {2}{1}" +
                    "Built Russian resources: {3}    English resources: {4}",
                    assetsDirectory, Environment.NewLine, extracted, russian, english)
                : "The selected game has no CG folder.";
            openLocalizedArchivesButton.Enabled = Directory.Exists(assetsDirectory);
            extractLocalizedArchivesButton.Enabled = supported && ProjectIsReady() && !UseWaitCursor;
            buildLocalizedArchivesButton.Enabled = supported && ProjectIsReady() && !UseWaitCursor;
        }
        catch
        {
            localizedArchivesStatusLabel.Text =
                "Complete Project setup to inspect the localized archives.";
            openLocalizedArchivesButton.Enabled = false;
            extractLocalizedArchivesButton.Enabled = false;
            buildLocalizedArchivesButton.Enabled = false;
        }
    }

    private void RevealLocalizedUiAssets()
    {
        try
        {
            string directory = Path.Combine(GetContext().WorkDirectory, "ui_assets");
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException(
                    "Extract Japanese UI resources first. The UI asset folder does not exist: " + directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = Quote(directory),
                UseShellExecute = true
            });
        }
        catch (Exception error) { ShowError(error); }
    }

    private void RefreshLauncherOutput()
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)RefreshLauncherOutput);
            return;
        }
        string launcher = null;
        try { launcher = Path.Combine(GetContext().GameDirectory, "DeltaLauncher.exe"); }
        catch { }
        bool available = !string.IsNullOrWhiteSpace(launcher) && File.Exists(launcher);
        launcherOutputLink.Links.Clear();
        launcherOutputLink.Text = string.IsNullOrWhiteSpace(launcher)
            ? "Complete Project setup to determine the launcher path."
            : launcher;
        launcherOutputLink.Enabled = available;
        if (available)
            launcherOutputLink.Links.Add(0, launcherOutputLink.Text.Length, launcher);
        openLauncherButton.Enabled = available;
    }

    private void RevealLauncherOutput()
    {
        try
        {
            string launcher = Path.Combine(GetContext().GameDirectory, "DeltaLauncher.exe");
            if (!File.Exists(launcher))
                throw new FileNotFoundException("The generated game launcher does not exist yet.", launcher);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select," + Quote(launcher),
                UseShellExecute = true
            });
        }
        catch (Exception error) { ShowError(error); }
    }

    private void AutoFillGameFiles()
    {
        if (!Directory.Exists(gameDirectoryBox.Text))
            return;
        bool reika = profileBox.SelectedIndex == 1 ||
            (profileBox.SelectedIndex == 0 && File.Exists(Path.Combine(gameDirectoryBox.Text, "RSA.EXE")));
        if (string.IsNullOrWhiteSpace(executableBox.Text) || !File.Exists(executableBox.Text))
        {
            string[] executables = Directory.GetFiles(gameDirectoryBox.Text, "*.exe");
            // Reika means RSA.EXE specifically; any other profile takes whatever
            // single executable the folder offers.
            string selected = reika
                ? executables.FirstOrDefault(path =>
                    string.Equals(Path.GetFileName(path), "RSA.EXE", StringComparison.OrdinalIgnoreCase))
                : executables.FirstOrDefault();
            if (!string.IsNullOrEmpty(selected))
                executableBox.Text = selected;
        }
        if (string.IsNullOrWhiteSpace(sourceBox.Text) || !File.Exists(sourceBox.Text))
        {
            // Every Delta build ships the script as RSAN.SD; the profile does not
            // change that, only whether its hash is checked later.
            string candidate = Path.Combine(gameDirectoryBox.Text, "RSAN.SD");
            if (File.Exists(candidate))
                sourceBox.Text = candidate;
        }
    }

    private Context GetContext()
    {
        string executable = executableBox.Text.Trim();
        bool reika = profileBox.SelectedIndex == 1 ||
            (profileBox.SelectedIndex == 0 && DeltaProject.LooksLikeReika(executable));
        return DeltaProject.Create(
            toolsDirectory,
            gameDirectoryBox.Text.Trim(),
            executable,
            sourceBox.Text.Trim(),
            languageBox.SelectedIndex == 0 ? "RU" : "EN",
            reika);
    }

    private CommandSpec BuildExtract()
    {
        Context context = GetContext();
        // The extractor verifies RSAN.SD against the build it was written for.
        // That check only means something for the profile it was written for, so
        // it stays on there and is waived for anything else.
        string unknown = context.Reika ? "" : " --allow-unknown";
        return new CommandSpec("delta_overlay.py extract " + Quote(context.Source) + " " + Quote(context.Workbook) + unknown, false);
    }

    private CommandSpec BuildTranslate()
    {
        Context context = GetContext();
        string overwrite = overwriteBox.Checked ? " --overwrite" : "";
        string limit = dialogLimitBox.Value > 0 ? " --max-dialogs " + dialogLimitBox.Value : "";
        return new CommandSpec("delta_deepl.py " + Quote(context.Workbook) + " " + Quote(context.TranslatedWorkbook) +
            " --target-lang " + context.Language + " --cache " + Quote(context.Cache) + overwrite + limit, true);
    }

    private CommandSpec BuildEstimate()
    {
        Context context = GetContext();
        string overwrite = overwriteBox.Checked ? " --overwrite" : "";
        string limit = dialogLimitBox.Value > 0 ? " --max-dialogs " + dialogLimitBox.Value : "";
        return new CommandSpec("delta_deepl.py " + Quote(context.Workbook) + " " + Quote(context.TranslatedWorkbook) +
            " --target-lang " + context.Language + " --cache " + Quote(context.Cache) + overwrite + limit + " --estimate", false);
    }

    private void CalculateEstimate()
    {
        CommandSpec command = BuildEstimate();
        int requestVersion = Interlocked.Increment(ref estimateRequestVersion);
        estimateCharacters = null;
        estimateAvailableCharacters = null;

        string token = tokenBox.Text.Trim();
        estimateAvailabilityMessage = token.Length == 0
            ? "token not specified"
            : "checking usage...";
        RefreshEstimateLabel();
        if (token.Length > 0)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    DeepLUsage usage = FetchDeepLUsage(token);
                    SetEstimateAvailability(
                        requestVersion,
                        Math.Max(0L, usage.Limit - usage.Used));
                }
                catch
                {
                    SetEstimateAvailabilityError(requestVersion);
                }
            });
        }

        RunSingle(command);
    }

    private void SetEstimateAvailability(int requestVersion, long available)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action) delegate
            {
                SetEstimateAvailability(requestVersion, available);
            });
            return;
        }
        if (requestVersion != estimateRequestVersion)
            return;
        estimateAvailableCharacters = available;
        estimateAvailabilityMessage = null;
        RefreshEstimateLabel();
    }

    private void SetEstimateAvailabilityError(int requestVersion)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action) delegate
            {
                SetEstimateAvailabilityError(requestVersion);
            });
            return;
        }
        if (requestVersion != estimateRequestVersion)
            return;
        estimateAvailableCharacters = null;
        estimateAvailabilityMessage = "usage API error";
        RefreshEstimateLabel();
    }

    private void RefreshEstimateLabel()
    {
        if (!estimateCharacters.HasValue)
        {
            usageLabel.Text = "API estimate: calculating...";
            return;
        }
        string available = estimateAvailableCharacters.HasValue
            ? string.Format(
                CultureInfo.InvariantCulture,
                " ({0:N0} available)",
                estimateAvailableCharacters.Value)
            : string.IsNullOrEmpty(estimateAvailabilityMessage)
                ? string.Empty
                : " (" + estimateAvailabilityMessage + ")";
        usageLabel.Text = string.Format(
            CultureInfo.InvariantCulture,
            "API estimate: {0:N0} source characters{1}",
            estimateCharacters.Value,
            available);
    }

    private void LoadSettings()
    {
        if (!File.Exists(settingsPath))
            return;
        try
        {
            TranslatorSettings settings = new JavaScriptSerializer().Deserialize<TranslatorSettings>(
                File.ReadAllText(settingsPath, Encoding.UTF8));
            if (settings == null)
                return;
            if (settings.ProfileIndex >= 0 && settings.ProfileIndex < profileBox.Items.Count)
                profileBox.SelectedIndex = settings.ProfileIndex;
            if (settings.LanguageIndex >= 0 && settings.LanguageIndex < languageBox.Items.Count)
                languageBox.SelectedIndex = settings.LanguageIndex;
            gameDirectoryBox.Text = settings.GameDirectory ?? string.Empty;
            executableBox.Text = settings.Executable ?? string.Empty;
            sourceBox.Text = settings.SourceArchive ?? string.Empty;
            tokenBox.Text = LoadProtectedToken(settings.DeepLTokenProtected ?? string.Empty);
        }
        catch
        {
            // A damaged local settings file must not prevent the translator from opening.
        }
    }

    private void SaveSettings()
    {
        try
        {
            TranslatorSettings settings = new TranslatorSettings
            {
                ProfileIndex = profileBox.SelectedIndex,
                LanguageIndex = languageBox.SelectedIndex,
                GameDirectory = gameDirectoryBox.Text.Trim(),
                Executable = executableBox.Text.Trim(),
                SourceArchive = sourceBox.Text.Trim(),
                DeepLTokenProtected = ProtectToken(tokenBox.Text.Trim())
            };
            string json = new JavaScriptSerializer().Serialize(settings);
            string temporaryPath = settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Copy(temporaryPath, settingsPath, true);
            File.Delete(temporaryPath);
        }
        catch (Exception error)
        {
            MessageBox.Show(
                this,
                "Could not save settings beside the executable: " + error.Message,
                "Delta VN Translator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private sealed class DeepLUsage
    {
        public string AccountType;
        public long Used;
        public long Limit;
    }

    private void CheckDeepLToken()
    {
        string token = tokenBox.Text.Trim();
        if (token.Length == 0)
        {
            SetTokenUsage("Enter a DeepL API token first.", Color.FromArgb(238, 151, 91), true);
            return;
        }

        checkTokenButton.Enabled = false;
        tokenUsageLabel.Text = "Checking token and usage...";
        tokenUsageLabel.ForeColor = Color.FromArgb(190, 194, 202);
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                DeepLUsage usage = FetchDeepLUsage(token);
                long remaining = Math.Max(0L, usage.Limit - usage.Used);
                string message = string.Format(
                    CultureInfo.InvariantCulture,
                    "Token is valid ({0}). Used: {1:N0} / {2:N0}. Remaining: {3:N0} characters.",
                    usage.AccountType,
                    usage.Used,
                    usage.Limit,
                    remaining);
                SetTokenUsage(message, Color.FromArgb(121, 206, 143), true);
            }
            catch (UnauthorizedAccessException)
            {
                SetTokenUsage(
                    "Token is invalid or does not belong to a DeepL API account.",
                    Color.FromArgb(232, 106, 106),
                    true);
            }
            catch (Exception error)
            {
                SetTokenUsage(
                    "Could not check the token: " + error.Message,
                    Color.FromArgb(238, 151, 91),
                    true);
            }
        });
    }

    private void SetTokenUsage(string text, Color color, bool enableButton)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action) delegate { SetTokenUsage(text, color, enableButton); });
            return;
        }
        tokenUsageLabel.Text = text;
        tokenUsageLabel.ForeColor = color;
        checkTokenButton.Enabled = enableButton;
    }

    private static DeepLUsage FetchDeepLUsage(string token)
    {
        Exception lastNetworkError = null;
        foreach (KeyValuePair<string, string> endpoint in new[]
        {
            new KeyValuePair<string, string>("Free API", "https://api-free.deepl.com/v2/usage"),
            new KeyValuePair<string, string>("Pro API", "https://api.deepl.com/v2/usage")
        })
        {
            try
            {
                DeepLUsage usage = RequestDeepLUsage(endpoint.Value, token);
                if (usage != null)
                {
                    usage.AccountType = endpoint.Key;
                    return usage;
                }
            }
            catch (WebException error)
            {
                lastNetworkError = error;
            }
        }

        if (lastNetworkError != null)
            throw new InvalidOperationException(lastNetworkError.Message, lastNetworkError);
        throw new UnauthorizedAccessException();
    }

    private static DeepLUsage RequestDeepLUsage(string url, string token)
    {
        // The .NET Framework 4.x default can still negotiate TLS 1.0 on older
        // configurations. DeepL requires a modern TLS channel.
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        HttpWebRequest request = (HttpWebRequest) WebRequest.Create(url);
        request.Method = "GET";
        request.Accept = "application/json";
        request.UserAgent = "delta-vn-translator/1.0";
        request.Timeout = 15000;
        request.ReadWriteTimeout = 15000;
        request.Headers[HttpRequestHeader.Authorization] = "DeepL-Auth-Key " + token;

        try
        {
            using (HttpWebResponse response = (HttpWebResponse) request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                Dictionary<string, object> payload =
                    new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                object usedValue;
                object limitValue;
                if (!payload.TryGetValue("character_count", out usedValue) ||
                    !payload.TryGetValue("character_limit", out limitValue))
                    throw new InvalidDataException("DeepL returned usage without character totals.");
                return new DeepLUsage
                {
                    Used = Convert.ToInt64(usedValue, CultureInfo.InvariantCulture),
                    Limit = Convert.ToInt64(limitValue, CultureInfo.InvariantCulture)
                };
            }
        }
        catch (WebException error)
        {
            HttpWebResponse response = error.Response as HttpWebResponse;
            if (response != null &&
                (response.StatusCode == HttpStatusCode.Forbidden ||
                 response.StatusCode == HttpStatusCode.Unauthorized))
                return null;
            throw;
        }
    }

    private CommandSpec BuildProofread()
    {
        Context context = GetContext();
        string input = File.Exists(context.TranslatedWorkbook) ? context.TranslatedWorkbook : context.Workbook;
        string backup = context.ProofreadWorkbook + ".before.xlsx";
        string profilePath = context.ProofreadProfile(toolsDirectory);
        string profile = File.Exists(profilePath) ? " --profile " + Quote(profilePath) : "";
        string rules = File.Exists(context.Rules) ? " --rules " + Quote(context.Rules) : "";
        return new CommandSpec("proofread.py " + Quote(input) + " " + Quote(context.ProofreadWorkbook) +
            " --backup " + Quote(backup) + profile + rules, false);
    }

    private CommandSpec BuildOverlay()
    {
        Context context = GetContext();
        if (!File.Exists(context.TranslatedWorkbook))
            throw new FileNotFoundException(
                "The translation workbook does not exist. Run Translate before Build overlay. Expected file:",
                context.TranslatedWorkbook);
        string input = File.Exists(context.ProofreadWorkbook) ? context.ProofreadWorkbook : context.TranslatedWorkbook;
        context.WarnAboutProofread(AppendLog);
        return BuildOverlay(context, input);
    }

    private CommandSpec BuildOverlay(Context context, string input)
    {
        string names = context.Reika ? " --target-lang " + context.Language : "";
        string staged = Path.Combine(context.WorkDirectory, Path.GetFileName(context.Overlay));
        return new CommandSpec("delta_overlay.py build-overlay " + Quote(input) + " " + Quote(staged) + names, false);
    }

    private void RunSingle(CommandSpec command)
    {
        try
        {
            Context context = GetContext();
            Directory.CreateDirectory(context.WorkDirectory);
            bool unlockLauncher = command.Arguments.StartsWith("delta_overlay.py build-overlay", StringComparison.Ordinal);
            Action installOverlay = unlockLauncher ? (Action) delegate
            {
                string staged = Path.Combine(context.WorkDirectory, Path.GetFileName(context.Overlay));
                InstallRuntimeFile(staged, context.Overlay, false);
            } : null;
            RunCommands(new[] { command }, context, unlockLauncher, installOverlay);
        }
        catch (Exception error) { ShowError(error); }
    }

    // Reads the captions out of the executable. This is a different source from
    // the scenario workbook, which comes from RSAN.SD, so it has its own step.
    private CommandSpec BuildMenuExtract()
    {
        Context context = GetContext();
        Directory.CreateDirectory(context.WorkDirectory);
        return new CommandSpec(
            "menu extract " + Quote(context.Executable) + " " + Quote(context.MenuCatalog), false, true);
    }

    private List<CommandSpec> BuildMenuTranslate()
    {
        Context context = GetContext();
        if (!File.Exists(context.MenuCatalog))
            throw new FileNotFoundException("Extract the captions first.", context.MenuCatalog);
        List<CommandSpec> commands = new List<CommandSpec>();
        foreach (string language in new[] { "RU", "EN" })
            commands.Add(new CommandSpec(
                "delta_menu.py translate " + Quote(context.MenuCatalog) + " " + Quote(context.MenuCatalog) +
                " --target-lang " + language + " --cache " + Quote(context.Cache), true));
        return commands;
    }

    // The game reads the runtime table; the copy in the work folder is what the
    // next build diffs against.
    private List<CommandSpec> BuildMenuRuntime()
    {
        Context context = GetContext();
        if (!File.Exists(context.MenuCatalog))
            throw new FileNotFoundException("Extract the captions first.", context.MenuCatalog);
        List<CommandSpec> commands = new List<CommandSpec>();
        foreach (string directory in new[] { context.GameDirectory, context.WorkDirectory })
            foreach (string language in new[] { "RU", "EN" })
                commands.Add(new CommandSpec(
                    "menu runtime " + Quote(context.MenuCatalog) + " " +
                    Quote(Path.Combine(directory, "delta_menu." + language.ToLowerInvariant() + ".tsv")) +
                    " --target-lang " + language, false, true));
        return commands;
    }

    private void RunMenuStep(Func<List<CommandSpec>> build)
    {
        try { RunCommands(build(), GetContext(), false); }
        catch (Exception error) { ShowError(error); }
    }

    private void RunMenuPipeline()
    {
        try
        {
            Context context = GetContext();
            List<CommandSpec> commands = new List<CommandSpec> { BuildMenuExtract() };
            commands.AddRange(BuildMenuTranslate());
            commands.AddRange(BuildMenuRuntime());
            RunCommands(commands, context, false);
        }
        catch (Exception error) { ShowError(error); }
    }

    private void OpenMenuCatalog()
    {
        try
        {
            Context context = GetContext();
            if (!File.Exists(context.MenuCatalog))
                throw new FileNotFoundException(
                    "Extract the captions first; there is no catalog to edit yet.", context.MenuCatalog);
            Process.Start(new ProcessStartInfo { FileName = context.MenuCatalog, UseShellExecute = true });
            menuCatalogLink.LinkVisited = true;
        }
        catch (Exception error) { ShowError(error); }
    }

    private void GenerateGameLauncher()
    {
        try
        {
            Context context = GetContext();
            if (!File.Exists(context.Overlay))
            {
                MessageBox.Show(this, "Build the translation overlay before generating a game launcher.", "Delta VN Translator", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string destination = Path.Combine(context.GameDirectory, "DeltaLauncher.exe");
            if (File.Exists(destination) && MessageBox.Show(this, "DeltaLauncher.exe already exists. Replace it?", "Delta VN Translator", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            InstallLauncherAssets(context);
            InstallLauncherRuntime(context);
            File.Copy(Application.ExecutablePath, destination, true);
            Dictionary<string, string> config = DeltaLauncherConfig.Read(context.GameDirectory);
            config["Executable"] = Path.GetFileName(context.Executable);
            config["Profile"] = context.Reika ? "REIKA" : "GENERIC";
            config["Language"] = context.Language;
            DeltaLauncherConfig.Write(context.GameDirectory, config);
            if (context.Reika)
            {
                string runtimeDirectory = Path.Combine(context.WorkDirectory, "runtime");
                Directory.CreateDirectory(runtimeDirectory);
                File.Copy(Application.ExecutablePath, Path.Combine(runtimeDirectory, "DeltaLauncher.exe"), true);
                DeltaLauncherConfig.Write(runtimeDirectory, config);
            }
            AppendLog("Generated " + destination);
            RefreshLauncherOutput();
            SetStatus("Game launcher generated");
        }
        catch (Exception error) { ShowError(error); }
    }

    private void BuildLocalizedUiArchives()
    {
        try
        {
            Context context = GetContext();
            List<CommandSpec> commands = BuildLocalizedArchiveCommands(context);
            RunCommands(commands, context, false, RefreshLocalizedArchivesOutput);
        }
        catch (Exception error) { ShowError(error); }
    }

    private void ExtractLocalizedUiResources()
    {
        try
        {
            Context context = GetContext();
            string cgDirectory = Path.Combine(context.GameDirectory, "CG");
            string assetsDirectory = Path.Combine(context.WorkDirectory, "ui_assets");
            CommandSpec command = new CommandSpec(
                "cgf extract-localizable " + Quote(cgDirectory) + " " + Quote(assetsDirectory),
                false,
                true);
            RunCommands(new[] { command }, context, false, RefreshLocalizedArchivesOutput);
        }
        catch (Exception error) { ShowError(error); }
    }

    private List<CommandSpec> BuildLocalizedArchiveCommands(Context context)
    {
        string cgDirectory = Path.Combine(context.GameDirectory, "CG");
        string sourceDirectory = Path.Combine(context.WorkDirectory, "ui_assets");
        if (!Directory.Exists(cgDirectory))
            throw new DirectoryNotFoundException("The game CG folder was not found: " + cgDirectory);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException("The project UI asset folder was not found: " + sourceDirectory);

        return new List<CommandSpec>
        {
            new CommandSpec(
                "cgf build-localized-set " + Quote(cgDirectory) + " " + Quote(sourceDirectory),
                false,
                true)
        };
    }

    private void InstallLauncherAssets(Context context)
    {
        string cgDirectory = Path.Combine(context.GameDirectory, "CG");
        if (!Directory.Exists(cgDirectory))
            return;

        foreach (string extension in new[] { ".IAF", ".CGF" })
        {
            foreach (string asset in LocalizedResourceNames(cgDirectory, extension))
            {
                string active = Path.Combine(cgDirectory, asset + extension);
                string japanese = Path.Combine(cgDirectory, asset + ".jp" + extension);
                if (!File.Exists(active))
                    throw new FileNotFoundException(
                        "A localized UI resource has no active base file.", active);
                if (!File.Exists(japanese))
                    File.Copy(active, japanese, false);
                File.Copy(japanese, active, true);
            }
        }
    }

    private static string[] LocalizedResourceNames(string directory, string extension)
    {
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in new[] { "ru", "en" })
        {
            string suffix = "." + language + extension;
            foreach (string path in Directory.GetFiles(
                directory, "*." + language + extension, SearchOption.TopDirectoryOnly))
            {
                string file = Path.GetFileName(path);
                if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    names.Add(file.Substring(0, file.Length - suffix.Length));
            }
        }
        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void InstallLauncherRuntime(Context context)
    {
        if (!context.Reika)
            return;

        string binDirectory = Path.Combine(toolsDirectory, "bin");
        string runtimeDirectory = Path.Combine(context.WorkDirectory, "runtime");

        // The proxy and the overlay format are versioned together: an older
        // winmm.dll left in place rejects the new overlay and the game quietly
        // shows Japanese, so this one is refreshed rather than only seeded.
        // bin\ holds the build output and is the only authority - the per-game
        // runtime folder is a staging copy that goes stale on every rebuild.
        string proxy = Path.Combine(binDirectory, "winmm.dll");
        InstallRuntimeFile(proxy, Path.Combine(context.GameDirectory, "winmm.dll"));
        if (Directory.Exists(runtimeDirectory))
            InstallRuntimeFile(proxy, Path.Combine(runtimeDirectory, "winmm.dll"));

        // The overlay the pipeline just built is the authority, exactly like the
        // proxy above. The runtime folder is a staging copy and is refreshed from
        // it; letting that copy win is how a rebuilt overlay used to be replaced
        // by an older one and the game fell back to Japanese.
        foreach (string language in new[] { "ru", "en" })
        {
            string name = "delta_overlay." + language + ".bin";
            string built = Path.Combine(context.WorkDirectory, name);
            string staged = Path.Combine(runtimeDirectory, name);
            string source = IsOverlayFile(built) ? built : staged;
            if (!IsOverlayFile(source))
                continue;

            InstallRuntimeFile(source, Path.Combine(context.GameDirectory, name), false);
            if (source != staged && Directory.Exists(runtimeDirectory))
                InstallRuntimeFile(source, staged, false);

            CopyIfExists(
                Path.Combine(context.WorkDirectory, "delta_menu." + language + ".tsv"),
                Path.Combine(context.GameDirectory, "delta_menu." + language + ".tsv"));
        }
    }

    private void InstallRuntimeFile(string source, string destination, bool keepBackup = true)
    {
        if (!File.Exists(source))
        {
            AppendLog("Runtime component missing from the toolset: " + Path.GetFileName(source));
            return;
        }
        if (string.Equals(
            Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return;
        if (File.Exists(destination))
        {
            if (FilesAreEqual(source, destination))
                return;
            if (keepBackup)
            {
                File.Copy(destination, destination + ".bak", true);
                AppendLog("Updated " + Path.GetFileName(destination) + " (previous copy kept as " +
                    Path.GetFileName(destination) + ".bak)");
            }
            else
                AppendLog("Updated " + Path.GetFileName(destination));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        File.Copy(source, destination, true);
    }

    private static bool FilesAreEqual(string first, string second)
    {
        FileInfo left = new FileInfo(first);
        FileInfo right = new FileInfo(second);
        if (left.Length != right.Length)
            return false;

        using (FileStream leftStream = left.OpenRead())
        using (FileStream rightStream = right.OpenRead())
        {
            byte[] leftBuffer = new byte[65536];
            byte[] rightBuffer = new byte[65536];
            int read;
            while ((read = leftStream.Read(leftBuffer, 0, leftBuffer.Length)) > 0)
            {
                int filled = 0;
                while (filled < read)
                {
                    int got = rightStream.Read(rightBuffer, filled, read - filled);
                    if (got == 0)
                        return false;
                    filled += got;
                }
                for (int index = 0; index < read; index++)
                {
                    if (leftBuffer[index] != rightBuffer[index])
                        return false;
                }
            }
        }
        return true;
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (!File.Exists(source) || string.Equals(
            Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        File.Copy(source, destination, true);
    }

    // Recognises any overlay revision ("RKT" plus a version digit, written by
    // delta_overlay.py and checked exactly by winmm.dll). Pinning this to one
    // revision made a format bump look like a corrupt file to the installer.
    private static bool IsOverlayFile(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            using (FileStream stream = File.OpenRead(path))
            {
                return stream.Length >= 8 && stream.ReadByte() == 'R' && stream.ReadByte() == 'K' &&
                    stream.ReadByte() == 'T' && char.IsDigit((char) stream.ReadByte());
            }
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void RunCommands(
        IEnumerable<CommandSpec> commands,
        Context context,
        bool unlockLauncher,
        Action successAction = null)
    {
        List<CommandSpec> commandList = commands.ToList();
        if (commandList.Any(command => !command.NativeResource) && !HasCompatiblePython)
        {
            MessageBox.Show(this, pythonDetectionMessage, "Python requirement", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string python = pythonExecutable;
        string apiKey = tokenBox.Text.Trim();
        translationStopRequested = false;
        runHasWarning = false;
        proofreadWarningLabel.Visible = false;
        overlayWarningLabel.Visible = false;
        SetBusy(true);
        ThreadPool.QueueUserWorkItem(delegate
        {
            bool success = true;
            try
            {
                foreach (CommandSpec command in commandList)
                {
                    AppendLog((command.NativeResource ? "> DeltaResourceTool " : "> python ") + command.Arguments);
                    int exitCode = command.NativeResource
                        ? RunResource(command)
                        : RunPython(command, context, python, apiKey);
                    if (exitCode != 0)
                    {
                        success = false;
                        AppendLog((command.NativeResource ? "Resource command" : "Python command") +
                            " failed with exit code " + exitCode);
                        break;
                    }
                }
                if (success && successAction != null)
                    successAction();
            }
            catch (Exception error)
            {
                success = false;
                AppendLog("ERROR: " + Context.Describe(error));
                DeltaDiagnosticLog.Write(error);
            }
            finally
            {
                if (success && unlockLauncher)
                    SetLauncherAvailable(true);
                RefreshExtractOutput();
                RefreshProofreadRulesLink();
                RefreshMenuStatus();
                RefreshOverlayOutput();
                RefreshLocalizedArchivesOutput();
                SetBusy(false);
                bool stopped = translationStopRequested;
                translationStopRequested = false;
                string finalStatus = stopped ? "Stopped" : success
                    ? (runHasWarning ? "Completed with warnings" : "Completed")
                    : "Failed";
                SetStatus(finalStatus);
                DeltaDiagnosticLog.Write("Run finished: " + finalStatus);
            }
        });
    }

    private int RunResource(CommandSpec command)
    {
        string executable = Path.Combine(toolsDirectory, "bin", "DeltaResourceTool.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException(
                "The native resource backend is missing. Rebuild the translator first.", executable);
        ProcessStartInfo start = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = command.Arguments,
            WorkingDirectory = toolsDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using (Process process = new Process { StartInfo = start, EnableRaisingEvents = true })
        {
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
                { if (args.Data != null) AppendLog(args.Data); };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
                { if (args.Data != null) AppendLog(args.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    private const string ProtectedTokenPrefix = "dpapi-current-user-v1:";
    private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes(
        "Delta VN Translator DeepL token v1");

    private static string ProtectToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return string.Empty;
        byte[] encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token), TokenEntropy, DataProtectionScope.CurrentUser);
        return ProtectedTokenPrefix + Convert.ToBase64String(encrypted);
    }

    private static string LoadProtectedToken(string protectedValue)
    {
        if (protectedValue.Length == 0)
            return string.Empty;
        if (!protectedValue.StartsWith(ProtectedTokenPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("Unknown DeepL token protection format.");
        byte[] encrypted = Convert.FromBase64String(
            protectedValue.Substring(ProtectedTokenPrefix.Length));
        byte[] plaintext = ProtectedData.Unprotect(
            encrypted, TokenEntropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plaintext);
    }

    private int RunPython(CommandSpec command, Context context, string python, string apiKey)
    {
        int space = command.Arguments.IndexOf(' ');
        string arguments = Quote(Path.Combine(toolsDirectory, "py", command.Arguments.Split(new[] { ' ' }, 2)[0])) +
            (space >= 0 ? command.Arguments.Substring(space) : "");
        bool sendApiKey = command.UsesDeepL && !string.IsNullOrWhiteSpace(apiKey);
        if (sendApiKey)
            arguments += " --api-key-stdin";
        string cancelFile = null;
        if (command.UsesDeepL)
        {
            // Beside the executable instead of in %TEMP%. The toolset is meant
            // to stay portable, and this was the one file it put on the system
            // drive. Naming it after the process stops two translators from
            // sharing one stop file, and lets the sweep at startup tell a file
            // left behind by a crash from one a running instance still needs.
            cancelFile = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "delta-cancel-" + Process.GetCurrentProcess().Id + ".stop");
            // A leftover under the same name would read as "already stopped".
            try { File.Delete(cancelFile); }
            catch { }
            arguments += " --cancel-file " + Quote(cancelFile);
        }
        ProcessStartInfo start = DeltaPython.Start(python, arguments, toolsDirectory, true);
        start.RedirectStandardInput = sendApiKey;

        try
        {
            using (Process process = new Process { StartInfo = start, EnableRaisingEvents = true })
            {
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) { if (args.Data != null) AppendLog(args.Data); };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { if (args.Data != null) AppendLog(args.Data); };
                try { process.Start(); }
                catch (Exception error)
                {
                    throw new InvalidOperationException("Could not start the detected Python runtime. Restart the translator or reinstall Python 3.10+.", error);
                }
                if (cancelFile != null)
                    SetActiveCancelFile(cancelFile);
                if (sendApiKey)
                {
                    process.StandardInput.WriteLine(apiKey);
                    process.StandardInput.Close();
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                return process.ExitCode;
            }
        }
        finally
        {
            ClearActiveCancelFile(cancelFile);
        }
    }

    private void SetActiveCancelFile(string path)
    {
        lock (cancellationSync)
            activeCancelFile = path;
        SetStopEnabled(true);
    }

    private void ClearActiveCancelFile(string path)
    {
        lock (cancellationSync)
        {
            if (string.Equals(activeCancelFile, path, StringComparison.OrdinalIgnoreCase))
                activeCancelFile = null;
        }
        SetStopEnabled(false);
        if (!string.IsNullOrEmpty(path))
        {
            try { File.Delete(path); }
            catch { }
        }
    }

    private void RequestTranslationStop()
    {
        string path;
        lock (cancellationSync)
            path = activeCancelFile;
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            translationStopRequested = true;
            File.WriteAllText(path, "stop", Encoding.ASCII);
            stopButton.Enabled = false;
            SetStatus("Stopping...");
            AppendLog("Stop requested. Waiting for the current API request to finish...");
        }
        catch (Exception error)
        {
            translationStopRequested = false;
            ShowError(error);
        }
    }

    private void SetStopEnabled(bool enabled)
    {
        Action action = delegate { stopButton.Enabled = enabled; };
        if (InvokeRequired) BeginInvoke(action); else action();
    }

    private void SetBusy(bool busy)
    {
        Action action = delegate
        {
            foreach (Button button in actionButtons)
                button.Enabled = !busy &&
                    (button != generateLauncherButton || launcherAvailable) &&
                    (button != extractLocalizedArchivesButton || LocalizedArchiveStageAvailable()) &&
                    (button != buildLocalizedArchivesButton || LocalizedArchiveStageAvailable());
            UseWaitCursor = busy;
            SetStatus(busy ? "Working..." : "Ready");
        };
        if (InvokeRequired) BeginInvoke(action); else action();
    }

    private bool LocalizedArchiveStageAvailable()
    {
        try
        {
            return ProjectIsReady() &&
                Directory.Exists(Path.Combine(GetContext().GameDirectory, "CG"));
        }
        catch { return false; }
    }

    private void SetLauncherAvailable(bool value)
    {
        Action action = delegate
        {
            launcherAvailable = value;
            if (!UseWaitCursor)
                generateLauncherButton.Enabled = value;
        };
        if (InvokeRequired) BeginInvoke(action); else action();
    }

    private void RefreshLauncherAvailability()
    {
        if (generateLauncherButton == null)
            return;
        try
        {
            Context context = GetContext();
            SetLauncherAvailable(File.Exists(context.Overlay));
        }
        catch
        {
            SetLauncherAvailable(false);
        }
    }

    private void SetStatus(string value)
    {
        if (InvokeRequired) { BeginInvoke((Action) delegate { SetStatus(value); }); return; }
        statusLabel.Text = value;
    }

    private void AppendLog(string line)
    {
        if (line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
            runHasWarning = true;
        if (InvokeRequired) { BeginInvoke((Action) delegate { AppendLog(line); }); return; }
        DeltaDiagnosticLog.Write(line);
        logBox.AppendText(line + Environment.NewLine);
        if (line.StartsWith(
            "WARNING: unresolved looped translations remain after proofread:",
            StringComparison.OrdinalIgnoreCase))
        {
            proofreadWarningLabel.Text = line;
            proofreadWarningLabel.Visible = true;
        }
        if (line.StartsWith(
            "WARNING: unsupported characters replaced with '?':",
            StringComparison.OrdinalIgnoreCase))
        {
            overlayWarningLabel.Text = line;
            overlayWarningLabel.Visible = true;
        }
        const string prefix = "Estimated API characters: ";
        if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            long parsed;
            if (long.TryParse(
                line.Substring(prefix.Length),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out parsed))
            {
                estimateCharacters = parsed;
                RefreshEstimateLabel();
            }
        }
        const string dialogWindowsPrefix = "Dialog windows sent: ";
        if (line.StartsWith(dialogWindowsPrefix, StringComparison.OrdinalIgnoreCase))
            dialogWindowsLabel.Text = line;
        logBox.SelectionStart = logBox.TextLength;
        logBox.ScrollToCaret();
    }

    private static TextBox NewRunWarningTextBox()
    {
        return new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ShortcutsEnabled = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(12, 13, 16),
            Width = 540,
            Height = 48,
            ForeColor = Color.FromArgb(238, 151, 91),
            Font = new Font("Segoe UI Semibold", 9F),
            Margin = new Padding(0, 12, 0, 0),
            WordWrap = true,
            HideSelection = false,
            Cursor = Cursors.IBeam,
            Visible = false
        };
    }

    private void ShowError(Exception error)
    {
        DeltaDiagnosticLog.Write(error);
        MessageBox.Show(this, Context.Describe(error), "Delta VN Translator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static string Quote(string value)
    {
        return Context.QuoteArgument(value);
    }

}

internal static class DeltaTranslatorProgram
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string folder = AppDomain.CurrentDomain.BaseDirectory;
        try
        {
            string previousBuild = Path.Combine(folder, "DeltaTranslator.previous.exe");
            if (File.Exists(previousBuild))
                File.Delete(previousBuild);
        }
        catch
        {
        }
        string launcherConfig = Path.Combine(folder, "delta_launcher.ini");
        if (File.Exists(launcherConfig))
        {
            Application.Run(new GeneratedDeltaLauncherForm(folder));
            return;
        }
        RemoveStaleCancelFiles(folder);
        DeltaDiagnosticLog.Start();
        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs args)
        {
            DeltaDiagnosticLog.Write(args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
        {
            Exception error = args.ExceptionObject as Exception;
            DeltaDiagnosticLog.Write(error ?? new Exception(Convert.ToString(args.ExceptionObject)));
        };
        Application.Run(new DeltaTranslatorForm());
    }

    // A stop file is named after the instance that made it, so one abandoned
    // by a crash can be told from one a second translator is using right now.
    // Only a file belonging to a process that is gone gets removed.
    private static void RemoveStaleCancelFiles(string folder)
    {
        try
        {
            foreach (string path in Directory.GetFiles(folder, "delta-cancel-*.stop"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (!name.StartsWith("delta-cancel-", StringComparison.OrdinalIgnoreCase))
                    continue;
                int id;
                if (!int.TryParse(name.Substring("delta-cancel-".Length), out id))
                    continue;
                bool running;
                try
                {
                    using (Process.GetProcessById(id))
                        running = true;
                }
                catch (ArgumentException)
                {
                    running = false;
                }
                if (running)
                    continue;
                try { File.Delete(path); }
                catch { }
            }
        }
        catch
        {
        }
    }
}

internal static class DeltaLauncherConfig
{
    private static readonly string[] LauncherKeys = { "Executable", "Profile", "Language" };
    private static readonly string[] OverlayKeys = { "TEXT_X", "FONT_HEIGHT", "LETTER_SPACING", "LOG_UNTRANSLATED" };

    public static Dictionary<string, string> Read(string folder)
    {
        string path = Path.Combine(folder, "delta_launcher.ini");
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return result;

        foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '#' || trimmed[0] == '[')
                continue;
            int separator = trimmed.IndexOf('=');
            if (separator > 0)
                result[trimmed.Substring(0, separator).Trim()] = trimmed.Substring(separator + 1).Trim();
        }
        return result;
    }

    public static void Write(string folder, Dictionary<string, string> values)
    {
        SetDefault(values, "Executable", "RSA.EXE");
        SetDefault(values, "Profile", "GENERIC");
        SetDefault(values, "Language", "JP");
        SetDefault(values, "TEXT_X", "32");
        SetDefault(values, "FONT_HEIGHT", "20");
        SetDefault(values, "LETTER_SPACING", "0");
        SetDefault(values, "LOG_UNTRANSLATED", "0");

        StringBuilder output = new StringBuilder();
        WriteSection(output, "Launcher", LauncherKeys, values);
        output.AppendLine();
        WriteSection(output, "Overlay", OverlayKeys, values);
        File.WriteAllText(
            Path.Combine(folder, "delta_launcher.ini"),
            output.ToString(),
            new UTF8Encoding(false));
    }

    private static void SetDefault(Dictionary<string, string> values, string key, string value)
    {
        if (!values.ContainsKey(key) || string.IsNullOrWhiteSpace(values[key]))
            values[key] = value;
    }

    private static void WriteSection(
        StringBuilder output,
        string section,
        IEnumerable<string> keys,
        Dictionary<string, string> values)
    {
        output.Append('[').Append(section).AppendLine("]");
        foreach (string key in keys)
            output.Append(key).Append('=').AppendLine(values[key]);
    }
}

internal sealed class GeneratedDeltaLauncherForm : Form
{
    private readonly string gameDirectory;
    private readonly string executable;
    private readonly string profile;
    private readonly RadioButton japanese;
    private readonly RadioButton russian;
    private readonly RadioButton english;

    public GeneratedDeltaLauncherForm(string folder)
    {
        gameDirectory = folder;
        Dictionary<string, string> config = DeltaLauncherConfig.Read(folder);
        profile = config.ContainsKey("Profile") ? config["Profile"] : "GENERIC";
        string configuredExecutable = config.ContainsKey("Executable") ? config["Executable"] : "";
        executable = File.Exists(Path.Combine(folder, configuredExecutable))
            ? Path.Combine(folder, configuredExecutable)
            : Directory.GetFiles(folder, "*.exe")
                .FirstOrDefault(path => !string.Equals(Path.GetFileName(path), "DeltaLauncher.exe", StringComparison.OrdinalIgnoreCase));

        Text = "Delta Game Launcher";
        ClientSize = new Size(390, 202);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(246, 247, 249);

        try
        {
            if (!string.IsNullOrEmpty(executable))
                Icon = Icon.ExtractAssociatedIcon(executable);
        }
        catch
        {
        }

        Controls.Add(new Label
        {
            Text = "Choose game language",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 14F, FontStyle.Bold, GraphicsUnit.Point),
            Location = new Point(22, 18)
        });
        Controls.Add(new Label
        {
            Text = "The selection is remembered for the next launch.",
            AutoSize = true,
            ForeColor = Color.FromArgb(85, 90, 99),
            Location = new Point(24, 52)
        });
        GroupBox languages = new GroupBox
        {
            Location = new Point(22, 78),
            Size = new Size(346, 62),
            Text = "Language"
        };
        japanese = CreateLanguageButton("Japanese", 16);
        russian = CreateLanguageButton("Russian", 122);
        english = CreateLanguageButton("English", 235);
        languages.Controls.Add(japanese);
        languages.Controls.Add(russian);
        languages.Controls.Add(english);
        Controls.Add(languages);

        Button launch = new Button
        {
            Location = new Point(137, 155),
            Size = new Size(116, 30),
            Text = "Launch",
            UseVisualStyleBackColor = true
        };
        launch.Click += delegate { Launch(); };
        Controls.Add(launch);
        AcceptButton = launch;
        UpdateLanguageAvailability();
        SelectSavedLanguage(config);
    }

    private static RadioButton CreateLanguageButton(string text, int x)
    {
        return new RadioButton
        {
            AutoSize = true,
            Location = new Point(x, 26),
            Text = text,
            UseVisualStyleBackColor = true
        };
    }

    private void SelectSavedLanguage(Dictionary<string, string> config)
    {
        japanese.Checked = true;
        string language = config.ContainsKey("Language")
            ? config["Language"].Trim().ToUpperInvariant()
            : "JP";
        if ((language == "RU" && !russian.Enabled) || (language == "EN" && !english.Enabled))
            language = "JP";
        japanese.Checked = language == "JP";
        english.Checked = language == "EN";
        russian.Checked = language != "JP" && language != "EN";
    }

    private void UpdateLanguageAvailability()
    {
        const string overlayName = "delta_overlay";
        japanese.Enabled = true;
        russian.Enabled = File.Exists(Path.Combine(gameDirectory, overlayName + ".ru.bin")) && HasLanguageAssets("RU");
        english.Enabled = File.Exists(Path.Combine(gameDirectory, overlayName + ".en.bin")) && HasLanguageAssets("EN");
    }

    private bool HasLanguageAssets(string language)
    {
        string cgDirectory = Path.Combine(gameDirectory, "CG");
        if (!Directory.Exists(cgDirectory)) return true;
        return GetLocalizedAssets().All(asset =>
                File.Exists(Path.Combine(cgDirectory, asset + ".IAF")) &&
                File.Exists(Path.Combine(cgDirectory, asset + ".jp.IAF"))) &&
            GetLocalizedArchives().All(asset =>
                File.Exists(Path.Combine(cgDirectory, asset + ".CGF")) &&
                File.Exists(Path.Combine(cgDirectory, asset + ".jp.CGF")));
    }

    private string[] GetLocalizedAssets()
    {
        string cgDirectory = Path.Combine(gameDirectory, "CG");
        if (!Directory.Exists(cgDirectory))
            return new string[0];

        return GetLocalizedResourceNames(cgDirectory, ".IAF");
    }

    private string[] GetLocalizedArchives()
    {
        string cgDirectory = Path.Combine(gameDirectory, "CG");
        if (!Directory.Exists(cgDirectory))
            return new string[0];

        return GetLocalizedResourceNames(cgDirectory, ".CGF");
    }

    private static string[] GetLocalizedResourceNames(string directory, string extension)
    {
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in new[] { "ru", "en" })
        {
            string suffix = "." + language + extension;
            foreach (string path in Directory.GetFiles(
                directory, "*." + language + extension, SearchOption.TopDirectoryOnly))
            {
                string file = Path.GetFileName(path);
                if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    names.Add(file.Substring(0, file.Length - suffix.Length));
            }
        }
        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void Launch()
    {
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
        {
            MessageBox.Show(this, "The configured game executable was not found.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        string language = japanese.Checked ? "JP" : english.Checked ? "EN" : "RU";
        const string overlayName = "delta_overlay";
        if (language != "JP")
        {
            string overlay = Path.Combine(gameDirectory, overlayName + "." + language.ToLowerInvariant() + ".bin");
            if (!File.Exists(overlay))
            {
                MessageBox.Show(this, "The " + language + " translation overlay is missing.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        try
        {
            SelectLanguageAssets(language);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Dictionary<string, string> config = DeltaLauncherConfig.Read(gameDirectory);
        config["Language"] = language;
        DeltaLauncherConfig.Write(gameDirectory, config);

        Dictionary<string, string> menu = language == "JP"
            ? new Dictionary<string, string>()
            : DeltaMenuRuntime.ReadMap(Path.Combine(gameDirectory, "delta_menu." + language.ToLowerInvariant() + ".tsv"));
        try
        {
            Process game = Process.Start(new ProcessStartInfo { FileName = executable, WorkingDirectory = gameDirectory, UseShellExecute = true });
            Hide();
            ThreadPool.QueueUserWorkItem(delegate
            {
                DeltaMenuRuntime.Monitor(
                    game,
                    menu,
                    language,
                    profile.Equals("REIKA", StringComparison.OrdinalIgnoreCase),
                    delegate { });
                BeginInvoke((Action)delegate { Close(); });
            });
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void SelectLanguageAssets(string language)
    {
        string cgDirectory = Path.Combine(gameDirectory, "CG");
        string[] assets = GetLocalizedAssets();
        List<string> selectedAssets = new List<string>();
        foreach (string asset in assets)
        {
            string active = Path.Combine(cgDirectory, asset + ".IAF");
            string japanese = Path.Combine(cgDirectory, asset + ".jp.IAF");
            if (!File.Exists(active))
                throw new FileNotFoundException("A Reika interface sprite is missing.", active);
            if (!File.Exists(japanese))
                throw new FileNotFoundException("The Japanese UI resource backup is missing.", japanese);

            string localized = Path.Combine(
                cgDirectory, asset + "." + language.ToLowerInvariant() + ".IAF");
            string selected = language == "JP" || !File.Exists(localized) ? japanese : localized;
            selectedAssets.Add(selected);
        }

        for (int index = 0; index < assets.Length; index++)
        {
            string active = Path.Combine(cgDirectory, assets[index] + ".IAF");
            string temporary = active + ".delta.tmp";
            File.Copy(selectedAssets[index], temporary, true);
            try
            {
                if (File.Exists(active))
                    File.Replace(temporary, active, null, true);
                else
                    File.Move(temporary, active);
            }
            catch (PlatformNotSupportedException)
            {
                if (File.Exists(active))
                    File.Delete(active);
                File.Move(temporary, active);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        foreach (string archive in GetLocalizedArchives())
        {
            string active = Path.Combine(cgDirectory, archive + ".CGF");
            string japanese = Path.Combine(cgDirectory, archive + ".jp.CGF");
            string localized = Path.Combine(
                cgDirectory, archive + "." + language.ToLowerInvariant() + ".CGF");
            string selected = language == "JP" || !File.Exists(localized) ? japanese : localized;
            if (!File.Exists(active))
                throw new FileNotFoundException("A Delta resource archive is missing.", active);
            if (!File.Exists(selected))
                throw new FileNotFoundException("The Japanese UI archive backup is missing.", selected);

            string temporary = active + ".delta.tmp";
            File.Copy(selected, temporary, true);
            try
            {
                File.Replace(temporary, active, null, true);
            }
            catch (PlatformNotSupportedException)
            {
                if (File.Exists(active))
                    File.Delete(active);
                File.Move(temporary, active);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }

}

internal static class DeltaMenuRuntime
{
    private const uint MfByPosition = 0x00000400;
    private const uint MfPopup = 0x00000010;

    [DllImport("user32.dll")]
    private static extern IntPtr GetMenu(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr menu, int position);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr menu, uint position, StringBuilder text, int maximum, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ModifyMenu(IntPtr menu, uint position, uint flags, IntPtr newItem, string text);
    [DllImport("user32.dll")]
    private static extern bool DrawMenuBar(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr window, string text);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    public static Dictionary<string, string> ReadMap(string path)
    {
        Dictionary<string, string> result = new Dictionary<string, string>();
        if (!File.Exists(path)) return result;
        foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
        {
            string[] fields = line.Split(new[] { '\t' }, 2);
            if (fields.Length != 2) continue;
            try
            {
                string source = Encoding.UTF8.GetString(Convert.FromBase64String(fields[0])).Trim();
                string translated = Encoding.UTF8.GetString(Convert.FromBase64String(fields[1])).Trim();
                if (source.Length > 0 && translated.Length > 0) result[source] = translated;
            }
            catch (FormatException) { }
        }
        return result;
    }

    public static void Monitor(
        Process game,
        Dictionary<string, string> translations,
        string language,
        bool reika,
        Action<string> log)
    {
        while (!game.HasExited)
        {
            uint pid = (uint)game.Id;
            EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                uint windowPid;
                GetWindowThreadProcessId(window, out windowPid);
                if (windowPid == pid && language != "JP")
                {
                    if (reika)
                        LocalizeReikaWindow(window, translations, language);
                    else
                        LocalizeWindow(window, translations);
                }
                return true;
            }, IntPtr.Zero);
            Application.DoEvents();
            Thread.Sleep(200);
        }
        log("Game exited.");
    }

    private static void LocalizeWindow(IntPtr window, Dictionary<string, string> translations)
    {
        IntPtr menu = GetMenu(window);
        if (menu == IntPtr.Zero) return;
        LocalizeMenu(menu, translations);
        DrawMenuBar(window);
    }

    private static void LocalizeReikaWindow(
        IntPtr window,
        Dictionary<string, string> generated,
        string language)
    {
        Dictionary<string, string> translations = BuiltInReikaTranslations(language);
        foreach (KeyValuePair<string, string> pair in generated)
            translations[pair.Key] = pair.Value;

        IntPtr menu = GetMenu(window);
        if (menu != IntPtr.Zero)
        {
            SetWindowText(
                window,
                language == "RU"
                    ? "Рейка: Дрессировка породистой суки"
                    : "Reika: Pedigree Bitch Training");
        }
        else
        {
            LocalizeWindowCaption(window, translations);
        }

        if (menu != IntPtr.Zero)
        {
            int count = GetMenuItemCount(menu);
            for (int position = 0; position < count; position++)
            {
                StringBuilder original = new StringBuilder(256);
                GetMenuString(menu, (uint)position, original, original.Capacity, MfByPosition);
                string translated;
                if (translations.TryGetValue(original.ToString().Trim(), out translated))
                {
                    IntPtr submenu = GetSubMenu(menu, position);
                    ModifyMenu(menu, (uint)position, MfByPosition | MfPopup, submenu, translated);
                }
            }
            LocalizeMenu(menu, translations);
            DrawMenuBar(window);
        }

        LocalizeReikaControls(window, translations);
    }

    private static void LocalizeWindowCaption(
        IntPtr window,
        Dictionary<string, string> translations)
    {
        StringBuilder current = new StringBuilder(256);
        GetWindowText(window, current, current.Capacity);
        string translated;
        if (translations.TryGetValue(current.ToString().Trim(), out translated))
            SetWindowText(window, translated);
    }

    private static void LocalizeReikaControls(
        IntPtr window,
        Dictionary<string, string> translations)
    {
        EnumChildWindows(window, delegate(IntPtr child, IntPtr unused)
        {
            StringBuilder current = new StringBuilder(256);
            GetWindowText(child, current, current.Capacity);
            string text = current.ToString().Trim();
            string translated;
            if (translations.TryGetValue(text, out translated))
                SetWindowText(child, translated);
            return true;
        }, IntPtr.Zero);
    }

    private static Dictionary<string, string> BuiltInReikaTranslations(string language)
    {
        bool russian = language == "RU";
        Dictionary<string, string> result = new Dictionary<string, string>();
        if (russian)
        {
            result["最初に戻る"] = "С начала";
            result["メニューに戻る"] = "В главное меню";
            result["セーブする..."] = "Сохранить...";
            result["ロードする..."] = "Загрузить...";
            result["クイックセーブ"] = "Быстрое сохранение";
            result["クイックロード"] = "Быстрая загрузка";
            result["ゲームの終了"] = "Выйти из игры";
            result["ファイル"] = "Файл";
            result["メッセージ"] = "Сообщения";
            result["オプション"] = "Параметры";
            result["画面サイズ"] = "Размер экрана";
            result["メッセージ自動送り"] = "Автоматическая прокрутка сообщений";
            result["メッセージ巻き戻し"] = "Прокрутка сообщений назад";
            result["メッセージ消去"] = "Удалить сообщение";
            result["メッセージを消す"] = "Убрать сообщение";
            result["スキップ"] = "Пропустить";
            result["画面効果設定"] = "Настройки визуальных эффектов";
            result["画面表示設定"] = "Настройки отображения";
            result["操作設定"] = "Настройки управления";
            result["音響効果設定"] = "Настройки звуковых эффектов";
            result["音響設定"] = "Настройки звука";
            result["フルスクリーン"] = "Полный экран";
            result["ヘルプ"] = "Справка";
            result["Route2 web site"] = "Веб-сайт Route2";
            result["BMPファイルを作成する"] = "Создать файл BMP";
            result["パラメータ表示"] = "Показать параметры";
            result["旧オプション"] = "Старые настройки";
            result["旧ボリューム"] = "Старая громкость";
            result["ﾌｧｲﾙ"] = "Файл";
            result["画面表示"] = "Отображение экрана";
            result["ウィンドウ"] = "Окно";
            result["文章色変更"] = "Изменение цвета текста";
            result["通常色"] = "Обычный цвет";
            result["既読色"] = "Цвет прочитанного";
            result["バックログ色"] = "Цвет журнала";
            result["アンチエイリアスを使用する"] = "Использовать сглаживание";
            result["文字に影を入れる"] = "Добавлять тень к тексту";
            result["ウィンドウ透過率"] = "Прозрачность окна";
            result["初期化"] = "Сбросить";
            result["取り消し"] = "Отмена";
            result["決定"] = "Применить";
            result["音響効果系"] = "Звуковые эффекты";
            result["操作系"] = "Управление";
            result["音量設定"] = "Настройка громкости";
            result["文字色の変更"] = "Изменение цвета текста";
            result["画面効果"] = "Эффекты экрана";
            result["フレーム表示"] = "Показывать рамку";
            result["ﾌｫﾝﾄｽﾑｰｼﾞﾝｸﾞ"] = "Сглаживание шрифта";
            result["影"] = "Тень";
            result["音楽"] = "Музыка";
            result["ボイス"] = "Голоса";
            result["効果音"] = "Звуковые эффекты";
            result["ボリュームの設定を記録する。"] = "Сохранять настройки громкости.";
            result["Ctrlキー"] = "Клавиша Ctrl";
            result["Shiftキー"] = "Клавиша Shift";
            result["通常"] = "Обычно";
            result["速い"] = "Быстро";
            result["遅い"] = "Медленно";
            result["普通"] = "Обычно";
            result["通常の文字色"] = "Обычный цвет текста";
            result["巻き戻り時の文字色"] = "Цвет текста при перемотке";
            result["既読文字色"] = "Цвет прочитанного текста";
            result["通常時表示速度テスト"] = "Тест скорости отображения";
            result["自動送り時表示速度テスト"] = "Тест скорости автопрокрутки";
            result["文字に影有無テスト"] = "Тест тени текста";
            result["文字速度"] = "Скорость текста";
            result["自動速度"] = "Автоскорость";
            result["既読スキップ"] = "Пропуск прочитанного";
            result["未読スキップ"] = "Пропуск непрочитанного";
            result["スキップ用キー"] = "Клавиша пропуска";
            result["可"] = "Да";
            result["不可"] = "Нет";
            result["はい"] = "Да";
            result["いいえ"] = "Нет";
            result["OK"] = "OK";
            result["ｷｬﾝｾﾙ"] = "Отмена";
        }
        else
        {
            result["最初に戻る"] = "Restart";
            result["メニューに戻る"] = "Return to Main Menu";
            result["セーブする..."] = "Save...";
            result["ロードする..."] = "Load...";
            result["クイックセーブ"] = "Quick Save";
            result["クイックロード"] = "Quick Load";
            result["ゲームの終了"] = "Exit Game";
            result["ファイル"] = "File";
            result["メッセージ"] = "Messages";
            result["オプション"] = "Options";
            result["画面サイズ"] = "Screen Size";
            result["メッセージ自動送り"] = "Auto-Advance Messages";
            result["メッセージ巻き戻し"] = "Rewind Messages";
            result["メッセージ消去"] = "Clear Messages";
            result["メッセージを消す"] = "Clear Message";
            result["スキップ"] = "Skip";
            result["画面効果設定"] = "Visual Effects Settings";
            result["画面表示設定"] = "Display Settings";
            result["操作設定"] = "Control Settings";
            result["音響効果設定"] = "Sound Effects Settings";
            result["音響設定"] = "Sound Settings";
            result["フルスクリーン"] = "Fullscreen";
            result["ヘルプ"] = "Help";
            result["Route2 web site"] = "Route2 Website";
            result["BMPファイルを作成する"] = "Create BMP File";
            result["パラメータ表示"] = "Show Parameters";
            result["旧オプション"] = "Legacy Options";
            result["旧ボリューム"] = "Legacy Volume";
            result["ﾌｧｲﾙ"] = "File";
            result["画面表示"] = "Screen Display";
            result["ウィンドウ"] = "Window";
            result["文章色変更"] = "Text Color Change";
            result["通常色"] = "Normal Color";
            result["既読色"] = "Read Color";
            result["バックログ色"] = "Backlog Color";
            result["アンチエイリアスを使用する"] = "Use anti-aliasing";
            result["文字に影を入れる"] = "Add a text shadow";
            result["ウィンドウ透過率"] = "Window Opacity";
            result["初期化"] = "Reset";
            result["取り消し"] = "Cancel";
            result["決定"] = "Apply";
            result["音響効果系"] = "Sound Effects";
            result["操作系"] = "Controls";
            result["音量設定"] = "Volume Settings";
            result["文字色の変更"] = "Text Color Change";
            result["画面効果"] = "Visual Effects";
            result["フレーム表示"] = "Show Frame";
            result["ﾌｫﾝﾄｽﾑｰｼﾞﾝｸﾞ"] = "Font Smoothing";
            result["影"] = "Shadow";
            result["音楽"] = "Music";
            result["ボイス"] = "Voices";
            result["効果音"] = "Sound Effects";
            result["ボリュームの設定を記録する。"] = "Remember volume settings.";
            result["Ctrlキー"] = "Ctrl Key";
            result["Shiftキー"] = "Shift Key";
            result["通常"] = "Normal";
            result["速い"] = "Fast";
            result["遅い"] = "Slow";
            result["普通"] = "Normal";
            result["通常の文字色"] = "Normal Text Color";
            result["巻き戻り時の文字色"] = "Rewind Text Color";
            result["既読文字色"] = "Read Text Color";
            result["通常時表示速度テスト"] = "Display Speed Test";
            result["自動送り時表示速度テスト"] = "Auto-Advance Speed Test";
            result["文字に影有無テスト"] = "Text Shadow Test";
            result["文字速度"] = "Text Speed";
            result["自動速度"] = "Auto Speed";
            result["既読スキップ"] = "Skip Read Text";
            result["未読スキップ"] = "Skip Unread Text";
            result["スキップ用キー"] = "Skip Key";
            result["可"] = "Yes";
            result["不可"] = "No";
            result["はい"] = "Yes";
            result["いいえ"] = "No";
            result["OK"] = "OK";
            result["ｷｬﾝｾﾙ"] = "Cancel";
        }
        return result;
    }

    private static void LocalizeMenu(IntPtr menu, Dictionary<string, string> translations)
    {
        int count = GetMenuItemCount(menu);
        for (int position = 0; position < count; position++)
        {
            StringBuilder original = new StringBuilder(256);
            GetMenuString(menu, (uint)position, original, original.Capacity, MfByPosition);
            string text = original.ToString();
            string shortcut = string.Empty;
            int tab = text.IndexOf('\t');
            if (tab >= 0) { shortcut = text.Substring(tab); text = text.Substring(0, tab); }
            string translated;
            if (translations.TryGetValue(text.Trim(), out translated))
            {
                IntPtr submenu = GetSubMenu(menu, position);
                IntPtr item = submenu;
                uint flags = MfByPosition | (submenu != IntPtr.Zero ? MfPopup : 0);
                if (submenu == IntPtr.Zero)
                {
                    uint commandId = GetMenuItemID(menu, position);
                    if (commandId != uint.MaxValue) item = new IntPtr(unchecked((int)commandId));
                }
                ModifyMenu(menu, (uint)position, flags, item, translated + shortcut);
            }
            IntPtr child = GetSubMenu(menu, position);
            if (child != IntPtr.Zero) LocalizeMenu(child, translations);
        }
    }
}
