const assert = require("node:assert/strict");
const { createHash } = require("node:crypto");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const TrackId = "t11AaBbCc_2";
const NextId = "u22CcDdEe-3";
const read = name => readFileSync(join(__dirname, "..", name), "utf8");

test("manifest only accesses YouTube and uses the fixed native messaging ID", () => {
    const manifest = JSON.parse(read("manifest.json"));
    assert.equal(manifest.manifest_version, 3);
    assert.deepEqual(manifest.permissions, ["nativeMessaging"]);
    assert.deepEqual(manifest.host_permissions, ["https://www.youtube.com/*"]);
    assert.deepEqual(manifest.content_scripts[0].matches, manifest.host_permissions);
    const digest = createHash("sha256").update(Buffer.from(manifest.key, "base64")).digest("hex");
    const extensionId = digest.slice(0, 32).replace(/./g, digit => String.fromCharCode(97 + parseInt(digit, 16)));
    assert.equal(extensionId, "jafenianfjmlnaiepgbjeabccfdflpfd");
});

function workerHarness() {
    const listeners = {};
    const packets = [];
    const requests = [];
    let now = 1700000000000;
    let connections = 0;
    let poll;
    const hook = event => ({ addListener: fn => { listeners[event] = fn; } });
    const chrome = {
        runtime: {
            onMessage: hook("message"),
            connectNative: name => {
                assert.equal(name, "com.sada.youtube");
                connections++;
                return { onDisconnect: hook("disconnected"), postMessage: packet => packets.push(packet) };
            }
        },
        tabs: {
            onUpdated: hook("updated"), onRemoved: hook("removed"),
            sendMessage: (tabId, message) => { requests.push([tabId, message.type]); return Promise.resolve(); }
        }
    };
    vm.runInNewContext(read("background.js"), { chrome, Date: { now: () => now }, URL, setInterval: fn => { poll = fn; } });
    return { listeners, packets, requests, poll, clock: n => { now += n; }, connections: () => connections };
}

function sender(videoId = TrackId, tabId = 51) {
    return { tab: { id: tabId }, url: `https://www.youtube.com/watch?v=${videoId}`, frameId: 0 };
}

function playback(overrides = {}) {
    return { type: "playback", videoId: TrackId, title: "Artist - Song", position: 17,
        duration: 210, rate: 1, playing: true, artist: "", album: "", ...overrides };
}

test("worker validates the sender URL, route identity, tab, title and playback clock", () => {
    const { listeners, packets } = workerHarness();
    const report = listeners.message;
    report(playback(), { ...sender(), url: `https://www.youtube.com.example.org/watch?v=${TrackId}` });
    report(playback(), { ...sender(), frameId: 1 });
    report(playback(), sender(NextId));
    report(playback({ duration: Infinity }), sender());
    report(playback({ position: 220 }), sender());
    report(playback({ rate: 0 }), sender());
    report(playback({ title: " " }), sender());
    assert.equal(packets.length, 0);

    report(playback({ tabId: 666, sequence: 80, unrelatedField: "discard" }), sender());
    assert.equal(packets.length, 1);
    assert.deepEqual(JSON.parse(JSON.stringify(packets[0])), {
        ...playback(), tabId: 51, sequence: 1700000000000000
    });
});

test("worker keeps distinct tabs and reports end of playback when leaving or closing a tab", () => {
    const { listeners, packets } = workerHarness();
    listeners.message(playback(), sender());
    listeners.message(playback(), sender(TrackId, 54));
    listeners.updated(51, { url: `${sender().url}&t=30` });
    assert.equal(packets.length, 2);
    listeners.updated(51, { url: sender(NextId).url });
    listeners.removed(51);
    listeners.removed(54);
    assert.deepEqual(packets.map(({ type, tabId }) => [type, tabId]),
        [["playback", 51], ["playback", 54], ["closed", 51], ["closed", 54]]);
    assert.ok(packets.every((packet, i) => i === 0 || packet.sequence > packets[i - 1].sequence));
});

test("worker throttles native reconnect and resumes forwarding with a new report", () => {
    const { listeners, packets, clock, connections } = workerHarness();
    listeners.message(playback(), sender());
    listeners.disconnected();
    listeners.message(playback(), sender());
    assert.equal(connections(), 1);
    assert.equal(packets.length, 1);
    clock(3000);
    listeners.message(playback({ position: 29 }), sender());
    assert.equal(connections(), 2);
    assert.equal(packets[1].position, 29);
});

test("worker requests a report when a known tab stops sending heartbeats", () => {
    const { listeners, requests, clock, poll } = workerHarness();
    listeners.message(playback({ playing: false }), sender());
    poll();
    assert.deepEqual(requests, []);
    clock(2000);
    poll();
    assert.deepEqual(requests, [[51, "request-playback"]]);
    listeners.removed(51);
    poll();
    assert.equal(requests.length, 1);
});

