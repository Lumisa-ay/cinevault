$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$secretDirectory = Join-Path $projectDirectory '.secrets'
$secretPath = Join-Path $secretDirectory 'admin-password.txt'
New-Item -ItemType Directory -Path $secretDirectory -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $projectDirectory 'downloads') -Force | Out-Null
if (-not (Test-Path -LiteralPath $secretPath)) {
    $randomBytes = New-Object byte[] 24
    $randomSource = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $randomSource.GetBytes($randomBytes) } finally { $randomSource.Dispose() }
    $initialPassword = [Convert]::ToBase64String($randomBytes)
    [System.IO.File]::WriteAllText($secretPath, $initialPassword, [System.Text.UTF8Encoding]::new($false))
}
if (-not (Test-Path -LiteralPath (Join-Path $projectDirectory '.env'))) {
    Copy-Item -LiteralPath (Join-Path $projectDirectory '.env.example') -Destination (Join-Path $projectDirectory '.env')
}
Write-Output 'Local Docker configuration is ready.'
Write-Output 'Account: admin'
Write-Output ('Initial password file: ' + $secretPath)
