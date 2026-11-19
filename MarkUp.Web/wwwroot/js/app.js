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

    if ("serviceWorker" in navigator) {
        window.addEventListener("load", function () {
            navigator.serviceWorker.register("service-worker.js").catch(function () { /* offline install not available */ });
        });
    }
})();
