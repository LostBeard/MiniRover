@echo off
REM ============================================================================
REM Builds the MiniRover nanoFramework firmware (nanoCLR) for the Freenove FNK0053.
REM
REM Prerequisites (see Docs/firmware-build.md):
REM   - ESP-IDF v5.5.5 at %IDF_PATH_OVERRIDE% or C:\Espressif\frameworks\esp-idf-v5.5.5
REM   - LostBeard/nf-interpreter, branch minirover/esp32-wrover, cloned NEXT TO this repo
REM     (default ..\..\nf-interpreter relative to this file's repo root), or set NF_DIR.
REM   - Python 3.13 (ESP-IDF 5.5 export.bat looks for the idf5.5_py3.13_env venv).
REM
REM Usage: firmware\build-firmware.bat            (preset MINIROVER_ESP32)
REM Run from cmd or PowerShell, not Git Bash (Bash mangles cmd /c).
REM If you changed the defconfig, delete %NF_DIR%\sdkconfig first.
REM ============================================================================
setlocal
set "PRESET=%1"
if "%PRESET%"=="" set "PRESET=MINIROVER_ESP32"
if "%NF_DIR%"=="" set "NF_DIR=%~dp0..\..\nf-interpreter"
if "%IDF_DIR%"=="" set "IDF_DIR=C:\Espressif\frameworks\esp-idf-v5.5.5"
if "%IDF_TOOLS_PATH%"=="" set "IDF_TOOLS_PATH=C:\Espressif"
set "VENVPY=%IDF_TOOLS_PATH%\python_env\idf5.5_py3.13_env\Scripts\python.exe"

if not exist "%NF_DIR%\targets\ESP32\defconfig\MINIROVER_ESP32_defconfig" (
    echo [ERROR] %NF_DIR% is not the minirover/esp32-wrover nf-interpreter branch.
    exit /b 1
)

set "PATH=C:\Python313;C:\Python313\Scripts;%PATH%"
set MSYSTEM=
set MSYS=
set PYTHONNOUSERSITE=1

call "%IDF_DIR%\export.bat"
if errorlevel 1 ( echo [ERROR] ESP-IDF export.bat failed & exit /b 1 )

cd /d "%NF_DIR%"
echo ==^> Configuring %PRESET%
cmake --preset %PRESET% -DPython3_EXECUTABLE=%VENVPY% -DPython3_FIND_REGISTRY=NEVER -DPython3_FIND_STRATEGY=LOCATION
if errorlevel 1 ( echo [ERROR] cmake configure failed & exit /b 2 )

echo ==^> Building %PRESET%
cmake --build --preset %PRESET%
if errorlevel 1 ( echo [ERROR] cmake build failed & exit /b 3 )

echo ==^> BUILD OK
dir "%NF_DIR%\build\nanoCLR.bin" "%NF_DIR%\build\bootloader\bootloader.bin" "%NF_DIR%\build\partition_table\partition-table.bin"
endlocal
