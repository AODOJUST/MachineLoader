@echo off
rem Machine loader - apply a downloaded update (run AFTER fully exiting the game).
rem ASCII-only on purpose: cmd.exe decodes .bat with the OEM code page, so non-ASCII
rem text here can turn into garbage. All localized output comes from the Python side.
setlocal enableextensions
chcp 65001 >nul
cd /d "%~dp0"

call :find_python
if not defined PY exit /b 9009

set "PYTHONUTF8=1"
set "PYTHONIOENCODING=utf-8"

echo Applying Machine update...
%PY% "%~dp0machine_update.py" %*
set "RC=%ERRORLEVEL%"

if not "%RC%"=="0" (
  echo.
  echo [ERROR] machine_update.py failed with exit code %RC%
  echo [HINT]  see Machine\logs\machine_update.log for details
  echo [HINT]  1 = bad arguments / game dir   2 = hash mismatch   3 = signature rejected
  echo [HINT]  4 = installer failed          5 = rolled back      6 = cancelled
  echo [HINT]  7 = game still running        8 = permission denied
  pause
  exit /b %RC%
)
exit /b 0

:find_python
set "PY="
rem Prefer the Windows Python launcher with an explicit 3.x request.
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
