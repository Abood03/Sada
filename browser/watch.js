"use strict";

(() => {
    const HeartbeatMs = 750;
    const VideoId = /^[A-Za-z0-9_-]{11}$/;
    let reported = false;
    let navigating = false;
    let scheduled = false;
    let waiting = false;
    let lastPacket = null;
    let lastSentAt = 0;
    let trackedVideo = null;
    let trackedPlayer = null;
    let announcedAd = null;
    const playerChanges = new MutationObserver(() => synchronize());

    function send(packet) {
        void chrome.runtime.sendMessage(packet).catch(() => {});
    }

    function close(adVideoId = null) {
        if (adVideoId) {
            if (announcedAd !== adVideoId) send({ type: "closed", ad: true, videoId: adVideoId });
        } else if (reported) {
            send({ type: "closed" });
        }
        announcedAd = adVideoId;
        reported = false;
        lastPacket = null;
    }

    function synchronize() {
        const videoId = location.pathname === "/watch" ? new URLSearchParams(location.search).get("v") : null;
        if (navigating || !videoId || !VideoId.test(videoId)) return close();

        const watch = document.querySelector(`ytd-watch-flexy[video-id="${videoId}"]`);
        if (!watch) return close();
        const player = watch.querySelector("#movie_player");
        if (!player) return close();
        if (player !== trackedPlayer) {
            trackedPlayer = player;
            playerChanges.disconnect();
            playerChanges.observe(player, { attributes: true, attributeFilter: ["class"] });
        }
        if (player.matches(".ad-showing, .ad-interrupting")) return close(videoId);
        announcedAd = null;
        if (player.matches(".ytp-live")) return close();
        const video = player.querySelector("video.html5-main-video");
        if (!video || video.readyState < HTMLMediaElement.HAVE_METADATA || video.ended) return close();
        const { duration, currentTime, playbackRate } = video;
        if (!Number.isFinite(duration) || duration <= 0 || duration > 86400 || !Number.isFinite(currentTime)) return close();

        const title = watch.querySelector("ytd-watch-metadata h1")?.textContent.trim();
        if (!title) return close();
        if (video !== trackedVideo || lastPacket?.videoId !== videoId) {
            waiting = video.readyState < HTMLMediaElement.HAVE_FUTURE_DATA;
            trackedVideo = video;
        }
        const packet = {
            type: "playback", videoId, title,
            position: Math.max(0, Math.min(currentTime, duration)), duration, rate: playbackRate,
            playing: !video.paused && !video.seeking && !waiting && video.readyState >= HTMLMediaElement.HAVE_FUTURE_DATA,
            artist: "", album: ""
        };
        const metadata = navigator.mediaSession?.metadata;
        // On regular watch pages the Media Session artist can be the channel name.
        if (metadata?.title === title && metadata.album?.trim() && metadata.artist?.trim()) {
            packet.artist = metadata.artist.trim();
            packet.album = metadata.album.trim();
        }

        const now = performance.now();
        if (lastPacket && now - lastSentAt < HeartbeatMs &&
            Object.keys(packet).every(key => packet[key] === lastPacket[key])) return;
        reported = true;
        lastPacket = packet;
        lastSentAt = now;
        send(packet);
    }

    function schedule() {
        if (scheduled) return;
        scheduled = true;
        setTimeout(() => {
            scheduled = false;
            synchronize();
        }, 0);
    }

    for (const event of ["loadedmetadata", "durationchange", "playing", "play", "pause", "seeking",
        "seeked", "ratechange", "waiting", "canplay", "ended", "emptied"]) {
        document.addEventListener(event, ({ target }) => {
            if (!(target instanceof HTMLMediaElement) || !target.matches("#movie_player video.html5-main-video")) return;
            if (event === "waiting") waiting = true;
            if (event === "playing" || event === "canplay" || event === "seeked") waiting = false;
            schedule();
        }, true);
    }

    document.addEventListener("yt-navigate-start", () => {
        navigating = true;
        close();
    });
    document.addEventListener("yt-navigate-finish", () => {
        navigating = false;
        schedule();
    });
    window.addEventListener("pagehide", () => close());
    window.addEventListener("pageshow", schedule);
    chrome.runtime.onMessage.addListener((message, sender, respond) => {
        if (message?.type !== "request-playback" || sender.id !== chrome.runtime.id) return;
        synchronize();
        respond(true);
    });
    setInterval(synchronize, HeartbeatMs);
    synchronize();
})();
