# Instala en .entorno\ el SDK de .NET, la cadena de Rust y cargo-deny, con versiones y huellas fijadas y sin tocar nada global.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File build\preparar-entorno.ps1 [-Forzar]
#      pwsh -NoProfile -File build/preparar-entorno.ps1      (PowerShell 7 en Linux o macOS)
# Después: build\entorno.ps1 (PowerShell) o build/entorno.sh (bash) dejan dotnet y cargo del entorno en la sesión.
#
# Mismo patrón que Rodaje\build\preparar-entorno.ps1: descarga a .part, verifica la huella, descomprime en una carpeta
# temporal con la marca y la renombra, así un apagón no deja nada a medias.

param([switch]$Forzar)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ($env:OS -eq 'Windows_NT') { $Rid = 'win-x64' } else { $Rid = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier }
if ($Rid -notin @('win-x64', 'linux-x64', 'osx-arm64')) { Write-Host "Sistema no previsto ($Rid): solo win-x64, linux-x64 y osx-arm64."; exit 1 }

# SDK de .NET 10.0.403: SHA-512 de https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json.
$SdkVersion = '10.0.403'
$Sdk = @{
    'win-x64'   = @{ Paquete = "dotnet-sdk-$SdkVersion-win-x64.zip"; Exe = 'dotnet.exe'
                     Hash = '7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48' }
    'linux-x64' = @{ Paquete = "dotnet-sdk-$SdkVersion-linux-x64.tar.gz"; Exe = 'dotnet'
                     Hash = 'b7b3d8461cf98b07d2552ce9199702fa3ad010ac2671a5f8cd5136f7534e50f6855ba7bbb02b11494563f1d04156276a31cda1103b900e5eecd0d9473dc7b9c5' }
    'osx-arm64' = @{ Paquete = "dotnet-sdk-$SdkVersion-osx-arm64.tar.gz"; Exe = 'dotnet'
                     Hash = '5893c66b48ea3674c7da18501059d606e96f3672fabe4d5e41762e692c7c83df3ee0b2b9dc5a0786191da2dc413d7335158101c14c935ab217f31ac2529770f3' }
}[$Rid]

# rustup 1.29.3 (SHA-256 de https://static.rust-lang.org/rustup/archive/1.29.3/<triple>/rustup-init[.exe].sha256).
# rustup comprueba después cada componente de la cadena con las huellas de su manifiesto.
# La versión de Rust la fija rust-toolchain.toml; este script la lee de ahí.
$RustupVersion = '1.29.3'
$Rustup = @{
    'win-x64'   = @{ Triple = 'x86_64-pc-windows-msvc'; Exe = 'rustup-init.exe'; Hash = '38027d51d69a5d05493627a0634dbea52d6cd793c8f072d2373d3f4163eec7db' }
    'linux-x64' = @{ Triple = 'x86_64-unknown-linux-gnu'; Exe = 'rustup-init'; Hash = 'b1c8cb14e30889e7e2999e6b69e62d9e9b9ffc0f4df8180564d306d9680e6c55' }
    'osx-arm64' = @{ Triple = 'aarch64-apple-darwin'; Exe = 'rustup-init'; Hash = '63752a1919a3a4793173ec70f8f212621a7ce38ba6a246095984482f4de20f9f' }
}[$Rid]

# cargo-deny 0.20.2 (EmbarkStudios/cargo-deny, MIT o Apache-2.0), binario de la release con su SHA-256.
$DenyVersion = '0.20.2'
$Deny = @{
    'win-x64'   = @{ Triple = 'x86_64-pc-windows-msvc'; Hash = '975a22143262fd27476d19ee00c7af67978426e40e1dee94eed6bbade1cf87dc' }
    'linux-x64' = @{ Triple = 'x86_64-unknown-linux-musl'; Hash = '9f12ed4c49936e09b48bf862b595cde2fe64fcbd9d74dfacac6131ca824c8d5f' }
    'osx-arm64' = @{ Triple = 'aarch64-apple-darwin'; Hash = 'fe67d82a10d8597a3549364cb733a3f9cc1bfff9031b7ae46384a9f2a72090c3' }
}[$Rid]

function Paso([string]$t) { Write-Host "[entorno] $t" }

$Raiz = Split-Path -Parent $PSScriptRoot
$Entorno = if ($env:SALA_ENTORNO) { $env:SALA_ENTORNO } else { Join-Path $Raiz '.entorno' }
$Descargas = Join-Path $Entorno 'descargas'
New-Item -ItemType Directory -Force -Path $Descargas | Out-Null

