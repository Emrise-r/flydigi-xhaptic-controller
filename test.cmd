@echo off
rem Builds and runs the unit tests (tests\*.cs together with src\**\*.cs) with the same C# 5 compiler as build.cmd.
rem The test exe runs from tests\bin, which also holds the config.json and data\ the tests create.
rem Usage: test.cmd [name filter]
setlocal
set OUT=%~dp0tests\bin
if not exist "%OUT%" mkdir "%OUT%"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /optimize+ /codepage:65001 ^
  /main:FlydigiHapticsGuard.Tests.TestRunner /out:"%OUT%\HapticsGuard.Tests.exe" ^
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /recurse:"%~dp0src\*.cs" /recurse:"%~dp0tests\*.cs" || exit /b 1
"%OUT%\HapticsGuard.Tests.exe" %*
