@echo off
rem Double-click to run the test suite. It is the command the README gives -
rem "python -m unittest discover -s tests -t tests" - wrapped so that the window
rem a double-click opens stays open long enough to read the result.
rem
rem   test.cmd             everything, about a minute
rem   test.cmd extract     one stage folder: extract, translate, proofreading,
rem                        overlay or resources. resources is the slow one.
setlocal
cd /d "%~dp0"
set "CODE=1"

rem A failing test quotes the script, which is Japanese, and the console starts
rem in a codepage that cannot print it - Python would then fail on the message
rem instead of showing the failure. The old codepage goes back at the end, for
rem the case where this was started from a terminal that was already open.
for /f "tokens=2 delims=:" %%c in ('chcp') do set "OLDCP=%%c"
chcp 65001 >nul
set "PYTHONUTF8=1"

rem The tests have to run on the same interpreter as the pipeline, so ask the
rem toolset rather than guess: line two of "delta python" is the one it settled
rem on. A checkout that has not been built yet has no bin\, hence the fallbacks.
set "PYEXE="
set "PYARG="
if exist "%~dp0bin\delta.exe" (
    for /f "usebackq skip=1 delims=" %%p in (`"%~dp0bin\delta.exe" python 2^>nul`) do if not defined PYEXE set "PYEXE=%%p"
)
if not defined PYEXE (
    where py.exe >nul 2>&1
    if not errorlevel 1 (
        set "PYEXE=py"
        set "PYARG=-3"
    )
)
if not defined PYEXE (
    where python.exe >nul 2>&1
    if not errorlevel 1 set "PYEXE=python"
)
if not defined PYEXE (
    echo Python was not found.
    echo Install Python 3.10 or newer, ticking "Add python.exe to PATH".
    goto done
)

set "START=tests"
if "%~1"=="" goto run
set "START=tests\%~1"
if exist "%START%\" goto run
echo There is no %START% folder here.
echo Stages: extract, translate, proofreading, overlay, resources
goto done

:run
echo Interpreter: %PYEXE% %PYARG%
echo Running:     %START%
if "%START%"=="tests" echo Time:        about a minute, nearly all of it in resources
echo.
"%PYEXE%" %PYARG% -m unittest discover -s "%START%" -t tests
set "CODE=%ERRORLEVEL%"
echo.
if "%CODE%"=="0" echo ==== PASSED ====
if not "%CODE%"=="0" echo ==== FAILED - scroll up for the lines marked FAIL or ERROR ====

:done
if defined OLDCP chcp %OLDCP% >nul 2>nul
echo.
pause
exit /b %CODE%
