@echo off
setlocal
cd /d "%~dp0"

rem Lance l'exe de dev DEJA compile (stage + signature = tools/Launch-DevBuild.ps1).
rem Ce script ne compile pas.

set "EXE=%LOCALAPPDATA%\XtremClipboard\dev-run\XtremClipboard.exe"

rem Option fresh : rejouer la config initiale (settings.json aux deux emplacements de donnees)
if /i "%~1"=="fresh" (
  del /q "%LOCALAPPDATA%\XtremSuite\XtremClipboard\settings.json" >nul 2>&1
  del /q "%LOCALAPPDATA%\XtremClipboard\settings.json" >nul 2>&1
  echo settings.json efface - la config initiale sera rejouee.
)

rem Ferme l'exe s'il tourne deja.
taskkill /IM XtremClipboard.exe /F >nul 2>&1

if not exist "%EXE%" (
  echo Executable introuvable : %EXE%
  echo Compiler d'abord : dotnet build src\XtremClipboard\XtremClipboard.csproj -c Debug -p:Platform=x64
  pause
  exit /b 1
)

echo Lancement de Xtrem Clipboard...
start "" "%EXE%"
exit /b 0
