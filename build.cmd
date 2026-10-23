@echo off
rem ===========================================================================
rem  86 RuntimePack - build script
rem  Builds a dependency-free native exe using the .NET Framework C# compiler
rem  that ships with Windows (no SDK / Python / Node required).
rem
rem  This file is intentionally PURE ASCII. cmd.exe decodes batch files with
rem  the *console* code page, so a Chinese literal here would mangle the output
rem  file name whenever the console is not running at code page 936.
rem  The product name therefore lives in src\appname.txt (UTF-8) and is read
rem  back with an explicit encoding by the PowerShell step below.
rem
rem  Usage: double-click this file, or run it from any console.
rem ===========================================================================
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. Please make sure .NET Framework 4.x is installed.
    pause
    exit /b 1
)

set "TMPOUT=%~dp0_build_out.exe"

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
    /out:"%TMPOUT%" ^
    /win32manifest:"%~dp0src\app.manifest" ^
    /win32icon:"%~dp0src\app.ico" ^
    /resource:"%~dp0src\app.ico",app.ico ^
    /reference:System.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.IO.Compression.FileSystem.dll ^
    "%~dp0src\RunTimePack.cs"

if errorlevel 1 (
    echo.
    echo [FAILED] Compilation error.
    pause
    exit /b 1
)

powershell -NoProfile -Command "$ErrorActionPreference='Stop'; $dir='%~dp0'; $n=[IO.File]::ReadAllText($dir+'src\appname.txt',[Text.Encoding]::UTF8).Trim(); $out=Join-Path $dir ($n+'.exe'); if(Test-Path -LiteralPath $out){Remove-Item -LiteralPath $out -Force}; [IO.File]::Move($dir+'_build_out.exe',$out); Write-Host ''; Write-Host ('[DONE] ' + $out)"

if errorlevel 1 (
    echo.
    echo [FAILED] Could not rename the output to the product name.
    pause
    exit /b 1
)

echo.
echo Copy this exe to any Windows 10 / 11 PC and double-click it - it runs as is.
echo Installers are downloaded on first run into a "packages" folder next to the
echo exe; keep that folder to install offline on other machines later.
echo.
pause
endlocal