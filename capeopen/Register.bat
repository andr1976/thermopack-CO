@echo off
echo ThermoPack CAPE-OPEN Registration
echo ==================================

set REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe

if not exist "%REGASM%" (
    echo ERROR: RegAsm.exe not found at %REGASM%
    echo Ensure .NET Framework 4.8 x64 is installed.
    pause
    exit /b 1
)

echo Registering ThermoPack.CapeOpen.dll...
"%REGASM%" /codebase ThermoPack.CapeOpen.dll

if %ERRORLEVEL% neq 0 (
    echo ERROR: Registration failed.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Registration successful.
echo ThermoPack property packages should now appear in COFE.
pause
