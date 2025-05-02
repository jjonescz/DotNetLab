/** @type {import('./dotnet').ModuleAPI} */
import { dotnet as dn } from '../../_framework/dotnet.js';

// Extract arguments from URL of the script.
const args = [...new URLSearchParams(self.location.search).entries()].filter(([k, _]) => k === 'arg').map(([_, v]) => v);

/** @type {import('./dotnet').DotnetHostBuilder} */
const dotnet = dn.withExitOnUnhandledError();

const instance = await dotnet
    .withApplicationArguments(...args)
    .create();

instance.setModuleImports('worker-imports.js', {
    registerOnMessage: (handler) => self.addEventListener('message', handler),
    postMessage: (message) => self.postMessage(message),
});

await instance.runMainAndExit('DotNetLab.Worker.wasm');
