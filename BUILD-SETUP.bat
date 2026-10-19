@echo off
setlocal EnableExtensions
title Xtrem Clipboard - Compiler l installateur

REM BUILD-SETUP.bat
REM 1) Publish Release win-x64 self-contained (PublishTrimmed=false)
REM 2) Inno Setup -> XtremClipboard-Setup-{version}.exe
REM 3) Copie vers web\downloads\files\ + versions.json
REM Pas de deploiement SSH. Ne pas committer le .exe Setup.
REM Cette app est autonome : rien n'est partage avec les autres apps de la suite.

cd /d "%~dp0"

set "CONFIG=Release"
set "RID=win-x64"
set "PLATFORM=x64"
set "PUBLISH_DIR=%~dp0artifacts\publish\win-x64"
set "ISS=%~dp0Installer\XtremClipboard.iss"
set "WEB_FILES=%~dp0web\downloads\files"
set "VERSIONS=%~dp0web\downloads\versions.json"
set "CSPROJ=%~dp0src\XtremClipboard\XtremClipboard.csproj"

for /f "usebackq delims=" %%A in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\setup\Read-AppVersion.ps1" -Csproj "%CSPROJ%"`) do (
  set "VERSION=%%A"
  goto :version_done
)
echo [ERREUR] Version introuvable dans XtremClipboard.csproj
exit /b 1
:version_done

set "SETUP_NAME=XtremClipboard-Setup-%VERSION%.exe"
set "OUT_DIR=%TEMP%\XtremClipboard-Inno-%VERSION%"
set "SETUP_EXE=%OUT_DIR%\%SETUP_NAME%"

echo.
echo ========================================
echo  Xtrem Clipboard - Compilation installateur
echo  Version %VERSION%
echo ========================================
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
  echo [ERREUR] dotnet introuvable.
  exit /b 1
)

set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if not defined ISCC (
  echo [ERREUR] ISCC.exe introuvable.
  exit /b 1
)

echo [1/4] Fermeture de XtremClipboard.exe si ouvert...
taskkill /IM XtremClipboard.exe /F >nul 2>&1
ping -n 2 127.0.0.1 >nul

echo [2/4] Publication Release x64...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
mkdir "%PUBLISH_DIR%" 2>nul

dotnet publish "%CSPROJ%" ^
  -c %CONFIG% ^
  -r %RID% ^
  --self-contained true ^
  -p:Platform=%PLATFORM% ^
  -p:PublishDir="%PUBLISH_DIR%\\" ^
  -p:WindowsAppSDKSelfContained=true ^
  -p:PublishTrimmed=false ^
  -p:DebugType=None ^
  -p:DebugSymbols=false
if errorlevel 1 (
  echo [ERREUR] dotnet publish a echoue.
  exit /b 1
)

if not exist "%PUBLISH_DIR%\XtremClipboard.exe" (
  echo [ERREUR] XtremClipboard.exe introuvable dans %PUBLISH_DIR%
  exit /b 1
)
echo       Publication OK

echo [3/4] Installateur Inno...
if exist "%OUT_DIR%" rmdir /s /q "%OUT_DIR%"
mkdir "%OUT_DIR%"
"%ISCC%" "%ISS%" /DMyAppVersion=%VERSION% /O"%OUT_DIR%"
if errorlevel 1 (
  echo [ERREUR] Inno Setup a echoue.
  exit /b 1
)
if not exist "%SETUP_EXE%" (
  echo [ERREUR] Setup introuvable : %SETUP_EXE%
  exit /b 1
)

echo [4/4] Copie locale et versions.json...
if not exist "%WEB_FILES%" mkdir "%WEB_FILES%"
copy /Y "%SETUP_EXE%" "%WEB_FILES%\%SETUP_NAME%" >nul
if errorlevel 1 (
  echo [ERREUR] Copie vers web\downloads\files echouee.
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\setup\Update-VersionsManifest.ps1" -VersionsPath "%VERSIONS%" -Version "%VERSION%" -FileName "%SETUP_NAME%" -ProductName "Xtrem Clipboard" -SetupPrefix "XtremClipboard" -SetupPath "%WEB_FILES%\%SETUP_NAME%"
if errorlevel 1 (
  echo [ERREUR] versions.json a echoue.
  exit /b 1
)

echo.
echo ========================================
echo  TERMINE
echo  %WEB_FILES%\%SETUP_NAME%
echo  Ne pas committer le .exe Setup.
echo ========================================
exit /b 0