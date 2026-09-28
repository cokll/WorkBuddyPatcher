@echo off
rem Build WorkBuddyPatcher.exe (.NET Framework 4, uses system csc, no SDK needed)
rem Regenerate payloads first when JS patch scripts change: node _gen_payloads.js
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo .NET Framework 4 csc.exe not found
  exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:anycpu /out:WorkBuddyPatcher.exe /win32manifest:app.manifest ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  WorkBuddyPatcher.cs PatchPayloads.cs
if errorlevel 1 (
  echo Build FAILED
  exit /b 1
)
echo Build OK: WorkBuddyPatcher.exe
