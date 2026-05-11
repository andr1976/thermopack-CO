@echo off
setlocal enabledelayedexpansion

set FWDIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set DLL=%~dp0bin\Release\net48\ThermoPack.CapeOpen.dll
set ASMNAME=ThermoPack.CapeOpen, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
set RUNTIME=v4.0.30319
set HKCU=HKCU\Software\Classes

rem CLSIDs from source code [Guid(...)] attributes
set MGR_CLSID={E7A1B2C3-0A00-4D5E-9F00-A00E30AC0000}
set PR_CLSID={E7A1B2C3-0A01-4D5E-9F00-A00E30AC0001}
set SRK_CLSID={E7A1B2C3-0A02-4D5E-9F00-A00E30AC0002}
set TCPR_CLSID={E7A1B2C3-0A03-4D5E-9F00-A00E30AC0003}
set CPA_CLSID={E7A1B2C3-0A04-4D5E-9F00-A00E30AC0004}
set GERG_CLSID={E7A1B2C3-0A05-4D5E-9F00-A00E30AC0005}
set PCSAFT_CLSID={E7A1B2C3-0A06-4D5E-9F00-A00E30AC0006}

rem CAPE-OPEN 1.1 category IDs
set CAT_PPM={CF51E383-0110-4ed8-ACB7-B50CFDE6908E}
set CAT_PP={CF51E384-0110-4ed8-ACB7-B50CFDE6908E}

if not exist "%DLL%" (
    echo ERROR: DLL not found at %DLL%
    echo Build the project in Release mode first:
    echo   dotnet build -c Release
    exit /b 1
)

rem Convert DLL path to file:/// URI (required by .NET COM activation)
set DLLURI=file:///%DLL:\=/%

echo.
echo Registering ThermoPack CAPE-OPEN packages (per-user)...
echo   DLL: %DLL%
echo   URI: %DLLURI%

rem === Helper: register a single class ===
rem Usage: call :regclass CLSID ProgId ClassName CapeName CapeDesc Category

call :regclass "%MGR_CLSID%" "ThermoPack.PackageManager" "ThermoPack.CapeOpen.ThermoPackPackageManager" "ThermoPack" "ThermoPack property package manager" "%CAT_PPM%"
call :regclass "%PR_CLSID%" "ThermoPack.PengRobinson" "ThermoPack.CapeOpen.PengRobinsonPropertyPackage" "ThermoPack Peng-Robinson" "Peng-Robinson cubic equation of state (thermopack)" "%CAT_PP%"
call :regclass "%SRK_CLSID%" "ThermoPack.SRK" "ThermoPack.CapeOpen.SrkPropertyPackage" "ThermoPack SRK" "Soave-Redlich-Kwong cubic equation of state (thermopack)" "%CAT_PP%"
call :regclass "%TCPR_CLSID%" "ThermoPack.TcPR" "ThermoPack.CapeOpen.TcPrPropertyPackage" "ThermoPack tcPR" "Translated-consistent Peng-Robinson (thermopack)" "%CAT_PP%"
call :regclass "%CPA_CLSID%" "ThermoPack.CPA" "ThermoPack.CapeOpen.CpaPropertyPackage" "ThermoPack CPA" "Cubic-Plus-Association equation of state (thermopack)" "%CAT_PP%"
call :regclass "%GERG_CLSID%" "ThermoPack.GERG2008" "ThermoPack.CapeOpen.Gerg2008PropertyPackage" "ThermoPack GERG-2008" "GERG-2008 multiparameter equation of state (thermopack)" "%CAT_PP%"
call :regclass "%PCSAFT_CLSID%" "ThermoPack.PCSAFT" "ThermoPack.CapeOpen.PcSaftPropertyPackage" "ThermoPack PC-SAFT" "PC-SAFT equation of state (thermopack)" "%CAT_PP%"

echo.
echo Registration complete (per-user, no admin required).
echo Restart COCO/COFE to discover the ThermoPack packages.
echo.

endlocal
exit /b 0

:regclass
set _CLSID=%~1
set _PROGID=%~2
set _CLASS=%~3
set _CAPENAME=%~4
set _CAPEDESC=%~5
set _CATEGORY=%~6

echo   Registering %_PROGID% ...
reg add "%HKCU%\CLSID\%_CLSID%" /ve /d "%_PROGID%" /f >nul
reg add "%HKCU%\CLSID\%_CLSID%\InprocServer32" /ve /d "mscoree.dll" /f >nul
reg add "%HKCU%\CLSID\%_CLSID%\InprocServer32" /v "ThreadingModel" /d "Both" /f >nul
reg add "%HKCU%\CLSID\%_CLSID%\InprocServer32" /v "Class" /d "%_CLASS%" /f >nul
reg add "%HKCU%\CLSID\%_CLSID%\InprocServer32" /v "Assembly" /d "%ASMNAME%" /f >nul
reg add "%HKCU%\CLSID\%_CLSID%\InprocServer32" /v "RuntimeVersion" /d "%RUNTIME%" /f >nul
reg add "%HKCU%\CLSID\%_CLSID%\InprocServer32" /v "CodeBase" /d "!DLLURI!" /f >nul
reg add "%HKCU%\CLSID\%_CLSID%\InprocServer32\1.0.0.0" /v "Class" /d "%_CLASS%" /f >nul
reg add "%HKCU%\CLSID\%_CLSID%\InprocServer32\1.0.0.0" /v "Assembly" /d "%ASMNAME%" /f >nul
reg add "%HKCU%\CLSID\%_CLSID%\InprocServer32\1.0.0.0" /v "RuntimeVersion" /d "%RUNTIME%" /f >nul
reg add "%HKCU%\CLSID\%_CLSID%\InprocServer32\1.0.0.0" /v "CodeBase" /d "!DLLURI!" /f >nul
reg add "%HKCU%\CLSID\%_CLSID%\ProgId" /ve /d "%_PROGID%" /f >nul
reg add "%HKCU%\CLSID\%_CLSID%\Implemented Categories\%_CATEGORY%" /f >nul
reg add "%HKCU%\CLSID\%_CLSID%\CapeDescription" /v Name /d "%_CAPENAME%" /f >nul
reg add "%HKCU%\CLSID\%_CLSID%\CapeDescription" /v Description /d "%_CAPEDESC%" /f >nul
reg add "%HKCU%\CLSID\%_CLSID%\CapeDescription" /v CapeVersion /d "1.1" /f >nul
reg add "%HKCU%\%_PROGID%\CLSID" /ve /d "%_CLSID%" /f >nul

exit /b 0
