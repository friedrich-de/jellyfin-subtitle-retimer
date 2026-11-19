// The Subtitle Retimer page's service worker. With it a browser offers to install the page as an app,
// and the installed app still opens, and says so, while the Jellyfin server cannot be reached.
//
// It keeps a copy of the page's own files only. They are the same for everyone and hold no data and no
// login. Every other request of the page (shows, jobs, settings) goes to the server as before and is
// never stored.
'use strict';

var CACHE = 'subtitle-retimer-page-v1';

// The page and its files, as addresses relative to this script at BASE/Retimer/sw.js.
var FILES = [
    '../Retimer',
    'app.js',
    'app.css',
    'manifest.webmanifest',
    'icons/favicon.svg',
    'icons/favicon-32.png'
].map(function (file) { return new URL(file, self.location.href).href; });

self.addEventListener('install', function (event) {
    event.waitUntil(caches.open(CACHE).then(function (cache) {
        // One file the server does not hand out must not stop the rest.
        return Promise.all(FILES.map(function (file) { return cache.add(file).catch(function () { }); }));
    }).then(function () { return self.skipWaiting(); }));
});

self.addEventListener('activate', function (event) {
    event.waitUntil(caches.keys().then(function (names) {
        return Promise.all(names.filter(function (name) {
            return name !== CACHE && name.indexOf('subtitle-retimer-') === 0;
        }).map(function (name) { return caches.delete(name); }));
    }).then(function () { return self.clients.claim(); }));
});

// The page's own files come from the server whenever it answers, so a new plugin version shows at once;
// the kept copy only when it does not. Every other request is not touched.
self.addEventListener('fetch', function (event) {
    var file = pageFile(event.request);
    if (!file) { return; }

    event.respondWith(fetch(event.request).then(function (response) {
        if (response.ok && response.type === 'basic') {
            var copy = response.clone();
            event.waitUntil(caches.open(CACHE).then(function (cache) { return cache.put(file, copy); }));
        }
        return response;
    }, function () {
        return caches.match(file).then(function (kept) { return kept || Response.error(); });
    }));
});

// The page file a request asks for, or null for anything else.
function pageFile(request) {
    if (request.method !== 'GET') { return null; }
    var url = new URL(request.url);
    url.search = '';
    url.hash = '';
    return FILES.indexOf(url.href) >= 0 ? url.href : null;
}