test("worker opens the native port for a preroll only on its exact watch URL", () => {
    const { listeners, packets, connections } = workerHarness();
    const advert = { type: "closed", videoId: TrackId, ad: true };
    listeners.message({ type: "closed" }, sender());
    listeners.message(advert, sender(NextId));
    listeners.message(advert, { ...sender(), url: "https://www.youtube.com/" });
    assert.equal(connections(), 0);
    listeners.message(advert, sender());
    assert.equal(connections(), 1);
    assert.deepEqual(JSON.parse(JSON.stringify(packets[0])), {
        type: "closed", tabId: 51, sequence: 1700000000000000
    });
});

function watchHarness(initialAd = false) {
    class HTMLMediaElement {
        static HAVE_METADATA = 1;
        static HAVE_FUTURE_DATA = 3;
        matches() { return true; }
    }
    const packets = [];
    const listeners = {};
    let classChanged;
    class MutationObserver {
        constructor(fn) { classChanged = fn; }
        observe() {}
        disconnect() {}
    }
    const video = Object.assign(new HTMLMediaElement(), {
        readyState: 4, ended: false, paused: false, seeking: false, duration: 210, currentTime: 17, playbackRate: 1
    });
    const page = { id: TrackId, title: "Artist - Song", ad: initialAd };
    const location = { pathname: "/watch", search: `?v=${TrackId}` };
    const metadata = { title: page.title, artist: "Channel name", album: "" };
    const navigator = { mediaSession: { metadata } };
    const player = { matches: selector => selector.startsWith(".ad-showing") && page.ad, querySelector: () => video };
    const watch = { querySelector: selector => selector === "#movie_player" ? player : { textContent: page.title } };
    const document = {
        querySelector: selector => selector === `ytd-watch-flexy[video-id="${page.id}"]` ? watch : null,
        addEventListener: (event, fn) => { listeners[event] = fn; }
    };
    const chrome = { runtime: {
        id: "test-extension",
        onMessage: { addListener: fn => { listeners.message = fn; } },
        sendMessage: packet => { packets.push(packet); return Promise.resolve(); }
    } };
    let heartbeat;
    let now = 0;
    vm.runInNewContext(read("watch.js"), {
        URLSearchParams, location, navigator, document, chrome, HTMLMediaElement, MutationObserver,
        window: { addEventListener: document.addEventListener }, performance: { now: () => now },
        setInterval: fn => { heartbeat = fn; }, setTimeout: fn => fn()
    });
    return { packets, listeners, page, location, video, metadata, classChanged,
        tick: () => { now += 750; heartbeat(); } };
}

test("watch page uses actual pause, seeking, buffering and playback rate", () => {
    const { packets, listeners, video, tick } = watchHarness();
    assert.equal(packets[0].playing, true);
    assert.equal(packets[0].artist, "");
    video.paused = true;
    listeners.pause({ target: video });
    assert.equal(packets.at(-1).playing, false);
    video.paused = false;
    video.seeking = true;
    video.currentTime = 92;
    listeners.seeking({ target: video });
    assert.equal(packets.at(-1).position, 92);
    assert.equal(packets.at(-1).playing, false);
    video.seeking = false;
    video.playbackRate = 1.5;
    listeners.seeked({ target: video });
    assert.equal(packets.at(-1).rate, 1.5);
    assert.equal(packets.at(-1).playing, true);
    listeners.waiting({ target: video });
    assert.equal(packets.at(-1).playing, false);
    tick();
    assert.equal(packets.at(-1).playing, false);
    listeners.playing({ target: video });
    assert.equal(packets.at(-1).playing, true);
});

test("watch page closes ad or stale player and follows in-page navigation", () => {
    const { packets, listeners, page, location, video, tick, classChanged } = watchHarness();
    page.ad = true;
    classChanged();
    assert.equal(packets.at(-1).type, "closed");
    tick();
    assert.deepEqual(packets.map(packet => packet.type), ["playback", "closed"]);
    page.ad = false;
    tick();
    assert.equal(packets.at(-1).videoId, TrackId);
    listeners["yt-navigate-start"]();
    location.search = `?v=${NextId}`;
    listeners["yt-navigate-finish"]();
    tick();
    assert.equal(packets.at(-1).type, "closed");
    page.id = NextId;
    page.title = "New artist - New song";
    video.currentTime = 1.2;
    tick();
    assert.equal(packets.at(-1).videoId, NextId);
    assert.equal(packets.at(-1).position, 1.2);
    listeners.pagehide();
    assert.equal(packets.at(-1).type, "closed");
});

test("watch page reports a single preroll closure before its first playback", () => {
    const { packets, page, tick, classChanged } = watchHarness(true);
    tick();
    classChanged();
    assert.deepEqual(JSON.parse(JSON.stringify(packets)), [{ type: "closed", ad: true, videoId: TrackId }]);
    page.ad = false;
    classChanged();
    assert.equal(packets.at(-1).type, "playback");
});
