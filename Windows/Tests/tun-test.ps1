# VPN Nova for Windows: real TUN check (needs an administrator PowerShell). Does not touch the
# installed service: runs its own sing-box with a Nova-generated TUN config next to a local
# Shadowsocks "server", then checks routing, the app rule, the kill switch and cleanup.
#   powershell -ExecutionPolicy Bypass -File .\tun-test.ps1
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$sb = Resolve-Path "$dir\..\sing-box.exe"
$work = Join-Path $env:TEMP "nova-tun-test"
$pass = 0; $fail = 0
function Check($ok, $name, $detail = "") {
    if ($ok) { $script:pass++; Write-Host "  ✓ $name" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  ✗ $name $detail" -ForegroundColor Red }
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Write-Host "Нужен PowerShell от имени администратора." -ForegroundColor Yellow; exit 2 }
if (Get-Service NovaVPN -ErrorAction SilentlyContinue | Where-Object Status -eq Running) {
    Write-Host "Выключи VPN в приложении перед тестом (служба может работать, ядро — нет)." -ForegroundColor Yellow
}

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $work | Out-Null
$ssPort = 18399; $clash = 19393; $secret = "nova-tun-test-secret-0123456789"

# Local Shadowsocks server, logging what arrives.
@"
{"log":{"level":"info"},"inbounds":[{"type":"shadowsocks","tag":"ss-in","listen":"127.0.0.1","listen_port":$ssPort,"method":"aes-256-gcm","password":"nova-tun-test"}],"outbounds":[{"type":"direct"}]}
"@ | Set-Content "$work\server.json" -Encoding ascii
$server = Start-Process $sb -ArgumentList "run","-c","`"$work\server.json`"","-D","`"$work`"" -RedirectStandardError "$work\server.log" -RedirectStandardOutput "$work\server.out" -PassThru -WindowStyle Hidden

# Nova's TUN config: curl.exe → local SS (app rule), example.net → dead server (kill switch), the rest direct.
& "$dir\Nova.SelfTest.exe" $sb --emit-tun "$work\tun.json" $ssPort $clash $secret
Write-Host "`nTUN: маршрутизация"
$core = Start-Process $sb -ArgumentList "run","-c","`"$work\tun.json`"","-D","`"$work`"" -RedirectStandardError "$work\tun.log" -RedirectStandardOutput "$work\tun.out" -PassThru -WindowStyle Hidden
Start-Sleep 4
try {
    $adapter = Get-NetAdapter -Name "Nova" -ErrorAction SilentlyContinue
    Check ($null -ne $adapter -and $adapter.Status -eq "Up") "адаптер Wintun «Nova» поднят"
    $api = @{ Authorization = "Bearer $secret" }
    $ok = $false; try { Invoke-RestMethod "http://127.0.0.1:$clash/version" -Headers $api | Out-Null; $ok = $true } catch {}
    Check $ok "Clash API отвечает"

    # Direct traffic from PowerShell (not curl.exe) works through the TUN.
    $ok = $false; try { Invoke-WebRequest "https://www.example.org" -UseBasicParsing -TimeoutSec 10 | Out-Null; $ok = $true } catch {}
    Check $ok "всё остальное → direct (PowerShell)"

    # curl.exe is routed to the SS server by the app rule.
    & "$env:SystemRoot\System32\curl.exe" -s -o NUL -m 10 https://www.wikipedia.org
    Start-Sleep 1
    $log = Get-Content "$work\server.log","$work\server.out" -Raw -ErrorAction SilentlyContinue
    Check ($log -match "wikipedia") "правило приложения: curl.exe → сервер (видно в логе сервера)"
    $conns = Invoke-RestMethod "http://127.0.0.1:$clash/connections" -Headers $api
    Check ($conns.connections.Count -ge 0) "ядро видит процессы соединений"

    # example.net goes to the dead server: blocked, nothing leaks directly.
    $ok = $false; try { Invoke-WebRequest "https://example.net" -UseBasicParsing -TimeoutSec 6 | Out-Null; $ok = $true } catch {}
    Check (-not $ok) "kill switch: домен на мёртвом сервере заблокирован"

    # DNS goes through the core (FakeIP for proxied names).
    $ip = (Resolve-DnsName example.net -Type A -DnsOnly -ErrorAction SilentlyContinue | Where-Object IPAddress | Select-Object -First 1).IPAddress
    Check ($ip -like "198.19.*") "DNS: домен через сервер получает FakeIP ($ip)"
} finally {
    Stop-Process -Id $core.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep 3
Check ($null -eq (Get-NetAdapter -Name "Nova" -ErrorAction SilentlyContinue)) "после остановки адаптер «Nova» убран"
$ok = $false; try { Invoke-WebRequest "https://www.example.org" -UseBasicParsing -TimeoutSec 10 | Out-Null; $ok = $true } catch {}
Check $ok "после остановки интернет работает как раньше"

Write-Host "`nИтого: ✓ $pass  ✗ $fail   (логи: $work)"
exit $(if ($fail -eq 0) { 0 } else { 1 })
