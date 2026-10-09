@echo off
rem ============================================================
rem  Build MonitorOSD.exe with the .NET Framework compiler
rem  that ships with Windows. No SDK / third-party deps needed.
rem ============================================================
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe not found
    exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:"%~dp0MonitorOSD.exe" "%~dp0MonitorOSD.cs"
if errorlevel 1 (
    echo Build FAILED
    exit /b 1
)
echo Build OK: %~dp0MonitorOSD.exe
