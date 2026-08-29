#include "pch.h"

#include "DeltaOverlay.h"

void* OriginalEntryPoint;

void Initialize();

__declspec(naked) void EntryPointHook()
{
    __asm
    {
        call Initialize
        jmp OriginalEntryPoint
    }
}

void Initialize()
{
    if (OriginalEntryPoint != nullptr)
    {
        LONG begin = DetourTransactionBegin();
        LONG update = begin == NO_ERROR ? DetourUpdateThread(GetCurrentThread()) : begin;
        LONG detach = update == NO_ERROR
            ? DetourDetach(&OriginalEntryPoint, &EntryPointHook) : update;
        LONG commit = detach == NO_ERROR ? DetourTransactionCommit() : detach;
        if (detach != NO_ERROR && begin == NO_ERROR)
            DetourTransactionAbort();
        if (begin != NO_ERROR || update != NO_ERROR || detach != NO_ERROR || commit != NO_ERROR)
        {
            DeltaOverlay::LogDiagnostic(
                "[entrypoint] could not remove startup hook: begin=" +
                std::to_string(begin) + " update=" + std::to_string(update) +
                " detach=" + std::to_string(detach) + " commit=" +
                std::to_string(commit));
        }
        else
            DeltaOverlay::LogDiagnostic("[entrypoint] startup hook removed");
    }

    DeltaOverlay::LogDiagnostic("[startup] initializing translation runtime");

    // Uncomment for games that only work in a Japanese locale
    // (and include LoaderDll.dll and LocaleEmulator.dll from https://github.com/xupefei/Locale-Emulator/releases)
    /*
    if (GetACP() != 932)
    {
        if (LocaleEmulator::Relaunch())
            ExitProcess(0);
    }
    //*/

    CompilerHelper::Init();
    Win32AToWAdapter::Init();
    SjisTunnelEncoding::PatchGameLookupTable();

    GdiProportionalizer::Init();
    D2DProportionalizer::Init();

    DeltaOverlay::Init();

    EnginePatches::Init();

    if (!SetCurrentDirectoryW(Path::GetModuleFolderPath(nullptr).c_str()))
        DeltaOverlay::LogDiagnostic("[startup] SetCurrentDirectoryW failed: error " +
            std::to_string(GetLastError()));
    DeltaOverlay::LogDiagnostic("[startup] translation runtime initialized");
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
    {
        DeltaOverlay::PrepareDiagnostics();
        DeltaOverlay::LogDiagnostic("[dll] DLL_PROCESS_ATTACH");
        Proxy::Init(hModule);

#if _DEBUG
        Initialize();
#else
        OriginalEntryPoint = DetourGetEntryPoint(nullptr);
        if (OriginalEntryPoint == nullptr)
        {
            DeltaOverlay::LogDiagnostic("[entrypoint] DetourGetEntryPoint returned null");
            return FALSE;
        }
        LONG begin = DetourTransactionBegin();
        LONG update = begin == NO_ERROR ? DetourUpdateThread(GetCurrentThread()) : begin;
        LONG attach = update == NO_ERROR
            ? DetourAttach(&OriginalEntryPoint, EntryPointHook) : update;
        LONG commit = attach == NO_ERROR ? DetourTransactionCommit() : attach;
        if (attach != NO_ERROR && begin == NO_ERROR)
            DetourTransactionAbort();
        if (begin != NO_ERROR || update != NO_ERROR || attach != NO_ERROR || commit != NO_ERROR)
        {
            DeltaOverlay::LogDiagnostic(
                "[entrypoint] startup hook failed: begin=" + std::to_string(begin) +
                " update=" + std::to_string(update) + " attach=" +
                std::to_string(attach) + " commit=" + std::to_string(commit));
            return FALSE;
        }
        DeltaOverlay::LogDiagnostic("[entrypoint] startup hook installed");
#endif
        break;
    }
    	
    case DLL_PROCESS_DETACH:
        DeltaOverlay::LogDiagnostic("[dll] DLL_PROCESS_DETACH");
        break;
    }
    return TRUE;
}
