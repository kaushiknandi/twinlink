@echo off
setlocal
cd /d "%~dp0.."
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%CSC%" /nologo /target:exe /optimize+ /out:tests\TransferTests.exe src\Transfer.cs src\HttpTransport.cs src\CurlRequest.cs tests\TransferTests.cs
if errorlevel 1 exit /b 1
tests\TransferTests.exe %*
exit /b %errorlevel%
