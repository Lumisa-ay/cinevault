param([switch]$EnableBt)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $compose = Get-Command docker-compose -ErrorAction SilentlyContinue
    $desktopCompose = Join-Path $env:ProgramFiles 'Docker/Docker/resources/cli-plugins/docker-compose.exe'
    if ($compose) { $composePath = $compose.Source }
    elseif (Test-Path -LiteralPath $desktopCompose) { $composePath = $desktopCompose }
    else { throw 'Docker Compose was not found. Install Docker Desktop with Compose first.' }
    if ($EnableBt) { & $composePath -f compose.yaml --profile bt up -d --build web qbittorrent }
    else { & $composePath -f compose.yaml up -d --build --no-deps web }
    if ($LASTEXITCODE -ne 0) { throw 'Docker update failed. Check that Docker Desktop is running and this terminal has Docker access.' }
    & $composePath -f compose.yaml ps
    if ($LASTEXITCODE -ne 0) { throw 'Could not read Docker service state.' }
    Write-Host 'Open http://localhost:5188/#sources and sign in with your existing account.'
    if ($EnableBt) { Write-Host 'Configure qBittorrent at http://localhost:8081, then save its credentials at http://localhost:5188/admin/sources.' }
}
finally { Pop-Location }
