@echo off
REM ============================================================================
REM  ITVCoffin - start the local capture proxy (relative paths only).
REM  All arguments are forwarded to the proxy (e.g. --tcp-port 35313).
REM ============================================================================
setlocal
cd /d "%~dp0"

if exist "proxy\publish\ITVCoffin.Proxy.exe" (
  echo [run_capture] using published proxy
  "proxy\publish\ITVCoffin.Proxy.exe" %*
  goto :eof
)

if exist "proxy\bin\Release\net9.0\ITVCoffin.Proxy.exe" (
  echo [run_capture] using Release build
  "proxy\bin\Release\net9.0\ITVCoffin.Proxy.exe" %*
  goto :eof
)

echo [run_capture] no prebuilt proxy found - building and running via dotnet...
dotnet run --project "proxy\ITVCoffin.Proxy.csproj" -c Release -- %*
