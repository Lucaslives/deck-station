@echo off
cd /d %~dp0
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize /target:winexe /win32icon:DeckStation.ico /out:DeckStation.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.Core.dll *.cs
pause
