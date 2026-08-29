#include "pch.h"
#include "DeltaOverlay.h"

#include <cstdio>

using namespace std;

namespace
{
// RSA.EXE draws a message line from three places: the scenario path
// (DrawRow -> DrawLine) and two redraw paths that replay the 10x96 byte row
// cache at 0x66bd60 when the window is repainted, for example while the button
// bar slides out. All three have to be hooked or the Japanese text comes back.
using DrawRowFunc = void(__cdecl*)(const char*, int);
using DrawLineFunc = void(__cdecl*)(const char*, int, int, int);
using RedrawLineFunc = void(__fastcall*)(void*, void*, int, int, int, const char*, int);
using RedrawPlainFunc = void(__cdecl*)(const char*, int, int);
using DrawStringFunc = void(__fastcall*)(void*, void*, int, int, int, const char*);

// Offsets into the 2007-06-07 RSA.EXE (462,848 bytes).
constexpr uintptr_t DrawRowRva = 0x1cae0;       // DrawRow(text, row)
constexpr uintptr_t DrawLineRva = 0x19890;      // DrawLine(text, x, y, cacheFlag)
constexpr uintptr_t RedrawLineRva = 0xfe30;     // thiscall, replays one cached row
constexpr uintptr_t RedrawPlainRva = 0x1a760;   // cdecl, replays one cached row
constexpr uintptr_t DrawStringRva = 0xea60;     // thiscall, smoothed glyph blitter
constexpr uintptr_t TextOriginXRva = 0x2736ec;  // message text origin, parsed from MS.MHU
constexpr uintptr_t FontHeightRva = 0x26c140;   // message font height, advance is (height + 2) / 2

constexpr BYTE ExpectedDrawRow[] = { 0x56, 0x57, 0x8B, 0x7C, 0x24, 0x10, 0x85, 0xFF };
constexpr BYTE ExpectedDrawLine[] = { 0xA1, 0x04, 0x68, 0x67, 0x00, 0x83, 0xEC, 0x10 };
constexpr BYTE ExpectedRedrawLine[] = { 0x83, 0xEC, 0x10, 0x55, 0x8B, 0x6C, 0x24, 0x24 };
constexpr BYTE ExpectedRedrawPlain[] = { 0x51, 0x8A, 0x0D, 0x0C, 0xB0, 0x66, 0x00, 0x53 };
constexpr BYTE ExpectedDrawString[] = { 0x83, 0xEC, 0x28, 0x53, 0x8B, 0x5C, 0x24, 0x3C };

constexpr char OverlayMagic[] = { 'R', 'K', 'T', '3' };

// Entry flag: this string may also be substituted when it reaches the glyph
// blitter on its own, without a line renderer above it. Only the name plates
// are marked, so a one-character key like a character name can never replace
// a single glyph in the middle of some other Japanese caption.
constexpr uint32_t StandaloneFlag = 1;

// File -> "ゲームの終了" in the window menu. The launcher rewrites the item text
// at runtime, so the command id is the stable way to recognise it.
constexpr UINT ExitMenuCommand = 32823;

// The Japanese script is hard wrapped at 25 full-width cells drawn from x=96
// with a 24 px font. Translations need the wider left margin, the smaller font
// and proportional advances to stay inside the 792 px frame.
int TextOriginX = 32;
int MessageFontHeight = 20;
int LetterSpacing = 0;
bool LogUntranslated = false;

DrawRowFunc OriginalDrawRow{};
DrawLineFunc OriginalDrawLine{};
RedrawLineFunc OriginalRedrawLine{};
RedrawPlainFunc OriginalRedrawPlain{};
DrawStringFunc OriginalDrawString{};

struct GlyphMetrics
{
    int Step;       // pen advance that keeps the next glyph from erasing this one
    int InkOffset;  // shift that keeps a negative left bearing from being clipped
};

struct Translation
{
    string Text;
    bool Standalone;
};

map<string, Translation> Translations;
map<uint32_t, GlyphMetrics> GlyphCache;
set<uint32_t> LoggedMetricFailures;
wchar_t OverlayCodepage[256]{};

// Set while one of our own strings is on screen: it selects the overlay
// codepage in Decode() and hands character placement to ProportionalPen.
bool RenderingTranslation{};
bool InsideLineRenderer{};
int ProportionalPen{};

HDC MeasureDc{};
map<int, HFONT> MeasureFonts;

set<string> LoggedStrings;
set<ATOM> LoggedMissingWindowProcAtoms;
wstring LogFolder;
wstring ProxyLogPath;
wstring MissingLogPath;

// Counters written out on shutdown: "drawn many, translated none" separates a
// rejected overlay from one whose keys simply do not match what is drawn.
int LinesDrawn{};
int LinesTranslated{};
int StandaloneDrawn{};
int StandaloneTranslated{};

// Original window procedures of the classes the game registers, by class atom.
map<ATOM, WNDPROC> WrappedWindowProcs;
bool ShuttingDown{};
bool DiagnosticsPrepared{};

BYTE* GameAddress(uintptr_t rva)
{
    return reinterpret_cast<BYTE*>(GetModuleHandleW(nullptr)) + rva;
}

int& GameInt(uintptr_t rva)
{
    return *reinterpret_cast<int*>(GameAddress(rva));
}

// A redraw can be triggered from inside the typewriter loop, so the state has
// to nest rather than being cleared unconditionally.
class RenderScope
{
public:
    RenderScope(bool translated, int penX, bool insideLine)
        : _rendering(RenderingTranslation), _inside(InsideLineRenderer), _pen(ProportionalPen)
    {
        RenderingTranslation = translated;
        InsideLineRenderer = insideLine;
        ProportionalPen = penX;
    }

