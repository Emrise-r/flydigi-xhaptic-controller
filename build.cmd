@echo off
rem Builds HapticsGuard.exe from src\**\*.cs with the .NET Framework 4 compiler that ships with Windows (C# 5).
rem HapticsGuard.csproj builds the same sources with MSBuild / Visual Studio.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 ^
  /out:"%~dp0HapticsGuard.exe" /win32manifest:"%~dp0app.manifest" /win32icon:"%~dp0assets\app.ico" ^
  /resource:"%~dp0assets\app.ico",FlydigiHapticsGuard.app.ico /resource:"%~dp0assets\app-off.ico",FlydigiHapticsGuard.app-off.ico ^
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /recurse:"%~dp0src\*.cs"
