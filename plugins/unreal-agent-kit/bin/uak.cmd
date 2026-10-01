@echo off
rem Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.
rem
rem `uak` on PATH for PowerShell and cmd on Windows: Claude Code puts an enabled plugin's bin/ folder on the PATH of the shell
rem it runs commands in. This runs the uak published for this plugin's version, %UAK_HOME%\<version>\uak.exe (UAK_HOME
rem defaults to %USERPROFILE%\.unreal-agent-kit; see docs\INSTALL.md), with every argument as given, and exits with its exit
rem code. When that uak isn't published, it says so and exits 2, uak's setup-error code. bin/uak does the same for sh.
rem No parenthesised blocks: a path with "(" or ")" in it, such as "Program Files (x86)", would end them early.
setlocal
for %%R in ("%~dp0..") do set "UAK_SHIM_ROOT=%%~fR"
set "UAK_SHIM_VERSION="
for /f "usebackq tokens=2 delims=:, " %%V in (`findstr /c:"\"version\"" "%UAK_SHIM_ROOT%\.claude-plugin\plugin.json"`) do if not defined UAK_SHIM_VERSION set "UAK_SHIM_VERSION=%%~V"
if defined UAK_SHIM_VERSION goto :home
>&2 echo uak: no version in %UAK_SHIM_ROOT%\.claude-plugin\plugin.json, so the uak to run is unknown.
exit /b 2

:home
set "UAK_SHIM_HOME=%USERPROFILE%\.unreal-agent-kit"
if defined UAK_HOME set "UAK_SHIM_HOME=%UAK_HOME%"
set "UAK_SHIM_EXE=%UAK_SHIM_HOME%\%UAK_SHIM_VERSION%\uak.exe"
if exist "%UAK_SHIM_EXE%" goto :run
>&2 echo uak: uak %UAK_SHIM_VERSION% is not published in %UAK_SHIM_HOME%\%UAK_SHIM_VERSION%. Publish it as docs\INSTALL.md says (in %UAK_SHIM_ROOT%).
exit /b 2

:run
"%UAK_SHIM_EXE%" %*
exit /b %ERRORLEVEL%
