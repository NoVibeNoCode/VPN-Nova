# VPN Nova for Windows: the automated self-test on this PC (no administrator rights needed).
# Links, config generator, ConfigGuard, fuzzing, live routing, 6 protocols end-to-end — against the
# shipped sing-box.exe. Run from the "VPN Nova\Tests" folder:
#   powershell -ExecutionPolicy Bypass -File .\selftest.ps1
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
& "$dir\Nova.SelfTest.exe" "$dir\..\sing-box.exe" @args
exit $LASTEXITCODE
