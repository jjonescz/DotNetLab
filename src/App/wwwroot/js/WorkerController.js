export function createWorker(scriptUrl, messageHandler, errorHandler) {
    const worker = new Worker(scriptUrl, { type: 'module' });
    worker.addEventListener('message', (e) => { messageHandler(e.data); });
    worker.addEventListener('error', (e) => { console.error(e); errorHandler(e.message ?? `${e.error ?? e}`); });
    worker.addEventListener('messageerror', () => { errorHandler('message error'); });
    return worker;
}

/**
 * @param {Worker} worker
 * @param {string} message
 */
export function postMessage(worker, message) {
    worker.postMessage(message);
}

/**
 * @param {Worker} worker
 */
export function disposeWorker(worker) {
    worker.terminate();
}
