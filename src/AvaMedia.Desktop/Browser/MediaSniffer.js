(() => {
    const token = '__AVAMEDIA_TOKEN__';
    if (window.__avaMediaSniffer === token) return;
    window.__avaMediaSniffer = token;
    const pending = new Map();
    const seen = new Map();
    const fragments = [];
    const removed = new Set();
    const videoLabel = '__AVAMEDIA_VIDEO_LABEL__';
    const frameId = Math.random().toString(36).slice(2);
    const playerIds = new WeakMap(), players = new Map(), outlines = new Map();
    let nextPlayer = 0, selectedPlayer = '', overlay, overlayRoot, positionTimer;
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
    addEventListener('message', event => {
        if (event.data?.avaMedia === token && typeof event.data.located === 'string') {
            for (const frame of document.querySelectorAll('iframe,frame')) if (frame.contentWindow === event.source)
                frame.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'smooth' });
            if (window !== window.top) parent.postMessage({ avaMedia: token, located: event.data.located }, '*');
            schedulePositions();
            return;
        }
        if (event.data?.avaMedia === token && typeof event.data.locate === 'string') {
            locate(event.data.locate);
            return;
        }
        if (window !== window.top) return;
        if (event.data?.avaMedia === token && typeof event.data.body === 'string' && event.data.body.length < 200000) {
            try { bridge(JSON.parse(event.data.body)); } catch { }
        }
    });
    const playerInfo = video => {
        if (!playerIds.has(video)) playerIds.set(video, { id: `${frameId}-${++nextPlayer}`, label: `${videoLabel} ${nextPlayer}` });
        const info = playerIds.get(video);
        players.set(info.id, video);
        return info;
    };
    const sourceUrls = video => [video.currentSrc, video.src, ...[...video.querySelectorAll('source')].map(source => source.src)].filter(Boolean);
    const findPlayer = url => {
        const videos = [...players.values()].filter(video => video.isConnected && !video.mediaKeys);
        const exact = videos.find(video => sourceUrls(video).includes(url));
        if (exact) return playerInfo(exact);
        // A single blob player in this frame owns the media requests observed by this frame.
        // With multiple blob players there is no reliable URL-to-element mapping.
        if (videos.length === 1 && videos[0].currentSrc.startsWith('blob:')) return playerInfo(videos[0]);
        return null;
    };
    const updatePositions = () => {
        positionTimer = undefined;
        const active = new Set([...seen.values()].map(item => item.locationId).filter(Boolean));
        if (!active.size && !overlay) return;
        if (!overlay) {
            overlay = document.createElement('div');
            overlay.style.cssText = 'all:initial!important;position:fixed!important;inset:0!important;pointer-events:none!important;z-index:2147483647!important;';
            overlayRoot = overlay.attachShadow({ mode: 'closed' });
            const style = document.createElement('style');
            style.textContent = '.box{position:absolute;box-sizing:border-box;border:2px solid #1688ff;border-radius:4px;pointer-events:none}.box.selected{border:3px solid #ffad20}.label{position:absolute;top:0;left:0;background:#0874d1;color:white;font:600 12px/20px system-ui,sans-serif;padding:0 6px;border-radius:0 0 4px 0}.selected .label{background:#865000}';
            overlayRoot.append(style);
        }
        if (!overlay.isConnected) document.documentElement.append(overlay);
        for (const [id, box] of outlines) if (!active.has(id) || !players.get(id)?.isConnected) { box.remove(); outlines.delete(id); }
        for (const id of active) {
            const video = players.get(id);
            if (!video?.isConnected || video.mediaKeys) continue;
            let box = outlines.get(id);
            if (!box) {
                box = document.createElement('div');
                const label = document.createElement('span');
                label.className = 'label'; label.textContent = playerIds.get(video).label;
                box.append(label); overlayRoot.append(box); outlines.set(id, box);
            }
            const rect = video.getBoundingClientRect(), style = getComputedStyle(video);
            const visible = rect.width > 0 && rect.height > 0 && rect.bottom > 0 && rect.right > 0 && rect.top < innerHeight && rect.left < innerWidth && style.visibility !== 'hidden' && style.display !== 'none';
            box.hidden = !visible;
            box.className = id === selectedPlayer ? 'box selected' : 'box';
            box.style.cssText = `left:${rect.left}px;top:${rect.top}px;width:${rect.width}px;height:${rect.height}px;`;
        }
    };
    const schedulePositions = () => { if (!positionTimer) positionTimer = requestAnimationFrame(updatePositions); };
    const locate = id => {
        const video = players.get(id);
        if (video?.isConnected) {
            selectedPlayer = id;
            video.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'smooth' });
            if (window !== window.top) parent.postMessage({ avaMedia: token, located: id }, '*');
            schedulePositions();
        }
        for (const frame of document.querySelectorAll('iframe,frame')) {
            try { frame.contentWindow?.postMessage({ avaMedia: token, locate: id }, '*'); } catch { }
        }
    };
    window.__avaMediaLocate = locate;
    addEventListener('scroll', schedulePositions, true);
    addEventListener('resize', schedulePositions);
    const playerResize = typeof ResizeObserver === 'function' ? new ResizeObserver(schedulePositions) : null;
    const flush = () => {
        timer = undefined;
        if (!pending.size && !removed.size) return;
        bridge({ token, media: [...pending.values()].slice(0, 100), discard: [...removed].slice(0, 1000) });
        pending.clear();
        removed.clear();
    };
    const schedule = () => { if (!timer) timer = setTimeout(flush, 200); };
    const discard = url => { pending.delete(url); seen.delete(url); removed.add(url); schedule(); schedulePositions(); };
    const literal = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const addFragment = (value, base, representation) => {
        try {
            if (!value || value.length > 8192 || fragments.length >= 500) return;
            let number = 0;
            const slots = [];
            const template = value.replace(/\$\$|\$(RepresentationID|Bandwidth|Number|Time)(?:%0\d+d)?\$/g, (match, name) => {
                if (match === '$$') return '$';
                if (name === 'RepresentationID' || name === 'Bandwidth') return representation?.getAttribute(name === 'RepresentationID' ? 'id' : 'bandwidth') || match;
                const slot = `__ava_segment_${number++}__`; slots.push(slot); return slot;
            });
            let pattern = literal(new URL(template, base).href);
            for (const slot of slots) pattern = pattern.replace(slot, '\\d+');
            fragments.push(new RegExp('^' + pattern + '$'));
        } catch { }
    };
    const inspectManifest = (url, text) => {
        if (typeof text !== 'string' || text.length > 2 * 1024 * 1024) return;
        try {
            if (/^\s*#EXTM3U/.test(text)) {
                if (!/#EXT(?:INF|-X-TARGETDURATION|-X-PART):/.test(text)) return;
                for (const line of text.split(/\r?\n/)) {
                    const value = line.trim();
                    if (value && !value.startsWith('#')) addFragment(value, url);
                    if (/^#EXT-X-(?:MAP|PART|PRELOAD-HINT):/.test(value)) {
                        const uri = /\bURI="([^"]+)"/.exec(value);
                        if (uri) addFragment(uri[1], url);
                    }
                }
            } else if (/<(?:\w+:)?MPD\b/.test(text)) {
                const xml = new DOMParser().parseFromString(text, 'application/xml');
                const child = (node, name) => [...node.children].find(item => item.localName === name);
                const baseFor = node => {
                    const parent = node.parentElement ? baseFor(node.parentElement) : url;
                    const base = child(node, 'BaseURL');
                    return base ? new URL(base.textContent.trim(), parent).href : parent;
                };
                for (const representation of [...xml.getElementsByTagNameNS('*', 'Representation')].slice(0, 100)) {
                    const base = baseFor(representation);
                    for (const name of ['media', 'initialization']) {
                        for (let node = representation; node; node = node.parentElement) {
                            const template = child(node, 'SegmentTemplate')?.getAttribute(name);
                            if (template) { addFragment(template, base, representation); break; }
                        }
                    }
                    for (let node = representation; node; node = node.parentElement) {
                        const list = child(node, 'SegmentList');
                        if (!list) continue;
                        for (const segment of [...list.children].slice(0, 500))
                            addFragment(segment.getAttribute('media') || segment.getAttribute('sourceURL'), base, representation);
                        break;
                    }
                }
            } else return;
            for (const value of new Set([...seen.keys(), ...performance.getEntriesByType('resource').slice(-1000).map(entry => entry.name)]))
                if (fragments.some(pattern => pattern.test(value))) discard(value);
        } catch { }
    };
    const isManifest = (url, mime) => /\.(m3u8|mpd)(?:[?#]|$)/i.test(url) || /mpegurl|dash\+xml/i.test(mime);
    const readManifest = async response => {
        // Read only a bounded clone of an already requested manifest; never replay signed requests.
        const reader = response.body?.getReader();
        if (!reader) return;
        const decoder = new TextDecoder();
        let text = '', size = 0;
        try {
            while (true) {
                const part = await reader.read();
                if (part.done) break;
                size += part.value.length;
                if (size > 2 * 1024 * 1024) { void reader.cancel(); return; }
                text += decoder.decode(part.value, { stream: true });
            }
            inspectManifest(response.url, text + decoder.decode());
        } catch { }
    };
    const report = (value, mime = '', duration = 0, origin = '', directVideo = false) => {
        try {
            if (typeof value !== 'string' || !value.trim()) return;
            const url = new URL(value, document.baseURI).href;
            if (mime && !isMedia(mime) && !/^application\/octet-stream/i.test(mime)) {
                // Playlists are also commonly served as text/plain; keep extension-based intake for them.
                if (!isManifest(url, mime) && (seen.has(url) || extension(url))) discard(url);
                return;
            }
            if (!/^https?:/i.test(url) || /\.(ts|m2ts|m4s|aac)(?:[?#]|$)/i.test(url)) return;
            if (fragments.some(pattern => pattern.test(url))) { discard(url); return; }
            if (!extension(url) && !isMedia(mime) && !directVideo) return;
            // A video element with an extensionless HTTP source is a media request.
            if (!mime && !extension(url) && directVideo) mime = 'video/mp4';
            // srcdoc/about:blank frames inherit their parent's base URL but are not valid HTTP Referers.
            const referer = /^https?:/i.test(location.href) ? location.href : document.referrer;
            const previous = seen.get(url);
            const player = findPlayer(url);
            const item = { url, mime: mime || previous?.mime || '', duration: Math.max(previous?.duration || 0, Number.isFinite(duration) ? duration : 0), referer: /^https?:/i.test(referer) ? referer : '', origin: /^https?:/i.test(origin) ? origin : previous?.origin || '', locationId: player?.id || '', locationLabel: player?.label || '' };
            if (JSON.stringify(previous) === JSON.stringify(item) || (!previous && seen.size >= 500)) return;
            seen.set(url, item);
            pending.set(url, item);
            schedule();
            schedulePositions();
        } catch { }
    };
    const scan = () => {
        if (window === window.top) bridge({ token, media: [] });
        for (const [id, video] of players) if (!video.isConnected) { playerResize?.unobserve(video); players.delete(id); }
        const videos = [...document.querySelectorAll('video')];
        for (const video of videos) {
            playerInfo(video);
            playerResize?.observe(video);
        }
        for (const video of videos) {
            if (video.mediaKeys) {
                bridge({ token, encrypted: true });
                continue;
            }
            report(video.currentSrc || video.src, video.getAttribute('type') || '', video.duration, '', true);
            for (const source of video.querySelectorAll('source')) report(source.src, source.type, video.duration, '', true);
        }
        for (const entry of performance.getEntriesByType('resource').slice(-1000)) report(entry.name);
        // Refresh position metadata when a delayed player appears or is removed.
        for (const item of seen.values()) report(item.url, item.mime, item.duration, item.origin);
        schedulePositions();
    };
    bridge({ token, media: [] });
    const originalFetch = window.fetch;
    if (originalFetch) window.fetch = function (...args) {
        // Preserve fetch timing, rejection and response body ownership.
        const task = originalFetch.apply(this, args);
        task.then(response => {
            try {
                if (!response.ok) return;
                const mime = response.headers.get('content-type') || '';
                report(response.url, mime, 0, new URL(response.url, document.baseURI).origin !== location.origin ? location.origin : '');
                if (isManifest(response.url, mime)) void readManifest(response.clone());
            } catch { }
        }, () => { });
        return task;
    };
    const open = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (...args) {
        this.addEventListener('load', () => {
            try {
                if (this.status < 200 || this.status >= 400 || !this.responseURL) return;
                const mime = this.getResponseHeader('content-type') || '';
                report(this.responseURL, mime, 0, new URL(this.responseURL, document.baseURI).origin !== location.origin ? location.origin : '');
                if (!isManifest(this.responseURL, mime)) return;
                if (!this.responseType || this.responseType === 'text') inspectManifest(this.responseURL, this.responseText);
                else if (this.responseType === 'document' && this.responseXML) inspectManifest(this.responseURL, new XMLSerializer().serializeToString(this.responseXML));
                else if (this.responseType === 'arraybuffer' && this.response?.byteLength <= 2 * 1024 * 1024) inspectManifest(this.responseURL, new TextDecoder().decode(this.response));
            } catch { }
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
