@echo off
REM Job Radar — native desktop app (Avalonia)
cd /d "%~dp0"
dotnet run --project src\JobRadar.Desktop -c Release
