@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "ACTION=build"
set "CONFIGURATION=Release"

if /I "%~1"=="publish" set "ACTION=publish"
if /I "%~1"=="Debug" set "CONFIGURATION=Debug"
if /I "%~2"=="Debug" set "CONFIGURATION=Debug"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet was not found. Install the .NET 9 SDK first.
    exit /b 1
)

echo.
echo ============================================================
echo   DataRecovery - %ACTION% / %CONFIGURATION%
echo ============================================================
echo.

echo [1/3] Restoring NuGet packages...
dotnet restore DataRecovery.slnx
if errorlevel 1 goto :failed

echo.
echo [2/3] Building solution...
dotnet build DataRecovery.slnx -c %CONFIGURATION% --no-restore
if errorlevel 1 goto :failed

echo.
echo [3/3] Running tests...
dotnet test DataRecovery.slnx -c %CONFIGURATION% --no-build
if errorlevel 1 goto :failed

if /I "%ACTION%"=="publish" (
    echo.
    echo [Publish] Creating self-contained Windows x64 package...
    dotnet publish src\DataRecovery.App\DataRecovery.App.csproj ^
        -c %CONFIGURATION% -r win-x64 --self-contained true ^
        -o artifacts\publish\win-x64
    if errorlevel 1 goto :failed
    echo Output: %CD%\artifacts\publish\win-x64
)

echo.
echo ============================================================
echo   SUCCESS - build and tests passed
echo ============================================================
exit /b 0

:failed
echo.
echo ============================================================
echo   FAILED - see errors above
echo ============================================================
exit /b 1
