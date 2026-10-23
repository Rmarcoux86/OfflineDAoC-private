@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"
rem Installs this fork's server (GameServer.dll) into an existing Offline DAoC 0.37 game folder.
rem The stock files it replaces are backed up first into <game folder>\fork-backups\<date-time>\.
rem Run it again with a newer download to update; run RESTORE STOCK SERVER.cmd to go back.

if not exist "%~dp0GameServer.dll" (
  echo GameServer.dll is missing next to this script. Extract the whole zip, then run it again.
  pause
  exit /b 1
)

echo.
echo  Offline DAoC fork update: fishing and Expedition mode
echo  ======================================================
echo.
echo  This replaces the game server file in your Offline DAoC 0.37 folder.
echo  Close the game and click STOP SERVER in the launcher before continuing.
echo.

set "GAME="
if exist "%~dp0..\playable-v0.37\runtime\server\GameServer.dll" set "GAME=%~dp0..\playable-v0.37"
if exist "%~dp0..\runtime\server\GameServer.dll" set "GAME=%~dp0.."

if defined GAME (
  for %%I in ("%GAME%") do set "GAME=%%~fI"
  echo  Found your game folder: !GAME!
  set /p "ANSWER=  Install there? [Y/n] "
  if /i "!ANSWER!"=="n" set "GAME="
)

if not defined GAME (
  echo  Type or paste the path of your game folder, the one that contains
  echo  START OFFLINE DAOC.cmd, for example C:\Games\OfflineDAoC\playable-v0.37
  set /p "GAME=  Game folder: "
)
set "GAME=%GAME:"=%"
if not exist "%GAME%\runtime\server\GameServer.dll" (
  echo.
  echo  That folder has no runtime\server\GameServer.dll. Is it the playable-v0.37 folder?
  pause
  exit /b 1
)

tasklist /FI "IMAGENAME eq GameServer.exe" 2>nul | find /i "GameServer.exe" >nul
if not errorlevel 1 (
  echo.
  echo  The game server is still running. Click STOP SERVER in the launcher, then run this again.
  pause
  exit /b 1
)

for /f "tokens=1-6 delims=/:. " %%a in ("%date% %time%") do set "STAMP=%%c-%%a-%%b_%%d%%e%%f"
set "STAMP=%STAMP: =0%"
set "BACKUP=%GAME%\fork-backups\%STAMP%"
echo.
echo  Backing up the current server files to
echo    %BACKUP%
for %%D in ("runtime\server" "runtime\server\lib" "runtime\server\win-x64") do (
  if exist "%GAME%\%%~D\GameServer.dll" (
    mkdir "%BACKUP%\%%~D" >nul 2>&1
    copy /y "%GAME%\%%~D\GameServer.dll" "%BACKUP%\%%~D\" >nul
    if exist "%GAME%\%%~D\GameServer.pdb" copy /y "%GAME%\%%~D\GameServer.pdb" "%BACKUP%\%%~D\" >nul
  )
)

echo  Installing the fork's server...
set "FAILED="
for %%D in ("runtime\server" "runtime\server\lib" "runtime\server\win-x64") do (
  if exist "%GAME%\%%~D\" (
    copy /y "%~dp0GameServer.dll" "%GAME%\%%~D\" >nul || set "FAILED=1"
    if exist "%~dp0GameServer.pdb" copy /y "%~dp0GameServer.pdb" "%GAME%\%%~D\" >nul
  )
)
if defined FAILED (
  echo.
  echo  Copying failed. Make sure the game and server are closed, then run this again.
  pause
  exit /b 1
)

echo.
echo  Done. Start the game as usual: START OFFLINE DAOC.cmd, START SERVER, ENTER REALM.
echo  In game: /fish pole to start fishing, /run new on a fresh level-1 character for Expedition mode.
echo  To go back to the stock server later, run RESTORE STOCK SERVER.cmd.
echo.
pause
