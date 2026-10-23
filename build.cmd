@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
title FaceTrackingApp - Build

echo ============================================================
echo   FaceTrackingApp - eigenstaendige EXE erzeugen (.NET 10, x64)
echo ============================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  echo FEHLER: "dotnet" wurde nicht gefunden.
  echo Bitte das .NET 10 SDK installieren:  winget install Microsoft.DotNet.SDK.10
  echo.
  pause
  exit /b 1
)

dotnet --list-sdks | findstr /b /c:"10." >nul
if errorlevel 1 (
  echo FEHLER: Kein .NET 10 SDK installiert. Gefunden:
  dotnet --list-sdks
  echo.
  echo Installieren mit:  winget install Microsoft.DotNet.SDK.10
  pause
  exit /b 1
)

echo Alte Build-Ausgaben entfernen ...
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
if exist publish rmdir /s /q publish

echo Baue und veroeffentliche (das dauert beim ersten Mal 1-3 Minuten) ...
echo.
dotnet publish FaceTrackingApp.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:DebugType=none -o publish > build.log 2>&1
set RC=%ERRORLEVEL%

type build.log
echo.
if not "%RC%"=="0" (
  echo ============================================================
  echo   BUILD FEHLGESCHLAGEN - Details stehen in build.log
  echo ============================================================
  pause
  exit /b %RC%
)

echo ============================================================
echo   FERTIG:  %~dp0publish\FaceTrackingApp.exe
echo   Der ganze Ordner "publish" kann kopiert werden
echo   (EXE + Unterordner Assets), .NET muss dort nicht installiert sein.
echo ============================================================
explorer "%~dp0publish"
pause
