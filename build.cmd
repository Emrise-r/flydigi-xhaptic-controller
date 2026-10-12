@echo off
rem Builds HapticsGuard.exe with the .NET Framework 4 compiler that ships with Windows.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 ^
  /out:"%~dp0HapticsGuard.exe" ^
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  "%~dp0HapticsGuard.cs"
