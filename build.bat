@echo off
rem Builds MixDial.exe with the C# compiler that ships with Windows (.NET Framework 4).
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo .NET Framework 4 C# compiler not found.
    exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /out:MixDial.exe /win32icon:MixDial.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll MixDial.cs
if errorlevel 1 exit /b 1
echo Built MixDial.exe
