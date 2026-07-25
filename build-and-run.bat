@echo off
:run
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-and-run.ps1"
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0prompt-dialog.ps1"
if %errorlevel%==1 goto run
exit