    ~RenderScope()
    {
        RenderingTranslation = _rendering;
        InsideLineRenderer = _inside;
        ProportionalPen = _pen;
    }

private:
    bool _rendering;
    bool _inside;
    int _pen;
};

bool AppendLine(const wstring& path, const string& line, DWORD* failure = nullptr)
{
    if (path.empty())
        return false;

    char terminator[3] = { 13, 10, 0 };
    string payload = line + terminator;

    HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ, nullptr,
        OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        if (failure != nullptr)
            *failure = GetLastError();
        return false;
    }

    DWORD written{};
    BOOL wrote = WriteFile(file, payload.data(), static_cast<DWORD>(payload.size()), &written, nullptr);
    DWORD error = wrote && written == payload.size() ? ERROR_SUCCESS :
        (wrote ? ERROR_WRITE_FAULT : GetLastError());
    CloseHandle(file);
    if (error != ERROR_SUCCESS && failure != nullptr)
        *failure = error;
    return error == ERROR_SUCCESS;
}

void LogProxy(const string& line)
{
    SYSTEMTIME now{};
    GetLocalTime(&now);
    char timestamp[32]{};
    sprintf_s(timestamp, "%04u-%02u-%02u %02u:%02u:%02u.%03u ",
        now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond,
        now.wMilliseconds);
    string entry = timestamp + line;
    string debug = entry + "\r\n";
    OutputDebugStringA(debug.c_str());

    DWORD failure{};
    if (!ProxyLogPath.empty() && !AppendLine(ProxyLogPath, entry, &failure))
    {
        char message[128]{};
        sprintf_s(message, "[delta proxy] could not append proxy.log: Win32 error %lu\r\n",
            failure);
        OutputDebugStringA(message);
    }
}

void LogWindowsError(const char* operation, DWORD error)
{
    char* systemMessage{};
    FormatMessageA(
        FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
            FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr, error, 0, reinterpret_cast<char*>(&systemMessage), 0, nullptr);
    string detail = systemMessage == nullptr ? string() : string(systemMessage);
    if (systemMessage != nullptr)
        LocalFree(systemMessage);
    while (!detail.empty() && (detail.back() == '\r' || detail.back() == '\n'))
        detail.pop_back();

    char prefix[192]{};
    sprintf_s(prefix, "[win32] %s failed: error %lu", operation, error);
    LogProxy(detail.empty() ? string(prefix) : string(prefix) + " (" + detail + ")");
}

string BytesToHex(const BYTE* bytes, size_t count)
{
    string value;
    value.reserve(count * 3);
    for (size_t index = 0; index < count; ++index)
    {
        char byte[4]{};
        sprintf_s(byte, "%02X", bytes[index]);
        if (!value.empty())
            value.push_back(' ');
        value += byte;
    }
    return value;
}

string ToUtf8(const wstring& text)
{
    int length = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (length <= 1)
        return string();

    string result(static_cast<size_t>(length), 0);
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, result.data(), length, nullptr, nullptr);
    result.pop_back();
    return result;
}

string ToUtf8(const char* text)
{
    string result;
    int wideLength = MultiByteToWideChar(932, 0, text, -1, nullptr, 0);
    if (wideLength <= 1)
        return result;

    wstring wide(static_cast<size_t>(wideLength), 0);
    MultiByteToWideChar(932, 0, text, -1, wide.data(), wideLength);
    wide.pop_back();

    int utf8Length = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (utf8Length <= 1)
        return result;

    result.resize(static_cast<size_t>(utf8Length));
    WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), -1, result.data(), utf8Length, nullptr, nullptr);
    result.pop_back();
    return result;
}

// Reports a string the engine drew that the overlay has no entry for, with its
// raw bytes: padding or per-character drawing shows up immediately that way.
void LogMissingText(const char* origin, const char* text)
{
    if (!LogUntranslated || text == nullptr || *text == 0)
        return;
    if (!LoggedStrings.insert(text).second)
        return;

    char tab[2] = { 9, 0 };
    string line = origin;
    line += tab;
    for (const char* cursor = text; *cursor != 0; cursor++)
    {
        char byte[4]{};
        sprintf_s(byte, "%02X", static_cast<BYTE>(*cursor));
        line += byte;
    }
    line += tab;
    line += ToUtf8(text);

    AppendLine(MissingLogPath, line);
}

template<typename T>
bool ReadValue(const vector<BYTE>& data, size_t& position, T& value)
{
    if (position + sizeof(T) > data.size())
        return false;

    memcpy(&value, data.data() + position, sizeof(T));
    position += sizeof(T);
    return true;
}

