@echo off
rem Bootstrap only. bin\delta.exe is what builds the toolset - this file exists
rem so a fresh checkout, which has no bin\, can produce it once. After that,
rem use "bin\delta.exe build".
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo The .NET Framework C# compiler was not found: %CSC%
    exit /b 1
)
set "MISSING_SOURCES="
for %%F in (DeltaCli.cs DeltaPython.cs DeltaProject.cs AssemblyInfo.cs DeltaTranslatorGui.cs DeltaResourceTool.cs) do (
    if not exist "%~dp0gui\%%F" set "MISSING_SOURCES=1"
)
if defined MISSING_SOURCES (
    echo The build source folder is incomplete: %~dp0gui
    echo Copy or extract the complete gui folder beside build.cmd and try again.
    exit /b 1
)
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" /nologo /target:exe /optimize+ /main:DeltaCliProgram ^
    "/out:%~dp0bin\delta.exe" /reference:System.dll /reference:System.Core.dll ^
    "%~dp0gui\DeltaCli.cs" "%~dp0gui\DeltaPython.cs" "%~dp0gui\DeltaProject.cs" "%~dp0gui\AssemblyInfo.cs"
if errorlevel 1 exit /b 1

rem winmm.dll needs Visual Studio, which the C# build does not. Ask for it only
rem when vswhere reports an installation, so a machine without the C++ workload
rem still ends up with a usable toolset instead of a failed bootstrap. Passing
rem --proxy by hand still reaches delta.exe through %*, and still fails loudly
rem when the compiler is missing, because that is an explicit request.
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "PROXY=--proxy"
if not exist "%VSWHERE%" set "PROXY="
if not defined PROXY echo Visual Studio was not found, so bin\winmm.dll is skipped.
if not defined PROXY echo Install it, then run "bin\delta.exe build --proxy".

"%~dp0bin\delta.exe" build %PROXY% %*
