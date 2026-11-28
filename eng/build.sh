#!/usr/bin/env bash
set -euo pipefail
curl -fsSL https://aka.ms/dotnetup/get-dotnetup.sh | bash -s -- --install-dir "$HOME/.dotnetup"
export PATH="$HOME/.dotnetup:$PATH"
dotnetup sdk install
source <(dotnetup print-env-script) || true
dotnet --version
dotnet workload install wasm-tools wasm-experimental
# -noAutoResponse omits Directory.Build.rsp's -mt to work around dotnet/packs/Microsoft.NET.Runtime.WebAssembly.Sdk/11.0.0-preview.7.26381.103/Sdk/WasmApp.Common.targets(408,5): error : Custom TaskFactory 'JsonToItemsTaskFactory' for Task 'ReadWasmProps' does not support out of process TaskHost execution. Turn off the multithreaded build mode or remove the custom TaskFactory from your <UsingTask> definitions in project files. [src/WebAssembly/WebAssembly.csproj]
dotnet publish -o output src/WebAssembly -noAutoResponse
find output/wwwroot/_framework -type f -name '*.wasm' -delete
dotnet run --file eng/check-publish-output.cs -- output