$RustVersion = (Select-String -LiteralPath (Join-Path $Raiz 'rust-toolchain.toml') -Pattern '^channel\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
$GlobalSdk = (Get-Content -LiteralPath (Join-Path $Raiz 'global.json') -Raw | ConvertFrom-Json).sdk.version
if ($GlobalSdk -ne $SdkVersion) { Write-Host "global.json pide el SDK $GlobalSdk y este script instala el ${SdkVersion}: cambia los dos a la vez."; exit 1 }

# Baja $Url a descargas\$Nombre (vía .part) y comprueba la huella; devuelve la ruta.
function Bajar([string]$Url, [string]$Nombre, [string]$Hash, [string]$Algoritmo) {
    $archivo = Join-Path $Descargas $Nombre
    if (-not (Test-Path -LiteralPath $archivo)) {
        Paso "Descargando $Nombre..."
        Invoke-WebRequest -Uri $Url -OutFile "$archivo.part" -UseBasicParsing
        Move-Item -LiteralPath "$archivo.part" -Destination $archivo -Force
    }
    $real = (Get-FileHash -LiteralPath $archivo -Algorithm $Algoritmo).Hash.ToLowerInvariant()
    if ($real -ne $Hash) {
        Remove-Item -LiteralPath $archivo -Force
        Write-Host "La huella de $Nombre no coincide (se borró). Si se repite, el archivo publicado cambió: actualiza el hash."
        exit 1
    }
    return $archivo
}

# Descomprime $Archivo en una carpeta temporal, escribe la marca y la renombra a $Destino.
function Descomprimir([string]$Archivo, [string]$Destino, [string]$Marca) {
    $tmp = "$Destino.tmp-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    if ($Archivo -like '*.zip') {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($Archivo, $tmp)
    } else {
        # Rutas relativas: el tar de GNU (el de Git en Windows) leería «D:\...» como un archivo remoto.
        Push-Location $Entorno
        try { tar -xzf ('descargas/' + (Split-Path -Leaf $Archivo)) -C (Split-Path -Leaf $tmp) }
        finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) { throw "tar devolvió $LASTEXITCODE" }
    }
    [System.IO.File]::WriteAllText((Join-Path $tmp $Marca), '')
    if (Test-Path -LiteralPath $Destino) { Remove-Item -LiteralPath $Destino -Recurse -Force }
    [System.IO.Directory]::Move($tmp, $Destino)
}

Get-ChildItem -LiteralPath $Entorno -Directory | Where-Object { $_.Name -like '*.tmp-*' } | Remove-Item -Recurse -Force

# 1. SDK de .NET
$Dotnet = Join-Path $Entorno 'dotnet'
if ($Forzar -or -not (Test-Path -LiteralPath (Join-Path $Dotnet ".sdk-$SdkVersion"))) {
    $a = Bajar "https://builds.dotnet.microsoft.com/dotnet/Sdk/$SdkVersion/$($Sdk.Paquete)" $Sdk.Paquete $Sdk.Hash 'SHA512'
    Descomprimir $a $Dotnet ".sdk-$SdkVersion"
    Remove-Item -LiteralPath $a -Force
}
Paso "SDK de .NET $SdkVersion en .entorno/dotnet"
New-Item -ItemType Directory -Force -Path (Join-Path $Entorno 'nuget') | Out-Null

# 2. Rust: rustup y la cadena de rust-toolchain.toml, con RUSTUP_HOME y CARGO_HOME dentro de .entorno
$env:RUSTUP_HOME = Join-Path $Entorno 'rustup'
$env:CARGO_HOME = Join-Path $Entorno 'cargo'
$CargoBin = Join-Path $env:CARGO_HOME 'bin'
$MarcaRust = Join-Path $env:RUSTUP_HOME ".rust-$RustVersion"
if ($Forzar -or -not (Test-Path -LiteralPath $MarcaRust)) {
    $init = Bajar "https://static.rust-lang.org/rustup/archive/$RustupVersion/$($Rustup.Triple)/$($Rustup.Exe)" $Rustup.Exe $Rustup.Hash 'SHA256'
    if ($Rid -ne 'win-x64') { chmod +x $init }
    Paso "Instalando Rust $RustVersion (perfil mínimo, con clippy y rustfmt)..."
    & $init -y --no-modify-path --no-update-default-toolchain --profile minimal --default-toolchain $RustVersion -c clippy -c rustfmt
    if ($LASTEXITCODE -ne 0) { Write-Host "rustup-init falló ($LASTEXITCODE)."; exit 1 }
    [System.IO.File]::WriteAllText($MarcaRust, '')
}
Paso "Rust $RustVersion en .entorno/rustup y .entorno/cargo"

# 3. cargo-deny
$DenyExe = Join-Path $CargoBin $(if ($Rid -eq 'win-x64') { 'cargo-deny.exe' } else { 'cargo-deny' })
$MarcaDeny = Join-Path $CargoBin ".cargo-deny-$DenyVersion"
if ($Forzar -or -not (Test-Path -LiteralPath $MarcaDeny)) {
    $nombre = "cargo-deny-$DenyVersion-$($Deny.Triple).tar.gz"
    $a = Bajar "https://github.com/EmbarkStudios/cargo-deny/releases/download/$DenyVersion/$nombre" $nombre $Deny.Hash 'SHA256'
    $tmp = Join-Path $Entorno 'deny.tmp-x'
    Descomprimir $a $tmp '.x'
    $exe = Get-ChildItem -LiteralPath $tmp -Recurse -File | Where-Object { $_.Name -in @('cargo-deny', 'cargo-deny.exe') } | Select-Object -First 1
    Copy-Item -LiteralPath $exe.FullName -Destination $DenyExe -Force
    Remove-Item -LiteralPath $tmp -Recurse -Force
    Remove-Item -LiteralPath $a -Force
    [System.IO.File]::WriteAllText($MarcaDeny, '')
}
Paso "cargo-deny $DenyVersion en .entorno/cargo/bin"
Paso "Entorno listo."
