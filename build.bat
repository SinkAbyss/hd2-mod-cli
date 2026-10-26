@echo off
rem Tests the core, then builds release\hd2qd.exe (the command line) and
rem release\HD2 Mod Quick Deploy.exe (the window). Both need only the .NET
rem Framework 4.8 that Windows 10 (1903+) and 11 have. Building needs the .NET
rem SDK (8 or later): dotnet on PATH, or set DOTNET to its dotnet.exe.
cd /d "%~dp0"
if "%DOTNET%"=="" set DOTNET=dotnet
set OUT=release
set DEFS=
set NAME=the whole program
"%DOTNET%" test tests\Hd2Mod.Tests.csproj || exit /b 1
"%DOTNET%" build cli\hd2qd.csproj -c Release %DEFS% || exit /b 1
"%DOTNET%" build gui\QuickDeploy\QuickDeploy.csproj -c Release %DEFS% || exit /b 1
if not exist "%OUT%" mkdir "%OUT%"
copy /y "cli\bin\Release\net48\hd2qd.exe" "%OUT%\hd2qd.exe" >nul || exit /b 1
copy /y "gui\QuickDeploy\bin\Release\net48\HD2 Mod Quick Deploy.exe" "%OUT%\HD2 Mod Quick Deploy.exe" >nul || exit /b 1
:copied
echo.
echo Built (%NAME%): %OUT%\hd2qd.exe and %OUT%\HD2 Mod Quick Deploy.exe
