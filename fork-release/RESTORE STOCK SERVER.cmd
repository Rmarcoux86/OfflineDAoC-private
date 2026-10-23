@echo off
setlocal DisableDelayedExpansion
cd /d "%~dp0"
rem Puts back the stock files that INSTALL FORK UPDATE.cmd backed up. The oldest backup in fork-backups holds the
rem files from before the first fork install (later backups hold earlier fork versions), so each file comes from the
rem oldest backup that has it. It also removes runtime\fork-version.txt.

echo.
echo  Restore the stock Offline DAoC server
echo  =====================================
echo  Close the game, click STOP SERVER and close the launcher before continuing.
echo.

set "GAME="
if exist "%~dp0..\playable-v0.37\fork-backups\" set "GAME=%~dp0..\playable-v0.37"
if exist "%~dp0..\fork-backups\" set "GAME=%~dp0.."
if defined GAME for %%I in ("%GAME%") do set "GAME=%%~fI"
if not defined GAME (
  echo  Type or paste the path of your game folder (the one with START OFFLINE DAOC.cmd^):
  set /p "GAME=  Game folder: "
)
set "GAME=%GAME:"=%"
if not exist "%GAME%\fork-backups\" (
  echo  No fork-backups folder in "%GAME%". Nothing to restore.
  pause
  exit /b 1
)

:wait_launcher
tasklist /FI "IMAGENAME eq OfflineDAoC.exe" 2>nul | find /i "OfflineDAoC.exe" >nul
if errorlevel 1 goto launcher_closed
echo  The Offline DAoC launcher is still open. Close its window, then press any key to continue.
pause >nul
goto wait_launcher
:launcher_closed

set "SERVER_FROM="
set "LAUNCHER_FROM="
set "NEWCLIENT_FROM="
for /f "delims=" %%B in ('dir /b /ad /o:d "%GAME%\fork-backups"') do (
  if not defined SERVER_FROM if exist "%GAME%\fork-backups\%%B\runtime\server\GameServer.dll" set "SERVER_FROM=%%B"
  if not defined LAUNCHER_FROM if exist "%GAME%\fork-backups\%%B\runtime\OfflineDAoC.exe" set "LAUNCHER_FROM=%%B"
  if not defined NEWCLIENT_FROM if exist "%GAME%\fork-backups\%%B\new-client\runtime\OfflineDAoC.exe" set "NEWCLIENT_FROM=%%B"
)

if defined SERVER_FROM (
  echo  Restoring the server from "fork-backups\%SERVER_FROM%" ...
  for %%D in ("runtime\server" "runtime\server\lib" "runtime\server\win-x64") do (
    if exist "%GAME%\fork-backups\%SERVER_FROM%\%%~D\GameServer.dll" (
      copy /y "%GAME%\fork-backups\%SERVER_FROM%\%%~D\GameServer.dll" "%GAME%\%%~D\" >nul
      if exist "%GAME%\fork-backups\%SERVER_FROM%\%%~D\GameServer.pdb" copy /y "%GAME%\fork-backups\%SERVER_FROM%\%%~D\GameServer.pdb" "%GAME%\%%~D\" >nul
    )
  )
)
if defined LAUNCHER_FROM (
  echo  Restoring the launcher from "fork-backups\%LAUNCHER_FROM%" ...
  copy /y "%GAME%\fork-backups\%LAUNCHER_FROM%\runtime\OfflineDAoC.*" "%GAME%\runtime\" >nul
)
if defined NEWCLIENT_FROM (
  echo  Restoring the new client's launcher from "fork-backups\%NEWCLIENT_FROM%" ...
  copy /y "%GAME%\fork-backups\%NEWCLIENT_FROM%\new-client\runtime\OfflineDAoC.*" "%GAME%\new-client\runtime\" >nul
)
if exist "%GAME%\runtime\fork-version.txt" del /q "%GAME%\runtime\fork-version.txt"
echo  Done. The stock server is back; your saves were never touched.
pause
