export async function checkForUpdates() {
    const registration = await navigator.serviceWorker.getRegistration();
    await registration?.update();
}
