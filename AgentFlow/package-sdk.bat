@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "ARTIFACTS=%ROOT%\artifacts"
set "STAGING=%ARTIFACTS%\AgentFlow.Sdk"
set "BUILD_ROOT=%ARTIFACTS%\sdk-build"
set "ZIP=%ARTIFACTS%\AgentFlow.Sdk.zip"

echo ==========================================
echo AgentFlow SDK packaging
echo Root:      %ROOT%
echo Staging:   %STAGING%
echo Output:    %ZIP%
echo ==========================================

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet was not found in PATH.
    exit /b 1
)

where powershell >nul 2>&1
if errorlevel 1 (
    echo ERROR: powershell was not found in PATH.
    exit /b 1
)

if exist "%STAGING%" rmdir /s /q "%STAGING%"
if exist "%BUILD_ROOT%" rmdir /s /q "%BUILD_ROOT%"
if exist "%ZIP%" del /f /q "%ZIP%"

mkdir "%STAGING%\x64\Debug\plugins" >nul 2>&1
mkdir "%STAGING%\x64\Release\plugins" >nul 2>&1
mkdir "%BUILD_ROOT%" >nul 2>&1

echo.
echo Copying public source projects...
call :CopySourceTree "%STAGING%\x64\Debug\src"
if errorlevel 1 goto :fail
call :CopySourceTree "%STAGING%\x64\Release\src"
if errorlevel 1 goto :fail

echo.
echo Building and packaging Debug plugins...
for /D %%D in ("%ROOT%\plugins\*") do (
    set "PLUGIN_NAME=%%~nxD"
    if exist "%%~fD\!PLUGIN_NAME!.csproj" (
        call :BuildPlugin "!PLUGIN_NAME!" Debug Debug
        if errorlevel 1 goto :fail
    )
)

echo.
echo Building and packaging Release plugins...
for /D %%D in ("%ROOT%\plugins\*") do (
    set "PLUGIN_NAME=%%~nxD"
    if /I not "!PLUGIN_NAME!"=="Node.Broadcast.Demo" (
        if exist "%%~fD\!PLUGIN_NAME!.csproj" (
            call :BuildPlugin "!PLUGIN_NAME!" Release Release
            if errorlevel 1 goto :fail
        )
    )
)

(
    echo AgentFlow SDK
    echo.
    echo This archive contains two complete variants:
    echo   x64\Debug\src      Public project source code
    echo   x64\Debug\plugins  Debug plugin assemblies and PDB files
    echo   x64\Release\src    Public project source code
    echo   x64\Release\plugins Release plugin assemblies
    echo.
    echo Node.Broadcast.Demo is included only in the Debug variant.
) > "%STAGING%\README.txt"

echo.
echo Creating zip...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%STAGING%' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 goto :fail

echo.
echo Package created:
echo   %ZIP%
exit /b 0

:CopySourceTree
set "DEST=%~1"
for %%S in (
    AgentFlow.Contracts
    AgentFlow.Core
    BroadcastKit
    BroadcastKit.Contracts
    FACT.Shared
    Hosting.Logging
) do (
    call :CopySource "%ROOT%\%%S" "%DEST%\%%S"
    if errorlevel 1 exit /b 1
)
exit /b 0

:CopySource
set "SOURCE=%~1"
set "DESTINATION=%~2"
if not exist "%SOURCE%\" (
    echo ERROR: source project not found: %SOURCE%
    exit /b 1
)
echo   %SOURCE%
robocopy "%SOURCE%" "%DESTINATION%" /E /XD bin obj .vs /XF *.user *.suo *.tmp /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
    echo ERROR: failed to copy source project: %SOURCE%
    exit /b 1
)
exit /b 0

:BuildPlugin
set "PLUGIN_NAME=%~1"
set "CONFIGURATION=%~2"
set "TARGET_VARIANT=%~3"
set "PROJECT=%ROOT%\plugins\%PLUGIN_NAME%\%PLUGIN_NAME%.csproj"
set "BUILD_OUT=%BUILD_ROOT%\%TARGET_VARIANT%\%PLUGIN_NAME%"
set "PLUGIN_DEST=%STAGING%\x64\%TARGET_VARIANT%\plugins"

echo   [%CONFIGURATION%] %PLUGIN_NAME%
dotnet build "%PROJECT%" -c "%CONFIGURATION%" -o "%BUILD_OUT%" -v:minimal
if errorlevel 1 (
    echo ERROR: failed to build %PROJECT% in %CONFIGURATION%.
    exit /b 1
)

if not exist "%BUILD_OUT%\%PLUGIN_NAME%.dll" (
    echo ERROR: plugin assembly was not produced: %BUILD_OUT%\%PLUGIN_NAME%.dll
    exit /b 1
)
copy /Y "%BUILD_OUT%\%PLUGIN_NAME%.dll" "%PLUGIN_DEST%\%PLUGIN_NAME%.dll" >nul
if errorlevel 1 (
    echo ERROR: failed to copy %PLUGIN_NAME%.dll.
    exit /b 1
)

if /I "%CONFIGURATION%"=="Debug" (
    if exist "%BUILD_OUT%\%PLUGIN_NAME%.pdb" (
        copy /Y "%BUILD_OUT%\%PLUGIN_NAME%.pdb" "%PLUGIN_DEST%\%PLUGIN_NAME%.pdb" >nul
        if errorlevel 1 (
            echo ERROR: failed to copy %PLUGIN_NAME%.pdb.
            exit /b 1
        )
    )
)

exit /b 0

:fail
echo.
echo SDK packaging failed.
exit /b 1
