@echo off
if exist "%~dp0artifacts\application-icon\win-x64\AvaMedia.Desktop.exe" (
  start "AvaMedia" "%~dp0artifacts\application-icon\win-x64\AvaMedia.Desktop.exe"
  exit /b
)
start "AvaMedia" "%~dp0artifacts\release\1.0.2\win-x64\AvaMedia.Desktop.exe"
