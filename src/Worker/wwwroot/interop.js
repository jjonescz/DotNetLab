export function getDotNetConfig() {
    return JSON.stringify(getDotnetRuntime(0).getConfig());
}
