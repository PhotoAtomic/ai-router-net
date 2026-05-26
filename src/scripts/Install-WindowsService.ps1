[CmdletBinding()]
param(
    [string]$ServiceName = "AiRouter",
    [string]$DisplayName = "AI Router",
    [string]$PublishDir = (Join-Path $PSScriptRoot "..\bin\Release\net10.0\win-x64\publish"),
    [string]$Arguments = "",
    [ValidateSet("auto", "demand", "disabled")]
    [string]$StartupType = "auto",
    [switch]$Start
)

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Eseguire questo script da una console PowerShell avviata come amministratore."
}

$exePath = Join-Path $PublishDir "AiRouter.exe"
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Eseguibile non trovato: $exePath. Prima eseguire: dotnet publish -c Release -r win-x64 --self-contained true"
}

$configPath = Join-Path $PublishDir "appsettings.json"
if (-not (Test-Path -LiteralPath $configPath)) {
    throw "Configurazione non trovata: $configPath. Copiare appsettings.json nella cartella di publish."
}

$installArgs = @(
    "--install-service",
    "--service-name", $ServiceName,
    "--display-name", $DisplayName,
    "--startup", $StartupType
)

if (-not [string]::IsNullOrWhiteSpace($Arguments)) {
    $installArgs += @("--service-args", $Arguments)
}

if ($Start) {
    $installArgs += "--start"
}

& $exePath @installArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
