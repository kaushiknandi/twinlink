@echo off
setlocal
cd /d "%~dp0"
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Install or enable .NET Framework 4.8, then run this file again.
  exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:TwinLink.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll src\*.cs
exit /b %errorlevel%
