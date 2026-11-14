@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "ARTIFACTS=%ROOT%\artifacts"
set "STAGING=%ARTIFACTS%\AgentFlow.Sdk.Binaries"
set "BUILD_ROOT=%ARTIFACTS%\sdk-dll-build"
set "ZIP=%ARTIFACTS%\AgentFlow.Sdk.Binaries.zip"

echo ==========================================
echo AgentFlow binary SDK packaging
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

for /f "usebackq delims=" %%T in (`dotnet msbuild "%ROOT%\AgentFlow.Contracts\AgentFlow.Contracts.csproj" -nologo -getProperty:AgentFlowTargetFramework`) do set "AGENTFLOW_TARGET_FRAMEWORK=%%T"
for /f "usebackq delims=" %%T in (`dotnet msbuild "%ROOT%\plugins\Node.AutoStart\Node.AutoStart.csproj" -nologo -getProperty:PluginTargetFramework`) do set "AGENTFLOW_PLUGIN_TARGET_FRAMEWORK=%%T"
if not defined AGENTFLOW_TARGET_FRAMEWORK (
    echo ERROR: AgentFlowTargetFramework is not defined.
    exit /b 1
)
if not defined AGENTFLOW_PLUGIN_TARGET_FRAMEWORK (
    echo ERROR: PluginTargetFramework is not defined.
    exit /b 1
)
if exist "%STAGING%" rmdir /s /q "%STAGING%"
if exist "%BUILD_ROOT%" rmdir /s /q "%BUILD_ROOT%"
if exist "%ZIP%" del /f /q "%ZIP%"

mkdir "%STAGING%\SharedDlls\x64\Debug\plugins" >nul 2>&1
mkdir "%STAGING%\SharedDlls\x64\Release\plugins" >nul 2>&1
mkdir "%BUILD_ROOT%" >nul 2>&1

echo.
echo Building and packaging Debug Shared assemblies...
for %%S in (
    AgentFlow.Contracts
    AgentFlow.Core
    BroadcastKit
    BroadcastKit.Contracts
    AgentFlow.Shared
    Hosting.Logging
) do (
    call :BuildShared "%%S" Debug Debug
    if errorlevel 1 goto :fail
)
call :VerifySharedAssemblies Debug
if errorlevel 1 goto :fail

echo.
echo Building and packaging Release Shared assemblies...
for %%S in (
    AgentFlow.Contracts
    AgentFlow.Core
    BroadcastKit
    BroadcastKit.Contracts
    AgentFlow.Shared
    Hosting.Logging
) do (
    call :BuildShared "%%S" Release Release
    if errorlevel 1 goto :fail
)
call :VerifySharedAssemblies Release
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

echo.
echo Validating binary version metadata...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\build\Verify-BinaryVersions.ps1" -Root "%STAGING%" -Output "%STAGING%\BinaryVersions.json"
if errorlevel 1 goto :fail
(
    echo AgentFlow Binary SDK
    echo.
    echo This archive does not contain public source projects.
    echo.
    echo Layout:
    echo   SharedDlls\x64\Debug          Debug shared assemblies, dependencies, and PDB files
    echo   SharedDlls\x64\Debug\plugins  Debug node plugin assemblies and PDB files
    echo   SharedDlls\x64\Release        Release shared assemblies and dependencies
    echo   SharedDlls\x64\Release\plugins Release node plugin assemblies
    echo.
    echo External projects should reference DLLs from the matching configuration:
    echo   Debug projects   -^> SharedDlls\x64\Debug
    echo   Release projects -^> SharedDlls\x64\Release
    echo.
    echo Keep plugins and SharedDlls from the same SDK archive. Do not mix Debug and Release.
    echo BinaryVersions.json contains the file and assembly versions of every AgentFlow-owned binary.
    echo Node.Broadcast.Demo is included only in the Debug plugin folder.
    echo Application/shared target framework: !AGENTFLOW_TARGET_FRAMEWORK!.
    echo Plugin target framework: !AGENTFLOW_PLUGIN_TARGET_FRAMEWORK!.
) > "%STAGING%\README.txt"

