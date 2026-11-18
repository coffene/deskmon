@echo off
title Allow PC Resource Monitor in Windows Firewall
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator privileges to update Windows Firewall rules...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b 0
)

rem Remove any Windows Firewall Block rules for python.exe or PCMonitorServer.exe
netsh advfirewall firewall delete rule name="python.exe" >nul 2>&1
netsh advfirewall firewall delete rule name="pcmonitorserver.exe" >nul 2>&1
netsh advfirewall firewall delete rule name="PC Resource Monitor TCP" >nul 2>&1
netsh advfirewall firewall delete rule name="PC Resource Monitor UDP" >nul 2>&1
netsh advfirewall firewall delete rule name="PC Resource Monitor App" >nul 2>&1

netsh advfirewall firewall add rule name="PC Resource Monitor TCP" dir=in action=allow protocol=TCP localport=8765 profile=any
netsh advfirewall firewall add rule name="PC Resource Monitor UDP" dir=in action=allow protocol=UDP localport=8766 profile=any
if exist "%~dp0PCMonitorServer.exe" netsh advfirewall firewall add rule name="PC Resource Monitor App" dir=in action=allow program="%~dp0PCMonitorServer.exe" profile=any

echo.
echo Done. Removed python.exe block rules and allowed ports 8765 (TCP) and 8766 (UDP).
pause
