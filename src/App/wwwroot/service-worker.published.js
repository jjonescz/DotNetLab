// Caution! Be sure you understand the caveats before publishing an application with
// offline support. See https://aka.ms/blazor-offline-considerations

// Some reload logic inspired by https://stackoverflow.com/a/50535316.

/// <reference lib="webworker" />

const worker = /** @type {ServiceWorkerGlobalScope} */ (self);

worker.importScripts('./service-worker-assets.js');
worker.addEventListener('install', event => event.waitUntil(onInstall(event)));
worker.addEventListener('activate', event => event.waitUntil(onActivate(event)));
worker.addEventListener('fetch', event => event.respondWith(onFetch(event)));
worker.addEventListener('message', event => onMessage(event));

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [ /\.dll$/, /\.pdb$/, /\.wasm$/, /\.html$/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.webmanifest$/, /\.ttf$/ ];
const offlineAssetsExclude = [ /^service-worker\.js$/ ];

// Replace with your base path if you are hosting on a subfolder. Ensure there is a trailing '/'.
const base = "/";
const baseUrl = new URL(base, self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

async function onInstall() {
    console.info('Service worker: Install');

    // Fetch and cache all matching items from the assets manifest
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
    const cache = await caches.open(cacheName);
    await cache.addAll(assetsRequests);
    
    // Clean responses.
    // Removes `redirected` flag so the response is servable by the service worker.
    // https://stackoverflow.com/a/45440505/9080566
    // https://github.com/dotnet/aspnetcore/issues/33872
    // Also avoids other inexplicable failures when serving responses from the service worker.
    for (const request of assetsRequests) {
        const response = await cache.match(request);
        const clonedResponse = response.clone();
        const responseData = await clonedResponse.arrayBuffer();
        const cleanedResponse = new Response(responseData, {
            headers: {
                'content-type': clonedResponse.headers.get('content-type') ?? '',
                'content-length': responseData.byteLength.toString(),
            },
        });
        await cache.put(request, cleanedResponse);
    }
}

async function onActivate() {
    console.info('Service worker: Activate');

    // Delete unused caches
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
}

/**
 * @param {FetchEvent} event
 */
async function onFetch(event) {
    // If there is only one remaining client that is navigating
    // (e.g., being refreshed using the broswer reload button),
    // and there is a new version of the service worker waiting,
    // force active the new version and reload the page (so it uses the new version).
    if (event.request.mode === 'navigate' &&
        event.request.method === 'GET' &&
        worker.registration.waiting &&
        (await worker.clients.matchAll()).length < 2
    ) {
        worker.registration.waiting.postMessage('skipWaiting');
        return new Repsonse('', { headers: { Refresh: '0' } });
    }

    let cachedResponse = null;
    if (event.request.method === 'GET') {
        // For all navigation requests, try to serve index.html from cache,
        // unless that request is for an offline resource.
        // If you need some URLs to be server-rendered, edit the following check to exclude those URLs
        const shouldServeIndexHtml = event.request.mode === 'navigate'
            && !manifestUrlList.some(url => url === event.request.url);

        if (shouldServeIndexHtml) {
            console.debug(`Service worker: serving index.html for ${event.request.url}`);
        }

        const request = shouldServeIndexHtml ? 'index.html' : event.request;

        const cache = await caches.open(cacheName);
        // We ignore search query (so our pre-cached `app.css` matches request `app.css?v=2`),
        // we have pre-cached the latest versions of all static assets anyway.
        cachedResponse = await cache.match(request, { ignoreSearch: true });
    }

    if (!cachedResponse) {
        console.debug(`Service worker: cache miss for ${event.request.url}`);
    }

    return cachedResponse || fetch(event.request);
}

/**
 * @param {ExtendableMessageEvent} event
 */
function onMessage(event) {
    if (event.data === 'skipWaiting') {
        worker.skipWaiting();
    }
}
