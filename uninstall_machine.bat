@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo 正在启动 Machine 卸载程序...
python uninstall_machine.py
if errorlevel 1 pause