bool LoadOverlay(const wstring& folder)
{
    wstring configPath = Path::Combine(folder, L"delta_launcher.ini");
    wchar_t language[16]{};
    GetPrivateProfileStringW(L"Launcher", L"Language", L"", language,
        static_cast<DWORD>(size(language)), configPath.c_str());

    bool japanese = (language[0] == L'j' || language[0] == L'J') &&
        (language[1] == L'p' || language[1] == L'P');
    wstring selectedWide = language;
    string selected;
    for (wchar_t character : selectedWide)
        selected.push_back(static_cast<char>(character));
    LogProxy("[language] delta_launcher.ini = " + (selected.empty() ? string("(missing)") : selected));
    if (japanese)
    {
        LogProxy("[overlay] Japanese selected, no overlay is loaded by design");
        return false;
    }

    bool english = (language[0] == L'e' || language[0] == L'E') &&
        (language[1] == L'n' || language[1] == L'N');
    bool russian = (language[0] == L'r' || language[0] == L'R') &&
        (language[1] == L'u' || language[1] == L'U');
    if (!selected.empty() && !english && !russian)
        LogProxy("[language] anomaly: unknown language value, falling back to RU overlay");
    wstring path = Path::Combine(
        folder, english ? L"delta_overlay.en.bin" : L"delta_overlay.ru.bin");
    if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        LogProxy("[overlay] language-specific file is absent; checking delta_overlay.bin");
        path = Path::Combine(folder, L"delta_overlay.bin");
    }

    LogProxy("[overlay] file " + ToUtf8(path));

    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        LogWindowsError("CreateFileW(overlay)", GetLastError());
        return false;
    }

    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size))
    {
        DWORD error = GetLastError();
        CloseHandle(file);
        LogWindowsError("GetFileSizeEx(overlay)", error);
        return false;
    }
    if (size.QuadPart < 8 + 512 || size.QuadPart > 64 * 1024 * 1024)
    {
        char message[160]{};
        sprintf_s(message,
            "[overlay] refused: file size is %lld bytes, expected 520..67108864",
            size.QuadPart);
        CloseHandle(file);
        LogProxy(message);
        return false;
    }

    vector<BYTE> data(static_cast<size_t>(size.QuadPart));
    DWORD read{};
    BOOL readSucceeded = ReadFile(
        file, data.data(), static_cast<DWORD>(data.size()), &read, nullptr);
    DWORD readError = readSucceeded ? ERROR_SUCCESS : GetLastError();
    CloseHandle(file);
    if (!readSucceeded)
    {
        LogWindowsError("ReadFile(overlay)", readError);
        return false;
    }
    if (read != data.size())
    {
        char message[160]{};
        sprintf_s(message, "[overlay] refused: short read, expected %zu bytes, received %lu",
            data.size(), read);
        LogProxy(message);
        return false;
    }
    if (memcmp(data.data(), OverlayMagic, sizeof(OverlayMagic)) != 0)
    {
        LogProxy("[overlay] refused: magic is " + BytesToHex(data.data(), 4) +
            ", expected " + BytesToHex(
                reinterpret_cast<const BYTE*>(OverlayMagic), sizeof(OverlayMagic)));
        return false;
    }

    size_t position = sizeof(OverlayMagic);
    uint32_t count{};
    if (!ReadValue(data, position, count))
    {
        LogProxy("[overlay] refused: entry count is truncated");
        return false;
    }
    if (count == 0 || count > 100000)
    {
        char message[128]{};
        sprintf_s(message, "[overlay] refused: invalid entry count %u", count);
        LogProxy(message);
        return false;
    }

    wchar_t loadedCodepage[256]{};
    for (int code = 0; code < 256; code++)
    {
        uint16_t character{};
        if (!ReadValue(data, position, character))
        {
            char message[128]{};
            sprintf_s(message, "[overlay] refused: codepage is truncated at byte code %d", code);
            LogProxy(message);
            return false;
        }

        loadedCodepage[code] = static_cast<wchar_t>(character);
    }

    map<string, Translation> loadedTranslations;
    for (uint32_t i = 0; i < count; ++i)
    {
        size_t entryPosition = position;
        uint32_t originalSize{};
        uint32_t translationSize{};
        if (!ReadValue(data, position, originalSize) ||
            !ReadValue(data, position, translationSize))
        {
            char message[160]{};
            sprintf_s(message,
                "[overlay] refused: entry %u header is truncated at offset %zu", i,
                entryPosition);
            LogProxy(message);
            return false;
        }
        if (originalSize > 1024 * 1024 || translationSize > 1024 * 1024)
        {
            char message[192]{};
            sprintf_s(message,
                "[overlay] refused: entry %u has invalid sizes original=%u translation=%u",
                i, originalSize, translationSize);
            LogProxy(message);
            return false;
        }

        uint32_t flags{};
        if (!ReadValue(data, position, flags))
        {
            char message[128]{};
            sprintf_s(message, "[overlay] refused: entry %u flags are truncated", i);
            LogProxy(message);
            return false;
        }
        if ((flags & ~StandaloneFlag) != 0)
        {
            char message[128]{};
            sprintf_s(message, "[overlay] refused: entry %u has unknown flags 0x%08X", i, flags);
            LogProxy(message);
            return false;
        }
        if (position + originalSize + translationSize > data.size())
        {
            char message[192]{};
            sprintf_s(message,
                "[overlay] refused: entry %u payload is truncated at offset %zu "
                "(original=%u translation=%u, file=%zu)",
                i, position, originalSize, translationSize, data.size());
            LogProxy(message);
            return false;
        }

        string original(reinterpret_cast<const char*>(data.data() + position), originalSize);
        position += originalSize;
        string translation(reinterpret_cast<const char*>(data.data() + position), translationSize);
        position += translationSize;
        auto inserted = loadedTranslations.emplace(
            original, Translation{ move(translation), (flags & StandaloneFlag) != 0 });
        if (!inserted.second)
        {
            LogProxy("[overlay] refused: duplicate source at entry " + to_string(i) +
                ", bytes=" + BytesToHex(
                    reinterpret_cast<const BYTE*>(original.data()), original.size()) +
                ", text=" + ToUtf8(original.c_str()));
            return false;
        }
    }

    if (position != data.size())
    {
        char message[160]{};
        sprintf_s(message,
            "[overlay] refused: %zu trailing bytes after offset %zu", data.size() - position,
            position);
        LogProxy(message);
        return false;
    }

    memcpy(OverlayCodepage, loadedCodepage, sizeof(OverlayCodepage));
    Translations = move(loadedTranslations);

    int standalone = 0;
    for (const auto& entry : Translations)
    {
        if (entry.second.Standalone)
            standalone++;
    }

    char summary[128]{};
    sprintf_s(summary, "[overlay] loaded %d entries, %d of them standalone UI strings",
        static_cast<int>(Translations.size()), standalone);
    LogProxy(summary);
    return true;
}

