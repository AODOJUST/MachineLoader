@echo off
rem Machine loader - one click uninstall (restores the vanilla game assembly).
rem ASCII-only on purpose (cmd.exe reads .bat with the OEM code page).
setlocal enableextensions
chcp 65001 >nul
cd /d "%~dp0"

call :find_python
if not defined PY exit /b 9009

set "PYTHONUTF8=1"
set "PYTHONIOENCODING=utf-8"

echo Starting Machine uninstaller...
%PY% "%~dp0uninstall_machine.py" %*
set "RC=%ERRORLEVEL%"

if not "%RC%"=="0" (
  echo.
  echo [ERROR] uninstall_machine.py failed with exit code %RC%
  echo [HINT]  If the game is running, close it and retry.
  echo [HINT]  If a file is locked by antivirus, add the game folder to its whitelist.
  pause
  exit /b %RC%
)
exit /b 0

:find_python
set "PY="
where py >nul 2>nul
if not errorlevel 1 (
  py -3 -c "import sys" >nul 2>nul
  if not errorlevel 1 set "PY=py -3"
)
if not defined PY (
  where python >nul 2>nul
  if not errorlevel 1 set "PY=python"
)
if not defined PY (
  where python3 >nul 2>nul
  if not errorlevel 1 set "PY=python3"
)
if not defined PY (
  echo.
  echo [ERROR] Python 3 was not found.
  echo [ERROR] Install Python 3 from https://www.python.org/downloads/windows/
  echo [ERROR] and tick "Add python.exe to PATH" during setup, then retry.
  pause
)
exit /b 0
