// Avoid hot reload crashing due some browser APIs missing in web workers.
globalThis.document = {
    baseUri: '',
    querySelector: () => null,
};
globalThis.window = globalThis;

/** @type {import('./dotnet').ModuleAPI} */
import { dotnet as dn } from '../../_framework/dotnet.js';
import * as interop from './interop.js';

// Extract arguments from URL of the script.
const args = [...new URLSearchParams(self.location.search).entries()].filter(([k, _]) => k === 'arg').map(([_, v]) => v);

/** @type {import('./dotnet').DotnetHostBuilder} */
const dotnet = dn.withExitOnUnhandledError();

const instance = await dotnet
    .withApplicationArguments(...args)
    .create();

instance.setModuleImports('worker-imports.js', {
    registerOnMessage: (handler) => self.addEventListener('message', (ev) => {
        if (ev.data?.type === 'init-ports') {
            /** @type {Record<string, MessagePort>} */
            const { side } = ev.data.ports;
            side.onmessage = (ev) => {
                if (ev.data === 'collect-gc-dump') {
                    handleCollectGcDump(side);
                } else {
                    console.error('Unrecognized side message', ev);
                }
            };
            side.start();
        } else {
            handler(ev.data ?? '');
        }
    }),
    postMessage: (message) => self.postMessage(message),
});

instance.setModuleImports('worker-interop.js', interop);

await instance.runMainAndExit('DotNetLab.Worker.wasm');

/**
 * @param {MessagePort} port
 */
async function handleCollectGcDump(port) {
    const result = await instance.collectGcDump({ skipDownload: true });
    port.postMessage({ type: 'collect-gc-dump', result });
}
