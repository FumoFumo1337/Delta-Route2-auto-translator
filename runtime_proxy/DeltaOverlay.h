#pragma once

class DeltaOverlay
{
public:
    // Available from DllMain and Proxy::Init, before the renderer hooks exist.
    static void PrepareDiagnostics();
    static void LogDiagnostic(const std::string& line);
    static void Init();

    // Decodes bytes the way the string currently being drawn was encoded: the
    // half-width overlay codepage while one of our translations is on screen,
    // the regular SJIS tunnel for everything the game renders on its own.
    static std::wstring Decode(const char* pText, int count);

    // Glyphs whose ink starts left of the pen lose that column: the engine
    // renders every glyph at x=0 in a scratch DC before blitting it.
    static int GlyphInkOffset(const wchar_t* pText, int count, int fontHeight);

    // Diagnostic: records text the engine draws through GDI directly, which is
    // the only way to see strings that reach no line renderer at all.
    static void LogDrawnText(const char* pText, int count);
};
