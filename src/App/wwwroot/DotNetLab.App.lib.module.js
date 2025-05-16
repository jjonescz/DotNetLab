export async function afterStarted(blazor) {
    /** @type {import('./dotnet').RuntimeAPI} */
    const runtime = blazor.runtime;

    const dotNetExports = await runtime.getAssemblyExports('DotNetLab.App.dll');

    globalThis.DotNetLab = dotNetExports.DotNetLab;

    // When a new service worker version is activated
    // (after user clicks "Refresh" which sends 'skipWaiting' message to the worker),
    // reload the page so the new service worker is used to load all the assets.
    let refreshing = false;
    navigator.serviceWorker.addEventListener('controllerchange', () => {
        // Prevent inifinite refresh loop when "Update on Reload" is enabled in DevTools.
        if (refreshing) {
            return;
        }

        refreshing = true;
        window.location.reload();
    });

    // Check whether service worker has an update available.
    (async () => {
        if (location.hostname === 'localhost') {
            return;
        }

        const registration = await navigator.serviceWorker.getRegistration();
        if (!registration) {
            return;
        }

        if (!navigator.serviceWorker.controller) {
            // No service worker controlling the page, so the new service worker
            // will be automatically activated immediately, we don't need to do anything.
            return;
        }
    
        if (registration.waiting) {
            updateAvailable();
            return;
        }

        if (registration.installing) {
            updateDownloading();
            return;
        }
    
        registration.addEventListener('updatefound', updateDownloading);

        function updateDownloading() {
            dotNetExports.DotNetLab.Lab.UpdateInfo.UpdateDownloading();

            registration.installing.addEventListener('statechange', (event) => {
                if (event.target.state === 'installed') {
                    updateAvailable();
                }
            })
        }

        function updateAvailable() {
            dotNetExports.DotNetLab.Lab.UpdateInfo.UpdateAvailable(() => {
                registration.waiting.postMessage('skipWaiting');
            });
        }
    })();

    // Notify the app when the screen is narrow or wide.
    (async () => {
        const mediaQuery = window.matchMedia("(max-width: 600px)");
        mediaQuery.addEventListener('change', reportMediaQuery);
        reportMediaQuery(mediaQuery);

        /**
         * @param {MediaQueryList | MediaQueryListEvent} e
         */
        function reportMediaQuery(e) {
            dotNetExports.DotNetLab.Lab.ScreenInfo.SetNarrowScreen(e.matches);
        }
    })();
}
