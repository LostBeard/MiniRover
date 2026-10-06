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

REM SpawnDev.nanoFramework.WebRTC (WebRTC interop + our libpeer build). Submodule location by default.
if "%SDNF_DIR%"=="" set "SDNF_DIR=%~dp0external\SpawnDev.nanoFramework.WebRTC"
if not exist "%SDNF_DIR%\native\SpawnDev.nanoFramework.WebRTC\FindINTEROP-SpawnDev.nanoFramework.WebRTC.cmake" (
    echo [ERROR] SpawnDev.nanoFramework.WebRTC not found at %SDNF_DIR% - run: git submodule update --init --recursive
    exit /b 1
)
REM CMake wants forward slashes.
set "SDNF_CMAKE=%SDNF_DIR:\=/%"
REM MiniRover.Native (this repo): ESP32 functions nanoFramework does not expose (WiFi signal, power save, camera next).
set "MRN_DIR=%~dp0native\MiniRover.Native"
set "MRN_CMAKE=%MRN_DIR:\=/%"
set "INTEROP_ASSEMBLIES=SpawnDev.nanoFramework.WebRTC MiniRover.Native"
set "INTEROP_PATHS=%SDNF_CMAKE%/native/SpawnDev.nanoFramework.WebRTC;%MRN_CMAKE%"
set "EXTRA_COMPONENTS=%SDNF_CMAKE%/native/components/libpeer"

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
cmake --preset %PRESET% -DPython3_EXECUTABLE=%VENVPY% -DPython3_FIND_REGISTRY=NEVER -DPython3_FIND_STRATEGY=LOCATION "-DNF_INTEROP_ASSEMBLIES=%INTEROP_ASSEMBLIES%" "-DNF_INTEROP_SEARCH_PATHS=%INTEROP_PATHS%" "-DNF_EXTRA_IDF_COMPONENT_DIRS=%EXTRA_COMPONENTS%"
if errorlevel 1 ( echo [ERROR] cmake configure failed & exit /b 2 )

echo ==^> Building %PRESET%
cmake --build --preset %PRESET%
if errorlevel 1 ( echo [ERROR] cmake build failed & exit /b 3 )

echo ==^> BUILD OK
dir "%NF_DIR%\build\nanoCLR.bin" "%NF_DIR%\build\bootloader\bootloader.bin" "%NF_DIR%\build\partition_table\partition-table.bin"
endlocal
