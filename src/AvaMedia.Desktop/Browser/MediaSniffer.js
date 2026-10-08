(() => {
    const token = '__AVAMEDIA_TOKEN__';
    if (window.__avaMediaSniffer === token) return;
    window.__avaMediaSniffer = token;
    const pending = new Map();
    const seen = new Map();
    let timer;
    const extension = url => /\.(mp4|webm|mkv|mov|m4v|m3u8|mpd)(?:[?#]|$)/i.test(url);
    const isMedia = mime => /^(video\/|application\/(?:vnd\.apple\.mpegurl|x-mpegurl|dash\+xml))/i.test(mime || '');
    const bridge = message => {
        if (window !== window.top) {
            try { window.top.postMessage({ avaMedia: token, body: JSON.stringify(message) }, '*'); } catch { }
            return;
        }
        message.page = location.href;
        message.title = document.title;
        message.userAgent = navigator.userAgent;
        const body = JSON.stringify(message);
        try {
            if (window.webkit?.messageHandlers?.postAvWebViewMessage)
                window.webkit.messageHandlers.postAvWebViewMessage.postMessage(body);
            else if (window === window.top && window.chrome?.webview)
                window.chrome.webview.postMessage(body);
            else if (typeof invokeCSharpAction === 'function') invokeCSharpAction(body);
        } catch { }
    };
    if (window === window.top) addEventListener('message', event => {
        if (event.data?.avaMedia === token && typeof event.data.body === 'string' && event.data.body.length < 200000) {
            try { bridge(JSON.parse(event.data.body)); } catch { }
        }
    });
    const flush = () => {
        timer = undefined;
        if (!pending.size) return;
        bridge({ token, page: location.href, title: document.title, userAgent: navigator.userAgent, media: [...pending.values()].slice(0, 100) });
        pending.clear();
    };
    const report = (value, mime = '', duration = 0, origin = '', directVideo = false) => {
        try {
            if (typeof value !== 'string' || !value.trim()) return;
            if (mime && !isMedia(mime) && !/^application\/octet-stream/i.test(mime)) return;
            const url = new URL(value, location.href).href;
            if (!/^https?:/i.test(url) || /\.(ts|m2ts|m4s|aac)(?:[?#]|$)/i.test(url)) return;
            if (!extension(url) && !isMedia(mime) && !directVideo) return;
            // A video element with an extensionless HTTP source is a media request.
            if (!mime && !extension(url) && directVideo) mime = 'video/mp4';
            const item = { url, mime, duration: Number.isFinite(duration) ? duration : 0, referer: location.href, origin };
            const signature = JSON.stringify(item);
            if (seen.get(url) === signature || seen.size >= 500) return;
            seen.set(url, signature);
            pending.set(url, item);
            if (!timer) timer = setTimeout(flush, 200);
        } catch { }
    };
    const scan = () => {
        if (window === window.top) bridge({ token, media: [] });
        for (const video of document.querySelectorAll('video')) {
            if (video.mediaKeys) {
                bridge({ token, encrypted: true });
                continue;
            }
            report(video.currentSrc || video.src, video.getAttribute('type') || '', video.duration, '', true);
            for (const source of video.querySelectorAll('source')) report(source.src, source.type, video.duration, '', true);
        }
        for (const entry of performance.getEntriesByType('resource').slice(-1000)) report(entry.name);
    };
    bridge({ token, media: [] });
    const originalFetch = window.fetch;
    if (originalFetch) window.fetch = function (...args) {
        // Preserve fetch timing, rejection and response body ownership.
        const task = originalFetch.apply(this, args);
        task.then(response => {
            if (response.ok) report(response.url, response.headers.get('content-type') || '', 0,
                new URL(response.url, location.href).origin !== location.origin ? location.origin : '');
        }, () => { });
        return task;
    };
    const open = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (...args) {
        this.addEventListener('load', () => {
            if (this.status >= 200 && this.status < 400 && this.responseURL) report(this.responseURL, this.getResponseHeader('content-type') || '', 0,
                new URL(this.responseURL, location.href).origin !== location.origin ? location.origin : '');
        }, { once: true });
        return open.apply(this, args);
    };
    try {
        new PerformanceObserver(list => { for (const entry of list.getEntries()) report(entry.name); }).observe({ type: 'resource', buffered: true });
    } catch { }
    for (const name of ['loadedmetadata', 'play', 'durationchange']) document.addEventListener(name, scan, true);
    document.addEventListener('encrypted', () => bridge({ token, encrypted: true }), true);
    const start = () => {
        scan();
        let scanTimer;
        new MutationObserver(() => {
            if (!scanTimer) scanTimer = setTimeout(() => { scanTimer = undefined; scan(); }, 400);
        }).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['src'] });
    };
    if (document.documentElement) start(); else document.addEventListener('DOMContentLoaded', start, { once: true });
    addEventListener('load', scan);
    // Also run inside same-origin frames on platforms without a document-start API.
    window.__avaMediaScan = scan;
})();
