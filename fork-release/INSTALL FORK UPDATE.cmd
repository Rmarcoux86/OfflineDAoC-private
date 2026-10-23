@echo off
setlocal DisableDelayedExpansion
cd /d "%~dp0"
rem Installs this fork's server (GameServer.dll) and launcher (launcher\OfflineDAoC.*) into an existing Offline DAoC
rem 0.37 game folder. The files it replaces are backed up first into <game folder>\fork-backups\<date-time>\.
rem Run it again with a newer download to update; run RESTORE STOCK SERVER.cmd to go back.
rem
rem Unattended (used by the launcher's GET UPDATE button):
rem   INSTALL FORK UPDATE.cmd "<game folder>" /auto [/newclient]
rem No questions; it waits for the launcher to close, installs, then starts START OFFLINE DAOC.cmd again
rem (or START OFFLINE DAOC - NEW CLIENT.cmd with /newclient). It only pauses when something fails.
rem
rem Delayed expansion stays off so an exclamation mark in a folder name survives. Paths are only expanded inside
rem quotes or outside parenthesised blocks, so brackets or an ampersand in a folder name do not break the script.

set "AUTO="
set "NEWCLIENT="
if /i "%~2"=="/auto" set "AUTO=1"
if /i "%~3"=="/newclient" set "NEWCLIENT=1"

if not exist "%~dp0GameServer.dll" (
  echo GameServer.dll is missing next to this script. Extract the whole zip, then run it again.
  pause
  exit /b 1
)

echo.
echo  Offline DAoC fork update: fishing and Expedition mode
echo  ======================================================
echo.
echo  This replaces the game server and launcher files in your Offline DAoC 0.37 folder.
echo  Close the game and click STOP SERVER in the launcher before continuing.
echo.

set "GAME="
if defined AUTO set "GAME=%~1"
if defined AUTO goto have_game

if exist "%~dp0..\playable-v0.37\runtime\server\GameServer.dll" set "GAME=%~dp0..\playable-v0.37"
if exist "%~dp0..\runtime\server\GameServer.dll" set "GAME=%~dp0.."
if not defined GAME goto ask_game

for %%I in ("%GAME%") do set "GAME=%%~fI"
echo  Found your game folder: "%GAME%"
set "ANSWER="
set /p "ANSWER=  Install there? [Y/n] "
if /i "%ANSWER%"=="n" set "GAME="
if defined GAME goto have_game

:ask_game
echo  Type or paste the path of your game folder, the one that contains
echo  START OFFLINE DAOC.cmd, for example C:\Games\OfflineDAoC\playable-v0.37
set /p "GAME=  Game folder: "

:have_game
if defined GAME set "GAME=%GAME:"=%"
if not exist "%GAME%\runtime\server\GameServer.dll" (
  echo.
  echo  That folder has no runtime\server\GameServer.dll. Is it the playable-v0.37 folder?
  pause
  exit /b 1
)

set "SERVER_RUNNING="
tasklist /FI "IMAGENAME eq CoreServer.exe" 2>nul | find /i "CoreServer.exe" >nul
if not errorlevel 1 set "SERVER_RUNNING=1"
tasklist /FI "IMAGENAME eq GameServer.exe" 2>nul | find /i "GameServer.exe" >nul
if not errorlevel 1 set "SERVER_RUNNING=1"
if defined SERVER_RUNNING (
  echo.
  echo  The game server is still running. Click STOP SERVER in the launcher, then run this again.
  pause
  exit /b 1
)

rem The launcher's own files can only be replaced once it is closed. The GET UPDATE button closes it right after
rem starting this script, so wait up to 20 seconds for that; when run by hand, ask.
if not exist "%~dp0launcher\OfflineDAoC.exe" goto launcher_closed
set "WAITED=0"
:wait_launcher
tasklist /FI "IMAGENAME eq OfflineDAoC.exe" 2>nul | find /i "OfflineDAoC.exe" >nul
if errorlevel 1 goto launcher_closed
if not defined AUTO goto ask_close_launcher
if %WAITED% GEQ 20 goto launcher_still_open
if %WAITED%==0 echo  Waiting for the launcher to close...
set /a WAITED+=1
timeout /t 1 /nobreak >nul 2>&1 || ping -n 2 127.0.0.1 >nul
goto wait_launcher

:launcher_still_open
echo.
echo  The Offline DAoC launcher is still open. Close every launcher window, then click GET UPDATE again
echo  or run this file by hand.
pause
exit /b 1

:ask_close_launcher
echo  The Offline DAoC launcher is still open. Close its window, then press any key to continue.
pause >nul
goto wait_launcher
:launcher_closed

rem The backup folder is named by the local date and time (yyyyMMdd_HHmmss), independent of the Windows date format.
set "STAMP="
for /f "delims=" %%T in ('powershell -NoProfile -NonInteractive -Command "Get-Date -Format yyyyMMdd_HHmmss" 2^>nul') do set "STAMP=%%T"
if not defined STAMP set "STAMP=backup_%RANDOM%%RANDOM%"
if exist "%GAME%\fork-backups\%STAMP%\" set "STAMP=%STAMP%_%RANDOM%"
set "BACKUP=%GAME%\fork-backups\%STAMP%"
echo.
echo  Backing up the current server and launcher files to
echo    "%BACKUP%"
for %%D in ("runtime\server" "runtime\server\lib" "runtime\server\win-x64") do (
  if exist "%GAME%\%%~D\GameServer.dll" (
    mkdir "%BACKUP%\%%~D" >nul 2>&1
    copy /y "%GAME%\%%~D\GameServer.dll" "%BACKUP%\%%~D\" >nul
    if exist "%GAME%\%%~D\GameServer.pdb" copy /y "%GAME%\%%~D\GameServer.pdb" "%BACKUP%\%%~D\" >nul
  )
)
if exist "%~dp0launcher\OfflineDAoC.exe" (
  for %%D in ("runtime" "new-client\runtime") do (
    if exist "%GAME%\%%~D\OfflineDAoC.exe" (
      mkdir "%BACKUP%\%%~D" >nul 2>&1
      copy /y "%GAME%\%%~D\OfflineDAoC.*" "%BACKUP%\%%~D\" >nul
    )
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
if exist "%~dp0launcher\OfflineDAoC.exe" (
  echo  Installing the fork's launcher...
  for %%D in ("runtime" "new-client\runtime") do (
    if exist "%GAME%\%%~D\OfflineDAoC.exe" (
      copy /y "%~dp0launcher\OfflineDAoC.*" "%GAME%\%%~D\" >nul || set "FAILED=1"
    )
  )
)
if not defined FAILED goto copied
echo.
echo  Copying failed. Make sure the game, the server and the launcher are closed, then run this again.
echo  The files from before this attempt are in "%BACKUP%"
pause
exit /b 1

:copied
if exist "%~dp0fork-version.txt" copy /y "%~dp0fork-version.txt" "%GAME%\runtime\fork-version.txt" >nul
if not defined AUTO goto done_by_hand

echo.
echo  Done. Starting the launcher again...
set "LAUNCH=START OFFLINE DAOC.cmd"
if defined NEWCLIENT if exist "%GAME%\START OFFLINE DAOC - NEW CLIENT.cmd" set "LAUNCH=START OFFLINE DAOC - NEW CLIENT.cmd"
if not exist "%GAME%\%LAUNCH%" goto launch_missing
rem Change to the game folder and call the start file by name: "call" expands %% signs a second time, so the full
rem path (which may contain them) is never passed through it. The start file starts the launcher with "start" and
rem returns, so this window closes.
cd /d "%GAME%"
call "%LAUNCH%"
exit /b 0

:launch_missing
echo  "%GAME%\%LAUNCH%" is missing. Start the game as usual.
pause
exit /b 0

:done_by_hand
echo.
echo  Done. Start the game as usual: START OFFLINE DAOC.cmd, START SERVER, ENTER REALM.
echo  In game: /fish pole to start fishing, /run new on a fresh level-1 character for Expedition mode.
echo  From now on the launcher shows GET UPDATE instead of START SERVER when a newer fork update is out.
echo  To go back to the stock server later, run RESTORE STOCK SERVER.cmd.
echo.
pause
