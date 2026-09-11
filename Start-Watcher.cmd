@echo off
title AMD HDR Screenshot Fixer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Fix-AmdScreenshot.ps1"
if errorlevel 1 pause
