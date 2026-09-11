@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo 正在应用 Machine 更新...
python machine_update.py
pause
