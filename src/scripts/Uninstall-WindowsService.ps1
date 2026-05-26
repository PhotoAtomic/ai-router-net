[CmdletBinding()]
param(
    [string]$ServiceName = "AiRouter",
    [string]$PublishDir = (Join-Path $PSScriptRoot "..\bin\Release\net10.0\win-x64\publish")
)

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Eseguire questo script da una console PowerShell avviata come amministratore."
}

$exePath = Join-Path $PublishDir "AiRouter.exe"
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Eseguibile non trovato: $exePath."
}

& $exePath --uninstall-service --service-name $ServiceName
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
