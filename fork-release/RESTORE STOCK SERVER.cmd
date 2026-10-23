@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"
rem Puts back the server files that INSTALL FORK UPDATE.cmd backed up (the newest backup in fork-backups).

echo.
echo  Restore the stock Offline DAoC server
echo  =====================================
echo  Close the game and click STOP SERVER in the launcher before continuing.
echo.

set "GAME="
if exist "%~dp0..\playable-v0.37\fork-backups\" set "GAME=%~dp0..\playable-v0.37"
if exist "%~dp0..\fork-backups\" set "GAME=%~dp0.."
if defined GAME for %%I in ("%GAME%") do set "GAME=%%~fI"
if not defined GAME (
  echo  Type or paste the path of your game folder (the one with START OFFLINE DAOC.cmd):
  set /p "GAME=  Game folder: "
)
set "GAME=%GAME:"=%"
if not exist "%GAME%\fork-backups\" (
  echo  No fork-backups folder in %GAME%. Nothing to restore.
  pause
  exit /b 1
)

set "LATEST="
for /f "delims=" %%B in ('dir /b /ad /o-n "%GAME%\fork-backups"') do if not defined LATEST set "LATEST=%%B"
echo  Restoring from fork-backups\%LATEST% ...
for %%D in ("runtime\server" "runtime\server\lib" "runtime\server\win-x64") do (
  if exist "%GAME%\fork-backups\%LATEST%\%%~D\GameServer.dll" (
    copy /y "%GAME%\fork-backups\%LATEST%\%%~D\GameServer.dll" "%GAME%\%%~D\" >nul
    if exist "%GAME%\fork-backups\%LATEST%\%%~D\GameServer.pdb" copy /y "%GAME%\fork-backups\%LATEST%\%%~D\GameServer.pdb" "%GAME%\%%~D\" >nul
  )
)
echo  Done. The stock server is back; your saves were never touched.
pause
