#!/bin/sh
set -eu
curl -sSL https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh > dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --jsonfile global.json --install-dir ./dotnet
./dotnet/dotnet --version
./dotnet/dotnet workload install wasm-tools wasm-experimental
./dotnet/dotnet build src/Worker
./dotnet/dotnet publish -p:CompressionEnabled=false -o output src/App
cp output/wwwroot/_framework/icudt_*.dat output/wwwroot/worker/wwwroot/_framework/
