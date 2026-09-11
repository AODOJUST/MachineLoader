@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo 正在启动 Machine 安装程序...
python install_machine.py
if errorlevel 1 pause