void LoadLayoutOverrides(const wstring& folder)
{
    wstring path = Path::Combine(folder, L"delta_launcher.ini");
    auto read = [&](const wchar_t* key, int minimum, int maximum, int& target)
    {
        wchar_t text[32]{};
        DWORD length = GetPrivateProfileStringW(L"Overlay", key, L"", text,
            static_cast<DWORD>(size(text)), path.c_str());
        if (length == 0)
            return;
        if (length >= size(text) - 1)
        {
            LogProxy("[layout] ignored truncated value for " + ToUtf8(wstring(key)));
            return;
        }

        int value{};
        if (swscanf_s(text, L"%d", &value) == 1 && value >= minimum && value <= maximum)
            target = value;
        else
        {
            char range[80]{};
            sprintf_s(range, " (allowed %d..%d)", minimum, maximum);
            LogProxy("[layout] ignored invalid " + ToUtf8(wstring(key)) + "=" +
                ToUtf8(wstring(text)) + range);
        }
    };

    read(L"TEXT_X", 0, 700, TextOriginX);
    read(L"FONT_HEIGHT", 8, 32, MessageFontHeight);
    read(L"LETTER_SPACING", -4, 8, LetterSpacing);

    int untranslated = LogUntranslated ? 1 : 0;
    read(L"LOG_UNTRANSLATED", 0, 1, untranslated);
    LogUntranslated = untranslated != 0;
}

// Both the scenario path and the redraw paths read these two globals to place
// a row, so overriding them keeps the first draw and every repaint in step.
void ApplyLayoutGlobals()
{
    GameInt(TextOriginXRva) = TextOriginX;
    GameInt(FontHeightRva) = MessageFontHeight;
}

const Translation* Translate(const char* text)
{
    if (text == nullptr)
        return nullptr;

    auto translation = Translations.find(text);
    return translation != Translations.end() ? &translation->second : nullptr;
}

HFONT FetchMeasureFont(int fontHeight)
{
    auto cached = MeasureFonts.find(fontHeight);
    if (cached != MeasureFonts.end())
        return cached->second;

    // Same LOGFONT the proxy hands the game through CreateFontA, so the metrics
    // describe the glyphs that actually get drawn.
    LOGFONTW info{};
    wcscpy_s(info.lfFaceName, Proportionalizer::CustomFontName.c_str());
    info.lfHeight = fontHeight;
    info.lfWeight = FW_NORMAL;
    info.lfCharSet = DEFAULT_CHARSET;
    info.lfClipPrecision = CLIP_DEFAULT_PRECIS;
    info.lfOutPrecision = OUT_DEFAULT_PRECIS;
    info.lfPitchAndFamily = DEFAULT_PITCH;
    info.lfQuality = DEFAULT_QUALITY;

    HFONT font = CreateFontIndirectW(&info);
    if (font == nullptr)
        LogWindowsError("CreateFontIndirectW", GetLastError());
    MeasureFonts[fontHeight] = font;
    return font;
}

// The engine blits every glyph as an opaque (fontHeight + 2) square and renders
// it at x=0 in a scratch DC first. So the next glyph erases whatever this one
// paints past the pen step, and a negative left bearing is clipped before the
// blit even happens. Both need compensating, or letters lose an edge.
const GlyphMetrics& FetchGlyphMetrics(wchar_t character, int fontHeight)
{
    uint32_t key = (static_cast<uint32_t>(fontHeight) << 16) | static_cast<uint16_t>(character);
    auto cached = GlyphCache.find(key);
    if (cached != GlyphCache.end())
        return cached->second;

    if (MeasureDc == nullptr)
    {
        MeasureDc = CreateCompatibleDC(nullptr);
        if (MeasureDc == nullptr)
            LogWindowsError("CreateCompatibleDC", GetLastError());
    }

    HFONT font = FetchMeasureFont(fontHeight);
    HGDIOBJ previous = MeasureDc == nullptr || font == nullptr
        ? nullptr : SelectObject(MeasureDc, font);
    if (MeasureDc == nullptr || font == nullptr || previous == nullptr || previous == HGDI_ERROR)
    {
        if (LoggedMetricFailures.insert(key).second)
        {
            char message[160]{};
            sprintf_s(message,
                "[font] using fallback advance for U+%04X at height %d: GDI setup failed",
                static_cast<unsigned int>(character), fontHeight);
            LogProxy(message);
        }
        return GlyphCache[key] = GlyphMetrics{ max((fontHeight + 2) / 2, 1), 0 };
    }

    GlyphMetrics metrics{ (fontHeight + 2) / 2, 0 };
    ABC abc{};
    SIZE size{};
    if (GetCharABCWidthsW(MeasureDc, character, character, &abc))
    {
        int advance = abc.abcA + static_cast<int>(abc.abcB) + abc.abcC;
        metrics.InkOffset = max(-abc.abcA, 0);
        int inkRight = abc.abcA + static_cast<int>(abc.abcB) + metrics.InkOffset;
        metrics.Step = max(advance, inkRight + 1);
    }
    else if (GetTextExtentPoint32W(MeasureDc, &character, 1, &size))
    {
        metrics.Step = size.cx;
    }
    else if (LoggedMetricFailures.insert(key).second)
    {
        char message[160]{};
        sprintf_s(message,
            "[font] using fallback advance for U+%04X at height %d: both metric APIs failed",
            static_cast<unsigned int>(character), fontHeight);
        LogProxy(message);
    }

    metrics.Step = max(metrics.Step, 1);
    return GlyphCache[key] = metrics;
}

