(function () {
    const external = window.external || {};

    if (typeof external.receiveMessage === 'function' && typeof external.sendMessage === 'function') {
        return;
    }

    let nativePort = null;
    const messageHandlers = [];
    const queuedMessages = [];

    function dispatchMessage(message) {
        for (const handler of messageHandlers) {
            handler(message);
        }
    }

    function flushMessages() {
        if (!nativePort) {
            return;
        }

        while (queuedMessages.length > 0) {
            nativePort.postMessage(queuedMessages.shift());
        }
    }

    function attachPort(port) {
        nativePort = port;
        nativePort.onmessage = event => dispatchMessage(event.data);
        nativePort.start?.();
        flushMessages();
    }

    external.receiveMessage = callback => {
        messageHandlers.push(callback);
    };

    external.sendMessage = message => {
        if (nativePort) {
            nativePort.postMessage(message);
        } else {
            queuedMessages.push(message);
        }
    };

    window.external = external;

    window.addEventListener('message', event => {
        if (event.data === 'capturePort' && event.ports.length > 0) {
            attachPort(event.ports[0]);
        } else if (typeof event.data === 'string') {
            dispatchMessage(event.data);
        }
    });
}());