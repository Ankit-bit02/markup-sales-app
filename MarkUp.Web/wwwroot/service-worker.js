// Development service worker: does nothing, so you always get the latest code while developing.
// The published version (service-worker.published.js) caches the app for offline use.
self.addEventListener('fetch', () => { });