// RSA.EXE faults inside its own shutdown: closing it with WM_CLOSE ends in an
// access violation with or without this proxy, which is why the title bar
// button did nothing in the first place. The game has already written its
// settings by that point, so the fault is turned into a quiet exit instead of
// a Windows crash report.
LONG CALLBACK ShutdownExceptionFilter(EXCEPTION_POINTERS* exception)
{
    if (ShuttingDown && exception != nullptr && exception->ExceptionRecord != nullptr &&
        exception->ExceptionRecord->ExceptionCode == EXCEPTION_ACCESS_VIOLATION)
    {
        char message[192]{};
        sprintf_s(message,
            "[shutdown] suppressed exception 0x%08lX at %p on thread %lu",
            exception->ExceptionRecord->ExceptionCode,
            exception->ExceptionRecord->ExceptionAddress, GetCurrentThreadId());
        LogProxy(message);
        ExitProcess(0);
    }

    return EXCEPTION_CONTINUE_SEARCH;
}

// The engine confirms three routine actions with a Japanese MessageBoxA:
// overwriting a save slot, loading the quick save, and quitting. Each one only
// repeats a choice the player has just made in the game's own interface, and
// none of them is readable without translating a Win32 message box, so they are
// answered affirmatively and never shown. Everything else passes through: the
// file and audio errors, and the two prompts that offer to delete or rename a
// save file, which are destructive and are not the player's own click coming
// back at them.
struct SkippedPrompt
{
    const char* Fragment;
    const char* Name;
};

// CP932 fragments, written byte by byte so the file stays plain ASCII. Each one
// is the verb of the prompt, which is what identifies the action being
// confirmed: "shuuryou shimasu" (quitting), "uwagaki save shimasu"
// (overwriting a save) and "load shimasu" (loading the quick save).
const SkippedPrompt SkippedPrompts[] =
{
    { "\x8f\x49\x97\xb9\x82\xb5\x82\xdc\x82\xb7\x81\x42", "quit" },
    { "\x8f\xe3\x8f\x91\x82\xab\x83\x5a\x81\x5b\x83\x75\x82\xb5\x82\xdc\x82\xb7\x81\x42", "overwrite save" },
    { "\x83\x8d\x81\x5b\x83\x68\x82\xb5\x82\xdc\x82\xb7\x81\x42", "load quick save" },
};

// Only the button sets with an unambiguous "go ahead" are answered; anything
// else is shown, because guessing at Retry or Ignore would be a coin flip.
int AffirmativeAnswer(UINT type)
{
    switch (type & MB_TYPEMASK)
    {
    case MB_OK:
    case MB_OKCANCEL:
        return IDOK;
    case MB_YESNO:
    case MB_YESNOCANCEL:
        return IDYES;
    default:
        return 0;
    }
}

int __stdcall MessageBoxAHook(HWND owner, LPCSTR text, LPCSTR caption, UINT type)
{
    if (text != nullptr)
    {
        for (const SkippedPrompt& prompt : SkippedPrompts)
        {
            if (strstr(text, prompt.Fragment) == nullptr)
                continue;

            int answer = AffirmativeAnswer(type);
            if (answer == 0)
                break;

            LogProxy(string("[dialog] skipped ") + prompt.Name + ": " + ToUtf8(text));
            return answer;
        }

        // Whatever is left is worth a line in the log: it is either a real
        // error or a prompt this list does not know about yet.
        LogProxy(string("[dialog] shown: ") + ToUtf8(text));
    }

    return MessageBoxA(owner, text, caption, type);
}

