// Browser helpers used from C# (see Browser.cs). No inline scripts, so a strict CSP can be used.
(function () {
    "use strict";

    window.markup = {
        getItem: function (key) {
            try { return window.localStorage.getItem(key); } catch (e) { return null; }
        },
        setItem: function (key, value) {
            try { window.localStorage.setItem(key, value); return true; } catch (e) { return false; }
        },
        removeItem: function (key) {
            try { window.localStorage.removeItem(key); } catch (e) { /* ignore */ }
        },
        isOnline: function () {
            return navigator.onLine;
        },
        getLocation: function () {
            return new Promise(function (resolve) {
                if (!("geolocation" in navigator)) {
                    resolve({ ok: false, error: "This device or browser does not support location." });
                    return;
                }
                navigator.geolocation.getCurrentPosition(
                    function (p) {
                        resolve({ ok: true, lat: p.coords.latitude, lng: p.coords.longitude, accuracy: p.coords.accuracy || 0 });
                    },
                    function (err) {
                        var msg = "Could not get your location. Turn on GPS/location and try again.";
                        if (err && err.code === 1) msg = "Location permission is blocked. Allow location for this app in your browser/phone settings, then try again.";
                        if (err && err.code === 3) msg = "Getting your location took too long. Move to an open area and try again.";
                        resolve({ ok: false, error: msg });
                    },
                    { enableHighAccuracy: true, timeout: 20000, maximumAge: 60000 }
                );
            });
        },
        openUrl: function (url) {
            var w = window.open(url, "_blank", "noopener");
            if (!w) { window.location.href = url; }
        },
        download: function (fileName, contentType, bytes) {
            var blob = new Blob([bytes], { type: contentType });
            var url = URL.createObjectURL(blob);
            var a = document.createElement("a");
            a.href = url;
            a.download = fileName;
            document.body.appendChild(a);
            a.click();
            a.remove();
            setTimeout(function () { URL.revokeObjectURL(url); }, 10000);
        },
        resizeImage: async function (bytes, contentType, maxSize, maxChars) {
            // Center-crops to a square, shrinks to maxSize px and returns a JPEG data URL
            // no longer than maxChars (or null when the picture cannot be read).
            try {
                var blob = new Blob([bytes], { type: contentType || "image/jpeg" });
                var bmp = await createImageBitmap(blob);
                var side = Math.min(bmp.width, bmp.height);
                var sx = (bmp.width - side) / 2;
                var sy = (bmp.height - side) / 2;
                var sizes = [Math.min(maxSize, side), 192, 144];
                for (var i = 0; i < sizes.length; i++) {
                    var size = sizes[i];
                    var canvas = document.createElement("canvas");
                    canvas.width = size;
                    canvas.height = size;
                    var ctx = canvas.getContext("2d");
                    ctx.fillStyle = "#ffffff";
                    ctx.fillRect(0, 0, size, size);
                    ctx.imageSmoothingQuality = "high";
                    ctx.drawImage(bmp, sx, sy, side, side, 0, 0, size, size);
                    for (var q = 0.85; q >= 0.45; q -= 0.1) {
                        var url = canvas.toDataURL("image/jpeg", q);
                        if (url.length <= maxChars) {
                            if (bmp.close) bmp.close();
                            return url;
                        }
                    }
                }
                if (bmp.close) bmp.close();
                return null;
            } catch (e) {
                return null;
            }
        },
        confirm: function (message) {
            return window.confirm(message);
        },
        onOnline: function (dotnetRef) {
            window.addEventListener("online", function () {
                dotnetRef.invokeMethodAsync("OnOnline");
            });
        }
    };

    // ------------------------------------------------------------------ Beat tracking
    // Records GPS while the app is open: a point when the person moved distanceM metres,
    // or at least every intervalMs while standing still. Keeps the screen awake if the browser allows.
    var track = { ref: null, watchId: null, timer: null, last: null, sent: null, wake: null, intervalMs: 60000, distanceM: 50 };

    function metresBetween(a, b) {
        var r = 6371000, rad = Math.PI / 180;
        var dLat = (b.lat - a.lat) * rad, dLng = (b.lng - a.lng) * rad;
        var h = Math.sin(dLat / 2) * Math.sin(dLat / 2) +
            Math.cos(a.lat * rad) * Math.cos(b.lat * rad) * Math.sin(dLng / 2) * Math.sin(dLng / 2);
        return 2 * r * Math.asin(Math.min(1, Math.sqrt(h)));
    }

    function emitPoint(p) {
        if (!track.ref) return;
        track.sent = { lat: p.lat, lng: p.lng, time: Date.now() };
        track.ref.invokeMethodAsync("OnTrackPoint", p.lat, p.lng, p.accuracy).catch(function () { /* app closing */ });
    }

    function onPosition(pos) {
        var p = { lat: pos.coords.latitude, lng: pos.coords.longitude, accuracy: pos.coords.accuracy || 0 };
        if (p.accuracy > 1000) return;
        track.last = p;
        if (!track.sent || Date.now() - track.sent.time >= track.intervalMs || metresBetween(track.sent, p) >= track.distanceM) {
            emitPoint(p);
        }
    }

    function onPositionError(err) {
        if (!track.ref) return;
        var msg = "GPS signal lost. Tracking will continue when the location is available.";
        if (err && err.code === 1) msg = "Location permission is blocked. Allow location for this app to continue tracking.";
        track.ref.invokeMethodAsync("OnTrackError", msg).catch(function () { });
    }

    function requestWakeLock() {
        if (!track.ref || !("wakeLock" in navigator) || document.visibilityState !== "visible") return Promise.resolve(false);
        return navigator.wakeLock.request("screen").then(function (lock) {
            track.wake = lock;
            lock.addEventListener("release", function () { track.wake = null; });
            return true;
        }).catch(function () { return false; });
    }

    document.addEventListener("visibilitychange", function () {
        if (document.visibilityState === "visible" && track.ref && !track.wake) requestWakeLock();
    });

    window.markup.trackStart = function (dotnetRef, intervalMs, distanceM) {
        window.markup.trackStop();
        if (!("geolocation" in navigator)) return Promise.resolve({ ok: false, wakeLock: false, error: "This device does not support location." });
        track.ref = dotnetRef;
        track.intervalMs = intervalMs;
        track.distanceM = distanceM;
        track.sent = null;
        track.watchId = navigator.geolocation.watchPosition(onPosition, onPositionError,
            { enableHighAccuracy: true, maximumAge: 15000, timeout: 60000 });
        // Standing still: repeat the last known position so the office still sees "last seen".
        track.timer = setInterval(function () {
            if (track.last && track.sent && Date.now() - track.sent.time >= track.intervalMs) emitPoint(track.last);
        }, 15000);
        return requestWakeLock().then(function (wake) { return { ok: true, wakeLock: wake, error: null }; });
    };

    window.markup.trackStop = function () {
        if (track.watchId !== null && "geolocation" in navigator) navigator.geolocation.clearWatch(track.watchId);
        if (track.timer) clearInterval(track.timer);
        if (track.wake) { try { track.wake.release(); } catch (e) { } }
        track.ref = null; track.watchId = null; track.timer = null; track.last = null; track.sent = null; track.wake = null;
    };

    // ------------------------------------------------------------------ Admin map (Leaflet + OpenStreetMap)
    var maps = {};
    var GAP_MS = 5 * 60 * 1000;

    function esc(v) {
        return String(v === null || v === undefined ? "" : v)
            .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
    }

    function pin(text, cls) {
        return L.divIcon({ className: "map-pin " + cls, html: "<span>" + esc(text) + "</span>", iconSize: [30, 30], iconAnchor: [15, 15], popupAnchor: [0, -14] });
    }

    function initials(name) {
        var parts = String(name || "").trim().split(/\s+/);
        if (!parts[0]) return "?";
        return (parts[0][0] + (parts.length > 1 ? parts[parts.length - 1][0] : "")).toUpperCase();
    }

    function money(v) {
        return Number(v || 0).toLocaleString("en-IN", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    }

    function fit(m, layers) {
        var bounds = L.featureGroup(layers).getBounds();
        if (bounds.isValid()) m.map.fitBounds(bounds, { padding: [30, 30], maxZoom: 17 });
    }

    function clear(m) {
        m.layer.clearLayers();
        m.markers = {};
    }

    window.markup.mapInit = function (id) {
        if (typeof L === "undefined") return false;
        if (maps[id]) { maps[id].map.remove(); delete maps[id]; }
        var el = document.getElementById(id);
        if (!el) return false;
        var map = L.map(el, { zoomControl: true }).setView([27.7172, 85.3240], 12);
        L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
            maxZoom: 19,
            attribution: "&copy; <a href=\"https://www.openstreetmap.org/copyright\" target=\"_blank\" rel=\"noopener\">OpenStreetMap</a> contributors"
        }).addTo(map);
        maps[id] = { map: map, layer: L.layerGroup().addTo(map), markers: {} };
        setTimeout(function () { map.invalidateSize(); }, 200);
        return true;
    };

    window.markup.mapDestroy = function (id) {
        if (maps[id]) { maps[id].map.remove(); delete maps[id]; }
    };

    window.markup.mapFocus = function (id, key, lat, lng) {
        var m = maps[id];
        if (!m) return;
        m.map.setView([lat, lng], Math.max(m.map.getZoom(), 16));
        if (key && m.markers[key]) m.markers[key].openPopup();
    };

    /** people: LiveLocation[] (camelCase from C#). */
    window.markup.mapShowLive = function (id, people, fitAll) {
        var m = maps[id];
        if (!m) return;
        clear(m);
        var list = [];
        (people || []).forEach(function (p) {
            if (!p.hasLocation) return;
            var cls = p.isTracking ? "live" : (p.minutesAgo <= 30 ? "recent" : "stale");
            var status = p.isTracking ? "Tracking now" : "Last seen " + esc(p.lastSeen) + " (" + p.minutesAgo + " min ago)";
            var html = "<b>" + esc(p.name) + "</b><br>" + status +
                (p.beatNames ? "<br>Beat: " + esc(p.beatNames) : "") +
                "<br>Visits: " + p.visitsToday + " &middot; Orders: " + p.ordersToday +
                "<br><small>GPS accuracy about " + Math.round(p.accuracy) + " m</small>";
            var mk = L.marker([p.lat, p.lng], { icon: pin(initials(p.name), cls), zIndexOffset: p.isTracking ? 1000 : 0 }).bindPopup(html);
            mk.addTo(m.layer);
            m.markers[p.userId] = mk;
            list.push(mk);
        });
        if (fitAll && list.length) fit(m, list);
    };

    /** route: RouteView (camelCase from C#). */
    window.markup.mapShowRoute = function (id, route) {
        var m = maps[id];
        if (!m) return;
        clear(m);
        var all = [];
        var pts = (route && route.points) || [];

        // Split into tracked segments; join segments across a gap with a dashed grey line.
        var seg = [], prev = null;
        function toMs(t) { var a = t.split(":"); return ((+a[0]) * 3600 + (+a[1]) * 60 + (+(a[2] || 0))) * 1000; }
        function flush() {
            if (seg.length > 1) all.push(L.polyline(seg, { color: "#1d4ed8", weight: 4, opacity: .85 }).addTo(m.layer));
            seg = [];
        }
        pts.forEach(function (p) {
            var ll = [p.lat, p.lng];
            if (prev) {
                var gap = toMs(p.time) - toMs(prev.time) > GAP_MS || prev.kind === "End";
                if (gap) {
                    flush();
                    L.polyline([[prev.lat, prev.lng], ll], { color: "#8a94a6", weight: 3, dashArray: "6 8", opacity: .9 })
                        .bindTooltip("Not tracked " + esc(prev.time.substring(0, 5)) + " - " + esc(p.time.substring(0, 5)))
                        .addTo(m.layer);
                }
            }
            seg.push(ll);
            prev = p;
        });
        flush();

        if (pts.length) {
            var first = pts[0], last = pts[pts.length - 1];
            all.push(L.marker([first.lat, first.lng], { icon: pin("S", "start") })
                .bindPopup("<b>Start</b><br>" + esc(first.time.substring(0, 5))).addTo(m.layer));
            all.push(L.marker([last.lat, last.lng], { icon: pin(last.kind === "End" ? "E" : "●", last.kind === "End" ? "end" : "live") })
                .bindPopup("<b>" + (last.kind === "End" ? "Beat ended" : "Last point") + "</b><br>" + esc(last.time.substring(0, 5))).addTo(m.layer));
        }

        if (route && route.attendanceLat !== null && route.attendanceLat !== undefined && (route.attendanceLat || route.attendanceLng)) {
            all.push(L.marker([route.attendanceLat, route.attendanceLng], { icon: pin("A", "attend") })
                .bindPopup("<b>Attendance</b><br>" + esc(route.attendanceTime)).addTo(m.layer));
        }

        // Beat shops: grey = not visited today, shops visited are shown by the numbered stops.
        ((route && route.parties) || []).forEach(function (s) {
            if (s.lat === null || s.lat === undefined || s.visited) return;
            var mk = L.circleMarker([s.lat, s.lng], { radius: 7, color: "#8a94a6", weight: 2, fillColor: "#fff", fillOpacity: 1 })
                .bindPopup("<b>" + esc(s.name) + "</b><br>Not visited" + (s.beatName ? "<br>Beat: " + esc(s.beatName) : "") + (s.address ? "<br>" + esc(s.address) : ""))
                .addTo(m.layer);
            m.markers["party-" + s.id] = mk;
            all.push(mk);
        });

        ((route && route.stops) || []).forEach(function (v, i) {
            if (!v.lat && !v.lng) return;
            var html = "<b>" + (i + 1) + ". " + esc(v.partyName) + "</b><br>" + esc(v.time) + "<br>" +
                (v.isProductive ? "Order " + esc(v.orderNo) + " &middot; NPR " + money(v.orderValue) : "No order: " + esc(v.noOrderReason));
            var mk = L.marker([v.lat, v.lng], { icon: pin(String(i + 1), v.isProductive ? "stop" : "noorder"), zIndexOffset: 500 })
                .bindPopup(html).addTo(m.layer);
            m.markers["stop-" + i] = mk;
            all.push(mk);
        });

        if (all.length) fit(m, all);
    };

    if ("serviceWorker" in navigator) {
        window.addEventListener("load", function () {
            navigator.serviceWorker.register("service-worker.js").catch(function () { /* offline install not available */ });
        });
    }
})();