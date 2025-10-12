/**
 * @returns {WorkerSetup}
 */
export function createWorker(scriptUrl, messageHandler, errorHandler) {
    const worker = new Worker(scriptUrl, { type: 'module' });
    worker.addEventListener('message', (e) => { messageHandler(e.data ?? ''); });
    worker.addEventListener('error', (e) => { console.error(e); errorHandler(e.message ?? `${e.error ?? e}`); });
    worker.addEventListener('messageerror', () => { errorHandler('message error'); });

    // Setup a side channel for some communication outside our WorkerController in .NET.
    const sideChannel = new MessageChannel();
    sideChannel.port2.addEventListener('message', (e) => {
        if (e.data?.type === 'collect-gc-dump') {
            downloadCollectGcDumpResult('worker', e.data.result);
        } else {
            console.error('Unrecognized side message', ev);
        }
    });
    sideChannel.port2.start();

    return {
        worker,
        sideChannel,
    };
}

/**
 * @param {WorkerSetup} setup
 */
export function workerReady(setup) {
    setup.worker.postMessage(
        {
            type: 'init-ports',
            ports: {
                side: setup.sideChannel.port1,
            },
        },
        [setup.sideChannel.port1]
    );
}

/**
 * @param {WorkerSetup} setup
 * @param {string} message
 */
export function postMessage(setup, message) {
    setup.worker.postMessage(message);
}

/**
 * @param {WorkerSetup} setup
 * @param {string} message
 */
export function postSideMessage(setup, message) {
    setup.sideChannel.port2.postMessage(message);
}

/**
 * @param {WorkerSetup} setup
 */
export function disposeWorker(setup) {
    setup.worker.terminate();
    setup.sideChannel.port2.close();
}

export async function collectAndDownloadGcDump() {
    const result = await getDotnetRuntime(0).collectGcDump({ skipDownload: true });
    downloadCollectGcDumpResult('app', result);
}

/**
 * @param {string} prefix
 * @param {Uint8Array[]} result
 */
function downloadCollectGcDumpResult(prefix, result) {
    // Download the file.
    const blob = new Blob(result, { type: 'application/octet-stream' });
    const blobUrl = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.download = `${prefix}.trace.${(new Date()).valueOf()}.nettrace`;
    console.log(`Downloading trace ${link.download} - ${blob.size} bytes`);
    link.href = blobUrl;
    document.body.appendChild(link);
    link.dispatchEvent(new MouseEvent('click', {
        bubbles: true, cancelable: true, view: window,
    }));
}

/**
 * @typedef {Object} WorkerSetup
 * @property {Worker} worker
 * @property {MessageChannel} sideChannel
 */
