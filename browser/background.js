"use strict";

const NativeHost = "com.sada.youtube";
const YouTubeOrigin = "https://www.youtube.com";
const VideoId = /^[A-Za-z0-9_-]{11}$/;
const activeTabs = new Map();
let port = null;
let retryAfter = 0;
let lastSequence = 0;

function parseYouTubeUrl(value) {
    try {
        const url = new URL(value);
        return url.origin === YouTubeOrigin ? url : null;
    } catch {
        return null;
    }
}

function videoIdFromUrl(url) {
    const videoId = url?.pathname === "/watch" ? url.searchParams.get("v") : null;
    return videoId && VideoId.test(videoId) ? videoId : null;
}

function textField(value, limit) {
    return typeof value === "string" ? value.replace(/[\u0000-\u001f\u007f-\u009f]/g, " ")
        .trim().slice(0, limit).replace(/[\ud800-\udbff]$/u, "") : "";
}

function ensurePort() {
    if (port || Date.now() < retryAfter) return;
    try {
        const connection = chrome.runtime.connectNative(NativeHost);
        port = connection;
        connection.onDisconnect.addListener(() => {
            void chrome.runtime.lastError;
            if (port !== connection) return;
            port = null;
            retryAfter = Date.now() + 3000;
        });
    } catch {
        retryAfter = Date.now() + 3000;
    }
}

function send(packet) {
    if (packet.type === "playback") ensurePort();
    if (!port) return;
    lastSequence = Math.max(Date.now() * 1000, lastSequence + 1);
    try {
        port.postMessage({ ...packet, sequence: lastSequence });
    } catch {
        port = null;
        retryAfter = Date.now() + 3000;
    }
}

function closeTab(tabId) {
    if (activeTabs.delete(tabId)) send({ type: "closed", tabId });
}

chrome.runtime.onMessage.addListener((message, sender) => {
    const tabId = sender.tab?.id;
    const url = parseYouTubeUrl(sender.url);
    if (!Number.isInteger(tabId) || tabId < 0 || sender.frameId !== 0 || !url) return;
    const videoId = videoIdFromUrl(url);
    if (message?.type === "closed") {
        if (message.ad === true && videoId && message.videoId === videoId) {
            activeTabs.delete(tabId);
            ensurePort();
            send({ type: "closed", tabId });
        } else {
            closeTab(tabId);
        }
        return;
    }

    if (message?.type !== "playback" || !videoId || videoId !== message.videoId) return;
    const { duration, position, rate, playing } = message;
    if (!Number.isFinite(duration) || duration <= 0 || duration > 86400 ||
        !Number.isFinite(position) || position < 0 || position > duration ||
        !Number.isFinite(rate) || rate <= 0 || rate > 16 || typeof playing !== "boolean") return;

    const title = textField(message.title, 400);
    if (!title) return;
    activeTabs.set(tabId, { videoId, lastReportAt: Date.now() });
    send({
        type: "playback", tabId, videoId, title, position, duration, rate, playing,
        artist: textField(message.artist, 200), album: textField(message.album, 200)
    });
});

chrome.tabs.onRemoved.addListener(closeTab);
chrome.tabs.onUpdated.addListener((tabId, change) => {
    if (change.url && activeTabs.has(tabId) && activeTabs.get(tabId).videoId !== videoIdFromUrl(parseYouTubeUrl(change.url))) {
        closeTab(tabId);
    }
});

setInterval(() => {
    // A connected native port keeps this worker alive, while Chrome can throttle
    // the content script's timer in YouTube tabs left paused in the background.
    if (!port) return;
    const now = Date.now();
    for (const [tabId, state] of activeTabs) {
        if (now - state.lastReportAt < 2000) continue;
        void chrome.tabs.sendMessage(tabId, { type: "request-playback" }, { frameId: 0 }).catch(() => {});
    }
}, 2000);