// The title bar button sends WM_SYSCOMMAND with SC_CLOSE. The engine swallows
// that one and answers it with a half-broken confirmation of its own, while
// WM_CLOSE runs the shutdown path it handles properly, so the button is routed
// straight there and the game just closes.
LRESULT CALLBACK WindowProcHook(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    bool closeRequested = (message == WM_SYSCOMMAND && (wParam & 0xFFF0) == SC_CLOSE) ||
        (message == WM_COMMAND && HIWORD(wParam) == 0 && LOWORD(wParam) == ExitMenuCommand);
    if (closeRequested)
    {
        message = WM_CLOSE;
        wParam = 0;
        lParam = 0;
    }

    if (message == WM_CLOSE && !ShuttingDown)
    {
        ShuttingDown = true;
        char buffer[256]{};
        sprintf_s(buffer,
            "[stats] message lines drawn %d, translated %d; standalone strings drawn %d, translated %d",
            LinesDrawn, LinesTranslated, StandaloneDrawn, StandaloneTranslated);
        LogProxy(buffer);
        LogProxy("[shutdown] closing on request");
    }

    ATOM atom = GetClassWord(window, GCW_ATOM);
    auto original = WrappedWindowProcs.find(atom);
    if (original == WrappedWindowProcs.end())
    {
        if (LoggedMissingWindowProcAtoms.insert(atom).second)
        {
            char message[160]{};
            sprintf_s(message,
                "[window] no original procedure recorded for class atom %u, hwnd=%p",
                static_cast<unsigned int>(atom), window);
            LogProxy(message);
        }
        return DefWindowProcA(window, message, wParam, lParam);
    }

    return CallWindowProcA(original->second, window, message, wParam, lParam);
}

ATOM __stdcall RegisterClassExAHook(const WNDCLASSEXA* wndClass)
{
    if (wndClass == nullptr)
    {
        LogProxy("[window] RegisterClassExA received a null class descriptor");
        return RegisterClassExA(wndClass);
    }
    if (wndClass->lpfnWndProc == nullptr)
    {
        LogProxy("[window] RegisterClassExA received a null window procedure");
        return RegisterClassExA(wndClass);
    }
    if (wndClass->lpfnWndProc == WindowProcHook)
        return RegisterClassExA(wndClass);

    WNDCLASSEXA patched = *wndClass;
    patched.lpfnWndProc = WindowProcHook;

    ATOM atom = RegisterClassExA(&patched);
    if (atom != 0)
        WrappedWindowProcs[atom] = wndClass->lpfnWndProc;
    else
        LogWindowsError("RegisterClassExA", GetLastError());

    return atom;
}

void __cdecl DrawRowHook(const char* text, int row)
{
    ApplyLayoutGlobals();
    OriginalDrawRow(text, row);
}

void __cdecl DrawLineHook(const char* text, int x, int y, int cacheFlag)
{
    const Translation* translation = Translate(text);
    LinesDrawn++;
    if (translation != nullptr)
        LinesTranslated++;
    else
        LogMissingText("line", text);

    RenderScope scope(translation != nullptr, x, true);
    OriginalDrawLine(translation != nullptr ? translation->Text.c_str() : text, x, y, cacheFlag);
}

void __fastcall RedrawLineHook(
    void* thisPtr, void* unused, int a1, int x, int y, const char* text, int a5)
{
    const Translation* translation = Translate(text);
    if (translation == nullptr)
        LogMissingText("redraw", text);

    RenderScope scope(translation != nullptr, x, true);
    OriginalRedrawLine(
        thisPtr, unused, a1, x, y, translation != nullptr ? translation->Text.c_str() : text, a5);
}

void __cdecl RedrawPlainHook(const char* text, int x, int y)
{
    const Translation* translation = Translate(text);
    if (translation == nullptr)
        LogMissingText("redraw-plain", text);

    RenderScope scope(translation != nullptr, x, true);
    OriginalRedrawPlain(translation != nullptr ? translation->Text.c_str() : text, x, y);
}

void DrawProportional(
    void* thisPtr, void* unused, int a1, int y, const char* text, int minimumStep)
{
    int fontHeight = GameInt(FontHeightRva);
    for (const char* character = text; *character != 0; character++)
    {
        char single[2] = { *character, 0 };
        OriginalDrawString(thisPtr, unused, a1, ProportionalPen, y, single);

        BYTE code = static_cast<BYTE>(*character);
        wchar_t decoded = OverlayCodepage[code] != 0
            ? OverlayCodepage[code]
            : static_cast<wchar_t>(code);
        int step = FetchGlyphMetrics(decoded, fontHeight).Step;
        ProportionalPen += max(step, minimumStep) + LetterSpacing;
    }
}

// The engine blits every glyph into a fixed (fontHeight + 2) / 2 cell. While
// one of our lines is on screen we place the glyphs ourselves instead, which
// is what turns the monospaced grid into real proportional text. The scenario
// path calls this once per character, the redraw paths once per line.
void __fastcall DrawStringHook(void* thisPtr, void* unused, int a1, int x, int y, const char* text)
{
    if (text == nullptr || *text == 0)
    {
        OriginalDrawString(thisPtr, unused, a1, x, y, text);
        return;
    }

    if (RenderingTranslation)
    {
        DrawProportional(thisPtr, unused, a1, y, text, 0);
        return;
    }

    // Name plates and other standalone strings reach the blitter directly,
    // without passing through one of the line renderers first.
    if (!InsideLineRenderer)
    {
        const Translation* translation = Translate(text);
        StandaloneDrawn++;
        if (translation != nullptr && translation->Standalone)
            StandaloneTranslated++;
        else
            LogMissingText("glyph", text);

        if (translation != nullptr && translation->Standalone)
        {
            // A name plate has the whole frame to itself, so it keeps the roomy
            // half-width rhythm of the original instead of being squeezed.
            RenderScope scope(true, x, false);
            DrawProportional(
                thisPtr, unused, a1, y, translation->Text.c_str(), (GameInt(FontHeightRva) + 2) / 2);
            return;
        }
    }

    OriginalDrawString(thisPtr, unused, a1, x, y, text);
}

