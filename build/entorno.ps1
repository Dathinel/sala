# Deja dotnet, cargo y cargo-deny de .entorno en esta sesión de PowerShell: . build\entorno.ps1
$Entorno = if ($env:SALA_ENTORNO) { $env:SALA_ENTORNO } else { Join-Path (Split-Path -Parent $PSScriptRoot) '.entorno' }
$S = [System.IO.Path]::PathSeparator
$env:DOTNET_ROOT = Join-Path $Entorno 'dotnet'
$env:NUGET_PACKAGES = Join-Path $Entorno 'nuget'
$env:DOTNET_CLI_HOME = $Entorno
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:RUSTUP_HOME = Join-Path $Entorno 'rustup'
$env:CARGO_HOME = Join-Path $Entorno 'cargo'
$env:PATH = $env:DOTNET_ROOT + $S + (Join-Path $env:CARGO_HOME 'bin') + $S + $env:PATH
