export async function afterStarted(blazor) {
    const dotNetExports = await blazor.runtime.getAssemblyExports('DotNetLab.App.dll');

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
            waitForInstalledState();
            return;
        }
    
        registration.addEventListener('updatefound', waitForInstalledState);

        function waitForInstalledState() {
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
}
