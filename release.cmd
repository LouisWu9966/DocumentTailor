@echo off
chcp 65001 > nul
setlocal
cd /d "%~dp0"

rem  NOTE: keep this file pure ASCII.  cmd.exe mis-advances its file pointer when a
rem  batch file contains multi-byte characters, which makes it resume parsing in the
rem  middle of a later line.  Chinese text belongs in README.md, not in here.

set "APP=src\DocumentTailor\DocumentTailor.csproj"
set "TESTS=tests\DocumentTailor.SmokeTests\DocumentTailor.SmokeTests.csproj"
set "OUTDIR=publish"
set "LOG=%~dp0release-log.txt"
set "STEP=%TEMP%\dt-release-step.txt"
set "INTERACTIVE="
set "CMD=%~1"

if not "%CMD%"=="" goto :resolve

set "INTERACTIVE=1"
echo.
echo   Document Tailor
echo   ------------------------------------------------
echo     1  build     compile Release
echo     2  test      compile + run smoke tests
echo     3  publish   self-contained single-file EXE
echo     4  all       build + test + publish
echo     5  clean     remove bin / obj / publish
echo.
set /p "CMD=Choose 1-5 (or type the name): "
echo.

:resolve
if "%CMD%"=="1" set "CMD=build"
if "%CMD%"=="2" set "CMD=test"
if "%CMD%"=="3" set "CMD=publish"
if "%CMD%"=="4" set "CMD=all"
if "%CMD%"=="5" set "CMD=clean"

where dotnet > nul 2>&1
if errorlevel 1 (
  echo [ERROR] dotnet not found on PATH.
  echo         Install .NET SDK 9: https://dotnet.microsoft.com/download/dotnet/9.0
  goto :fail
)

echo Document Tailor release log > "%LOG%"
echo command=%CMD%  %DATE% %TIME% >> "%LOG%"

if /i "%CMD%"=="build"   goto :run_build
if /i "%CMD%"=="test"    goto :run_test
if /i "%CMD%"=="publish" goto :run_publish
if /i "%CMD%"=="all"     goto :run_all
if /i "%CMD%"=="clean"   goto :run_clean

echo [ERROR] unknown command: %CMD%
echo Usage: release.cmd [build ^| test ^| publish ^| all ^| clean]
goto :fail


:run_build
call :step_build
if errorlevel 1 goto :fail
goto :done

:run_test
call :step_test
if errorlevel 1 goto :fail
goto :done

:run_publish
call :step_publish
if errorlevel 1 goto :fail
goto :done

:run_all
call :step_build
if errorlevel 1 goto :fail
call :step_test
if errorlevel 1 goto :fail
call :step_publish
if errorlevel 1 goto :fail
goto :done

:run_clean
call :step_clean
if errorlevel 1 goto :fail
goto :done


:step_build
echo.
echo ================ BUILD (Release) ================
echo   dotnet build "%APP%" -c Release
echo   working, please wait...
echo. >> "%LOG%"
echo ===== BUILD ===== >> "%LOG%"
dotnet build "%APP%" -c Release --nologo > "%STEP%" 2>&1
set "RC=%ERRORLEVEL%"
type "%STEP%"
type "%STEP%" >> "%LOG%"
exit /b %RC%

:step_test
echo.
echo ================ SMOKE TESTS ================
echo   dotnet run --project "%TESTS%" -c Release
echo   Needs a Windows OCR language pack (Settings ^> Time ^& language).
echo   working, this takes a while...
echo. >> "%LOG%"
echo ===== TEST ===== >> "%LOG%"
dotnet run --project "%TESTS%" -c Release > "%STEP%" 2>&1
set "RC=%ERRORLEVEL%"
type "%STEP%"
type "%STEP%" >> "%LOG%"
exit /b %RC%

:step_publish
echo.
echo ================ PUBLISH ================
echo   dotnet publish "%APP%" -c Release -r win-x64 --self-contained true -o "%OUTDIR%"
echo   working, please wait...
echo. >> "%LOG%"
echo ===== PUBLISH ===== >> "%LOG%"
dotnet publish "%APP%" -c Release -r win-x64 --self-contained true -o "%OUTDIR%" --nologo > "%STEP%" 2>&1
set "RC=%ERRORLEVEL%"
type "%STEP%"
type "%STEP%" >> "%LOG%"
if not "%RC%"=="0" exit /b %RC%
if not exist "%OUTDIR%\DocumentTailor.exe" (
  echo [ERROR] publish reported success but "%OUTDIR%\DocumentTailor.exe" is missing.
  echo [ERROR] publish reported success but the EXE is missing. >> "%LOG%"
  exit /b 1
)
exit /b 0

:step_clean
echo.
echo The following folders will be deleted:
echo   src\DocumentTailor\bin                 src\DocumentTailor\obj
echo   tests\DocumentTailor.SmokeTests\bin    tests\DocumentTailor.SmokeTests\obj
echo   %OUTDIR%
echo.
set /p "CONFIRM=Delete them? (y/N): "
if /i not "%CONFIRM%"=="y" echo Cancelled.& exit /b 1
if exist "src\DocumentTailor\bin"  rd /s /q "src\DocumentTailor\bin"
if exist "src\DocumentTailor\obj"  rd /s /q "src\DocumentTailor\obj"
if exist "tests\DocumentTailor.SmokeTests\bin" rd /s /q "tests\DocumentTailor.SmokeTests\bin"
if exist "tests\DocumentTailor.SmokeTests\obj" rd /s /q "tests\DocumentTailor.SmokeTests\obj"
if exist "%OUTDIR%" rd /s /q "%OUTDIR%"
echo Cleaned.
exit /b 0


:done
echo.
echo ------------------------------------------------
echo   OK: %CMD%
if /i "%CMD%"=="publish" echo   EXE: %~dp0%OUTDIR%\DocumentTailor.exe
if /i "%CMD%"=="all"     echo   EXE: %~dp0%OUTDIR%\DocumentTailor.exe
echo   log: %LOG%
echo ------------------------------------------------
if defined INTERACTIVE pause
exit /b 0

:fail
echo.
echo ------------------------------------------------
echo   FAILED: %CMD%
echo   full output: %LOG%
echo ------------------------------------------------
if defined INTERACTIVE pause
exit /b 1
