# Deja dotnet, cargo y cargo-deny de .entorno en esta sesión de bash: source build/entorno.sh
ENTORNO="${SALA_ENTORNO:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/.entorno}"
export DOTNET_ROOT="$ENTORNO/dotnet"
export NUGET_PACKAGES="$ENTORNO/nuget"
export DOTNET_CLI_HOME="$ENTORNO"
export DOTNET_MULTILEVEL_LOOKUP=0
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export RUSTUP_HOME="$ENTORNO/rustup"
export CARGO_HOME="$ENTORNO/cargo"
export PATH="$DOTNET_ROOT:$CARGO_HOME/bin:$PATH"
