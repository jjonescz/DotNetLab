export async function afterStarted(blazor) {
    const dotNetExports = await blazor.runtime.getAssemblyExports('DotNetLab.App.dll');

    // Check whether service worker has an update available.
    (async () => {
        if (location.hostname === 'localhost') {
            return;
        }

        const registration = await navigator.serviceWorker.getRegistration();
        if (!registration) {
            return;
        }
    
        if (registration.waiting) {
            dotNetExports.DotNetLab.Lab.UpdateInfo.UpdateAvailable();
            return;
        }
    
        registration.addEventListener('updatefound', () => {
            dotNetExports.DotNetLab.Lab.UpdateInfo.UpdateAvailable();
            return;
        });
    })();
}