void LogDetourError(const char* operation, const char* hookName, LONG error)
{
    char message[192]{};
    sprintf_s(message, "[hooks] %s failed%s%s: error %ld", operation,
        hookName == nullptr ? "" : " for ", hookName == nullptr ? "" : hookName, error);
    LogProxy(message);
}

bool AttachHook(uintptr_t rva, const BYTE* expected, size_t expectedSize,
    PVOID* original, PVOID hook, const char* hookName)
{
    BYTE* target = GameAddress(rva);
    MEMORY_BASIC_INFORMATION memory{};
    SIZE_T queried = VirtualQuery(target, &memory, sizeof(memory));
    uintptr_t regionEnd = queried == sizeof(memory)
        ? reinterpret_cast<uintptr_t>(memory.BaseAddress) + memory.RegionSize : 0;
    bool readable = queried == sizeof(memory) && memory.State == MEM_COMMIT &&
        (memory.Protect & (PAGE_NOACCESS | PAGE_GUARD)) == 0 &&
        reinterpret_cast<uintptr_t>(target) <= regionEnd &&
        expectedSize <= regionEnd - reinterpret_cast<uintptr_t>(target);
    if (!readable)
    {
        char message[224]{};
        sprintf_s(message,
            "[hooks] cannot inspect %s at RVA 0x%08zX: VirtualQuery=%zu state=0x%lX protect=0x%lX",
            hookName, static_cast<size_t>(rva), static_cast<size_t>(queried),
            queried == sizeof(memory) ? memory.State : 0,
            queried == sizeof(memory) ? memory.Protect : 0);
        LogProxy(message);
        return false;
    }
    if (memcmp(target, expected, expectedSize) != 0)
    {
        char prefix[160]{};
        sprintf_s(prefix, "[hooks] executable signature mismatch for %s at RVA 0x%08zX",
            hookName, static_cast<size_t>(rva));
        LogProxy(string(prefix) + "; expected=" + BytesToHex(expected, expectedSize) +
            "; actual=" + BytesToHex(target, expectedSize));
        return false;
    }

    *original = target;
    LONG error = DetourAttach(original, hook);
    if (error != NO_ERROR)
    {
        LogDetourError("DetourAttach", hookName, error);
        return false;
    }
    LogProxy(string("[hooks] attached ") + hookName);
    return true;
}
}

void DeltaOverlay::PrepareDiagnostics()
{
    if (DiagnosticsPrepared)
        return;
    DiagnosticsPrepared = true;
    wstring folder = Path::GetModuleFolderPath(nullptr);
    LogFolder = Path::Combine(folder, L"log");
    ProxyLogPath = Path::Combine(LogFolder, L"proxy.log");
    if (!CreateDirectoryW(LogFolder.c_str(), nullptr))
    {
        DWORD error = GetLastError();
        if (error != ERROR_ALREADY_EXISTS)
            LogWindowsError("CreateDirectoryW(log)", error);
    }
    if (!DeleteFileW(ProxyLogPath.c_str()))
    {
        DWORD error = GetLastError();
        if (error != ERROR_FILE_NOT_FOUND)
            LogWindowsError("DeleteFileW(proxy.log)", error);
    }
}

void DeltaOverlay::LogDiagnostic(const string& line)
{
    PrepareDiagnostics();
    LogProxy(line);
}

