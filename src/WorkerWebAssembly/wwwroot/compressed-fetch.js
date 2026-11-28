// Cloudflare Pages hosts the precompressed files but does not negotiate them for Blazor.
const originalFetch = globalThis.fetch;

globalThis.fetch = async function (input, init) {
    const request = new Request(input, init);
    const url = new URL(request.url);

    if (request.method !== 'GET' ||
        !url.pathname.endsWith('.wasm')) {
        return originalFetch(request);
    }

    url.pathname += '.gz';
    let response;
    try {
        response = await originalFetch(new Request(url, {
            cache: request.cache,
            credentials: request.credentials,
            headers: request.headers,
            integrity: '',
            method: request.method,
            mode: request.mode,
            redirect: request.redirect,
            referrer: request.referrer,
            referrerPolicy: request.referrerPolicy,
            signal: request.signal,
        }));
    }
    catch (error) {
        if (request.signal.aborted || error?.name === 'AbortError') {
            throw error;
        }

        return originalFetch(request);
    }

    if (!response.ok) {
        return originalFetch(request);
    }

    if (response.body === null) {
        throw new Error('Compressed WASM response has no body.');
    }

    const decompressed = await new Response(
        response.body.pipeThrough(new DecompressionStream('gzip'))
    ).arrayBuffer();
    await verifyIntegrity(decompressed, request.integrity);

    const headers = new Headers(response.headers);
    headers.delete('content-encoding');
    headers.delete('content-length');
    headers.set('content-type', 'application/wasm');
    return new Response(decompressed, {
        headers,
        status: response.status,
        statusText: response.statusText,
    });
};

async function verifyIntegrity(data, integrity) {
    if (!integrity) {
        return;
    }

    for (const value of integrity.split(/\s+/)) {
        const match = /^(sha256|sha384|sha512)-(.+)$/.exec(value);
        if (match === null) {
            continue;
        }

        const algorithm = {
            sha256: 'SHA-256',
            sha384: 'SHA-384',
            sha512: 'SHA-512',
        }[match[1]];
        const digest = await crypto.subtle.digest(algorithm, data);
        const bytes = new Uint8Array(digest);
        let binary = '';
        for (const byte of bytes) {
            binary += String.fromCharCode(byte);
        }

        if (btoa(binary) === match[2]) {
            return;
        }
    }

    throw new Error('Decompressed WASM integrity check failed.');
}
