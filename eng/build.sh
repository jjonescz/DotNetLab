#!/bin/sh
set -eu
curl -sSL https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh > dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --jsonfile global.json --install-dir ./dotnet
./dotnet/dotnet --version
./dotnet/dotnet workload install wasm-tools wasm-experimental
./dotnet/dotnet publish -o output src/App
