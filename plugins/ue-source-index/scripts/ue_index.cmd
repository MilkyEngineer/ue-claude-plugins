@echo off
rem Finds a Python 3 (preferring the one bundled with any installed Unreal Engine) and runs ue_index.py.
rem Override with the UE_INDEX_PYTHON environment variable.
setlocal DisableDelayedExpansion
set "PYCACHE=%USERPROFILE%\.claude\ue-index\python.txt"
set "PY="

if defined UE_INDEX_PYTHON if exist "%UE_INDEX_PYTHON%" set "PY=%UE_INDEX_PYTHON%"
if defined PY goto run

if not exist "%PYCACHE%" goto discover
set /p PY=<"%PYCACHE%"
if defined PY if exist "%PY%" goto run
set "PY="

:discover
for /f "tokens=2,*" %%A in ('reg query "HKLM\SOFTWARE\EpicGames\Unreal Engine" /s /v InstalledDirectory 2^>nul ^| findstr /c:"InstalledDirectory"') do (
  if exist "%%B\Engine\Binaries\ThirdParty\Python3\Win64\python.exe" set "PY=%%B\Engine\Binaries\ThirdParty\Python3\Win64\python.exe"
)
if defined PY goto save
for /f "tokens=2,*" %%A in ('reg query "HKCU\SOFTWARE\Epic Games\Unreal Engine\Builds" 2^>nul ^| findstr /c:"REG_SZ"') do (
  if exist "%%B\Engine\Binaries\ThirdParty\Python3\Win64\python.exe" set "PY=%%B\Engine\Binaries\ThirdParty\Python3\Win64\python.exe"
)
if defined PY goto save
for %%N in (python3.exe python.exe) do (
  for /f "delims=" %%P in ('where %%N 2^>nul') do (
    "%%P" -c "import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)" >nul 2>&1 && (set "PY=%%P" & goto save)
  )
)
echo error: no Python 3.8+ found. Install one, or set UE_INDEX_PYTHON to e.g. ^<Engine^>\Engine\Binaries\ThirdParty\Python3\Win64\python.exe 1>&2
exit /b 1

:save
if not exist "%USERPROFILE%\.claude\ue-index" mkdir "%USERPROFILE%\.claude\ue-index" >nul 2>&1
> "%PYCACHE%" echo %PY%

:run
"%PY%" -X utf8 "%~dp0ue_index.py" %*
exit /b %ERRORLEVEL%
