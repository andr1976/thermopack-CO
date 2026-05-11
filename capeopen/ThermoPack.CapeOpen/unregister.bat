@echo off
setlocal

set HKCU=HKCU\Software\Classes

echo Removing ThermoPack per-user COM registration...

rem CLSIDs from source code
set MGR_CLSID={E7A1B2C3-0A00-4D5E-9F00-A00E30AC0000}
set PR_CLSID={E7A1B2C3-0A01-4D5E-9F00-A00E30AC0001}
set SRK_CLSID={E7A1B2C3-0A02-4D5E-9F00-A00E30AC0002}
set TCPR_CLSID={E7A1B2C3-0A03-4D5E-9F00-A00E30AC0003}
set CPA_CLSID={E7A1B2C3-0A04-4D5E-9F00-A00E30AC0004}
set GERG_CLSID={E7A1B2C3-0A05-4D5E-9F00-A00E30AC0005}
set PCSAFT_CLSID={E7A1B2C3-0A06-4D5E-9F00-A00E30AC0006}

rem Remove CLSID entries
for %%C in (%MGR_CLSID% %PR_CLSID% %SRK_CLSID% %TCPR_CLSID% %CPA_CLSID% %GERG_CLSID% %PCSAFT_CLSID%) do (
    reg delete "%HKCU%\CLSID\%%C" /f 2>nul && echo   Removed CLSID %%C
)

rem Remove ProgId entries
for %%P in (ThermoPack.PackageManager ThermoPack.PengRobinson ThermoPack.SRK ThermoPack.TcPR ThermoPack.CPA ThermoPack.GERG2008 ThermoPack.PCSAFT) do (
    reg delete "%HKCU%\%%P" /f 2>nul && echo   Removed ProgId %%P
)

echo.
echo Unregistration complete.
echo.

endlocal