void DeltaOverlay::Init()
{
    PrepareDiagnostics();
    wstring folder = Path::GetModuleFolderPath(nullptr);

    LogProxy(string("[startup] winmm.dll built ") + __DATE__ + " " + __TIME__);
    char process[160]{};
    sprintf_s(process, "[startup] process=%lu thread=%lu module-base=%p",
        GetCurrentProcessId(), GetCurrentThreadId(), GetModuleHandleW(nullptr));
    LogProxy(process);

    wchar_t executablePath[32768]{};
    DWORD executableLength = GetModuleFileNameW(
        nullptr, executablePath, static_cast<DWORD>(size(executablePath)));
    if (executableLength == 0)
        LogWindowsError("GetModuleFileNameW(game)", GetLastError());
    else if (executableLength >= size(executablePath) - 1)
        LogProxy("[startup] anomaly: executable path was truncated");
    else
    {
        LogProxy("[startup] executable " + ToUtf8(wstring(executablePath, executableLength)));
        WIN32_FILE_ATTRIBUTE_DATA attributes{};
        if (GetFileAttributesExW(executablePath, GetFileExInfoStandard, &attributes))
        {
            ULARGE_INTEGER fileSize{};
            fileSize.HighPart = attributes.nFileSizeHigh;
            fileSize.LowPart = attributes.nFileSizeLow;
            char identity[160]{};
            sprintf_s(identity, "[startup] executable size=%llu bytes timestamp=%08lX:%08lX",
                fileSize.QuadPart, attributes.ftLastWriteTime.dwHighDateTime,
                attributes.ftLastWriteTime.dwLowDateTime);
            LogProxy(identity);
        }
        else
            LogWindowsError("GetFileAttributesExW(game)", GetLastError());
    }

    bool importsHooked = ImportHooker::Hook({
        { "RegisterClassExA", RegisterClassExAHook },
        { "MessageBoxA", MessageBoxAHook },
    });
    if (importsHooked)
        LogProxy("[startup] window procedure wrapper and dialog filter installed");
    else
        LogProxy("[startup] anomaly: DetourEnumerateImportsEx failed for GUI hooks");
    PVOID exceptionHandler = AddVectoredExceptionHandler(1, ShutdownExceptionFilter);
    if (exceptionHandler != nullptr)
        LogProxy("[startup] shutdown exception handler installed");
    else
        LogWindowsError("AddVectoredExceptionHandler", GetLastError());

    LoadLayoutOverrides(folder);
    char layout[160]{};
    sprintf_s(layout, "[layout] TEXT_X=%d FONT_HEIGHT=%d LETTER_SPACING=%d",
        TextOriginX, MessageFontHeight, LetterSpacing);
    LogProxy(layout);

    if (LogUntranslated)
    {
        MissingLogPath = Path::Combine(LogFolder, L"untranslated.log");
        DeleteFileW(MissingLogPath.c_str());
        LogProxy("[log] untranslated.log is enabled");
    }

    if (!LoadOverlay(folder))
    {
        LogProxy("[startup] no overlay is active, the game runs untranslated");
        return;
    }

    // The original Japanese font gives Cyrillic glyphs a full-width advance.
    // Use a proportional system font only when a translated overlay is active.
    Proportionalizer::CustomFontName = L"Arial Narrow";
    Proportionalizer::LastFontName = L"Arial Narrow";

    LONG transactionError = DetourTransactionBegin();
    if (transactionError != NO_ERROR)
    {
        Translations.clear();
        LogDetourError("DetourTransactionBegin", nullptr, transactionError);
        LogProxy("[startup] no overlay is active, the game runs untranslated");
        return;
    }
    LONG updateError = DetourUpdateThread(GetCurrentThread());
    if (updateError != NO_ERROR)
    {
        LONG abortError = DetourTransactionAbort();
        Translations.clear();
        LogDetourError("DetourUpdateThread", nullptr, updateError);
        if (abortError != NO_ERROR)
            LogDetourError("DetourTransactionAbort", nullptr, abortError);
        LogProxy("[startup] no overlay is active, the game runs untranslated");
        return;
    }
    bool attached =
        AttachHook(DrawRowRva, ExpectedDrawRow, sizeof(ExpectedDrawRow),
            reinterpret_cast<PVOID*>(&OriginalDrawRow), DrawRowHook, "DrawRow") &&
        AttachHook(DrawLineRva, ExpectedDrawLine, sizeof(ExpectedDrawLine),
            reinterpret_cast<PVOID*>(&OriginalDrawLine), DrawLineHook, "DrawLine") &&
        AttachHook(RedrawLineRva, ExpectedRedrawLine, sizeof(ExpectedRedrawLine),
            reinterpret_cast<PVOID*>(&OriginalRedrawLine), RedrawLineHook, "redraw") &&
        AttachHook(RedrawPlainRva, ExpectedRedrawPlain, sizeof(ExpectedRedrawPlain),
            reinterpret_cast<PVOID*>(&OriginalRedrawPlain), RedrawPlainHook, "redraw-plain") &&
        AttachHook(DrawStringRva, ExpectedDrawString, sizeof(ExpectedDrawString),
            reinterpret_cast<PVOID*>(&OriginalDrawString), DrawStringHook, "glyph blitter");

    if (!attached)
    {
        LONG abortError = DetourTransactionAbort();
        if (abortError != NO_ERROR)
            LogDetourError("DetourTransactionAbort", nullptr, abortError);
        Translations.clear();
        LogProxy("[hooks] a renderer hook failed, every pending hook was rolled back");
        LogProxy("[startup] no overlay is active, the game runs untranslated");
        return;
    }

    LONG commitError = DetourTransactionCommit();
    if (commitError != NO_ERROR)
    {
        Translations.clear();
        LogDetourError("DetourTransactionCommit", nullptr, commitError);
        LogProxy("[startup] no overlay is active, the game runs untranslated");
        return;
    }
    LogProxy("[hooks] renderers hooked: DrawRow, DrawLine, redraw, redraw-plain, glyph blitter");
}

wstring DeltaOverlay::Decode(const char* pText, int count)
{
    if (!RenderingTranslation)
        return SjisTunnelEncoding::Decode(pText, count);

    wstring result;
    if (pText == nullptr)
        return result;

    for (int i = 0; count < 0 ? pText[i] != 0 : i < count; i++)
    {
        BYTE code = static_cast<BYTE>(pText[i]);
        if (OverlayCodepage[code] != 0)
        {
            result += OverlayCodepage[code];
            continue;
        }

        // Everything the encoder leaves unmapped is plain ASCII.
        wchar_t character = L'?';
        MultiByteToWideChar(932, 0, pText + i, 1, &character, 1);
        result += character;
    }

    return result;
}

int DeltaOverlay::GlyphInkOffset(const wchar_t* pText, int count, int fontHeight)
{
    if (!RenderingTranslation || pText == nullptr || count != 1)
        return 0;

    return FetchGlyphMetrics(pText[0], fontHeight).InkOffset;
}

void DeltaOverlay::LogDrawnText(const char* pText, int count)
{
    if (!LogUntranslated || RenderingTranslation || pText == nullptr || count <= 0)
        return;

    string text(pText, count);
    LogMissingText("textout", text.c_str());
}