echo.
echo Creating zip...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%STAGING%' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 goto :fail

echo.
echo Package created:
echo   %ZIP%
exit /b 0

:BuildShared
set "PROJECT_NAME=%~1"
set "CONFIGURATION=%~2"
set "TARGET_VARIANT=%~3"
set "PROJECT=%ROOT%\%PROJECT_NAME%\%PROJECT_NAME%.csproj"
set "BUILD_OUT=%BUILD_ROOT%\Shared\%TARGET_VARIANT%\%PROJECT_NAME%"
set "SHARED_DEST=%STAGING%\SharedDlls\x64\%TARGET_VARIANT%"

if not exist "%PROJECT%" (
    echo ERROR: Shared project not found: %PROJECT%
    exit /b 1
)

echo   [%CONFIGURATION%] %PROJECT_NAME%
dotnet build "%PROJECT%" -c "%CONFIGURATION%" -p:Platform=x64 -p:CopyLocalLockFileAssemblies=true -o "%BUILD_OUT%" -v:minimal
if errorlevel 1 (
    echo ERROR: failed to build Shared project %PROJECT% in %CONFIGURATION%.
    exit /b 1
)

xcopy /Y /I "%BUILD_OUT%\*.dll" "%SHARED_DEST%\" >nul
if errorlevel 1 (
    echo ERROR: failed to copy Shared DLLs from %BUILD_OUT%.
    exit /b 1
)

if /I "%CONFIGURATION%"=="Debug" (
    xcopy /Y /I "%BUILD_OUT%\*.pdb" "%SHARED_DEST%\" >nul
    if errorlevel 1 (
        echo ERROR: failed to copy Shared PDB files from %BUILD_OUT%.
        exit /b 1
    )
)

exit /b 0

:VerifySharedAssemblies
set "TARGET_VARIANT=%~1"
set "SHARED_DEST=%STAGING%\SharedDlls\x64\%TARGET_VARIANT%"
for %%A in (
    AgentFlow.Contracts.dll
    AgentFlow.Core.dll
    BroadcastKit.dll
    BroadcastKit.Contracts.dll
    AgentFlow.Shared.dll
    Hosting.Logging.dll
) do (
    if not exist "%SHARED_DEST%\%%A" (
        echo ERROR: required Shared assembly is missing: %SHARED_DEST%\%%A
        exit /b 1
    )
)
exit /b 0

:BuildPlugin
set "PLUGIN_NAME=%~1"
set "CONFIGURATION=%~2"
set "TARGET_VARIANT=%~3"
set "PROJECT=%ROOT%\plugins\%PLUGIN_NAME%\%PLUGIN_NAME%.csproj"
set "BUILD_OUT=%BUILD_ROOT%\plugins\%TARGET_VARIANT%\%PLUGIN_NAME%"
set "PLUGIN_DEST=%STAGING%\SharedDlls\x64\%TARGET_VARIANT%\plugins"
set "SHARED_DEST=%STAGING%\SharedDlls\x64\%TARGET_VARIANT%"

echo   [%CONFIGURATION%] %PLUGIN_NAME%
dotnet build "%PROJECT%" -c "%CONFIGURATION%" -p:Platform=x64 -p:CopyLocalLockFileAssemblies=true -o "%BUILD_OUT%" -v:minimal
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

for %%F in ("%BUILD_OUT%\*.dll") do (
    if /I not "%%~nxF"=="%PLUGIN_NAME%.dll" (
        copy /Y "%%~fF" "%SHARED_DEST%\%%~nxF" >nul
        if errorlevel 1 (
            echo ERROR: failed to collect plugin dependency %%~nxF.
            exit /b 1
        )
    )
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
echo Binary SDK packaging failed.
exit /b 1


