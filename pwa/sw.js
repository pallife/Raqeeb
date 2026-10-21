'use strict';
/* عامل خدمة بسيط: يجعل الصفحة قابلة للتثبيت ويحتفظ بقشرة الواجهة للفتح السريع حتى مع شبكة بطيئة.
   لا يخزّن أي استدعاء API أبداً — الشبكة أولاً دائماً. */
const CACHE = 'raqeeb-shell-v2';
self.addEventListener('install', (e) => { self.skipWaiting(); });
self.addEventListener('activate', (e) => {
  e.waitUntil((async () => {
    for (const k of await caches.keys()) if (k !== CACHE) await caches.delete(k);
    await self.clients.claim();
  })());
});
self.addEventListener('fetch', (e) => {
  const req = e.request, url = new URL(req.url);
  if (req.method !== 'GET' || url.origin !== location.origin) return;
  const p = url.pathname;
  const isShell = req.mode === 'navigate' || /\/static\/[^/]+\.(js|css|png|ttf)$/.test(p);
  if (!isShell || /\/api\/|\/dl\//.test(p)) return;
  e.respondWith((async () => {
    try {
      const r = await fetch(req, { cache: 'no-cache' });
      if (r.ok) { const c = await caches.open(CACHE); c.put(req, r.clone()); }
      return r;
    } catch {
      const hit = await caches.match(req, { ignoreSearch: true });
      if (hit) return hit;
      throw new Error('offline');
    }
  })());
});
