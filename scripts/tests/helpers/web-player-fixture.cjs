'use strict';
// Run the full web application against deterministic DOM, media and server adapters.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.resolve(__dirname, '../../../src/StreamlinkVlcStudio.WebClient/Assets/app.js'), 'utf8');

function fixture({ storage = new Map(), volumes = new Map(), layouts = new Map(), media = 'hls', userAgent = 'Desktop', onRequest, now = Date.now, code = source } = {}) {
  const pendingEvents = [];
  class Element extends EventTarget {
    constructor() {
      super();
      this.elements = new Map(); this.children = []; this.dataset = {}; this.value = ''; this.checked = false;
      const classes = new Set();
      this.classList = { add: (...items) => items.forEach(item => classes.add(item)), remove: (...items) => items.forEach(item => classes.delete(item)),
        contains: item => classes.has(item), toggle: (item, active) => active ? classes.add(item) : classes.delete(item) };
      this.style = { setProperty() {} }; this.clientWidth = 1440; this.clientHeight = 810;
    }
    querySelector(selector) {
      if (!this.elements.has(selector)) this.elements.set(selector, selector === 'video' ? new Video() : new Element());
      return this.elements.get(selector);
    }
    querySelectorAll() { return []; }
    get childElementCount() { return this.children.length; }
    append(...elements) { for (const element of elements) this.insertBefore(element, null); }
    prepend(...elements) { const first = this.children[0] || null; for (const element of elements) this.insertBefore(element, first); }
    insertBefore(element, before) {
      if (element === before) return;
      element.remove?.();
      if (element && typeof element === 'object') element.parentElement = this;
      this.children.splice(before === null ? this.children.length : this.children.indexOf(before), 0, element);
    }
    replaceChildren(...elements) { for (const child of [...this.children]) child.remove?.(); this.children = []; this.append(...elements); }
    contains(element) { for (; element; element = element.parentElement) if (element === this) return true; return false; }
    remove() {
      if (this.parentElement) {
        if (this.contains(document.activeElement)) document.activeElement = null;
        this.parentElement.children = this.parentElement.children.filter(element => element !== this);
        this.parentElement = null;
      }
    }
    cloneNode() { return new Element(); }
    setAttribute() {}
    removeAttribute() {}
    focus() { document.activeElement = this; }
    close() {}
    scrollIntoView() {}
    getBoundingClientRect() { return { left: 0, top: 0, width: this.clientWidth, height: this.clientHeight }; }
  }
  class Video extends Element {
    constructor() { super(); this.level = 1; this.silent = false; this.paused = true; this.ended = false; this.defaultPlaybackRate = this.playbackRate = 1; this.duration = 100; this.currentTime = 0; this.attachedVolumes = []; this.seekable = this.buffered = { length: 0 }; }
    get volume() { return this.level; }
    set volume(value) {
      assert.ok(Number.isFinite(value) && value >= 0 && value <= 1);
      if (this.level !== value) { this.level = value; pendingEvents.push(() => this.dispatchEvent(new Event('volumechange'))); }
    }
    get muted() { return this.silent; }
    set muted(value) { if (this.silent !== value) { this.silent = value; pendingEvents.push(() => this.dispatchEvent(new Event('volumechange'))); } }
    set src(value) { this.url = value; this.playbackRate = this.defaultPlaybackRate; this.attachedVolumes.push(this.volume); }
    canPlayType() { return media === 'native' || media === 'both' ? 'probably' : ''; }
    play() { this.firstPlayVolume ??= this.volume; this.paused = false; return Promise.resolve(); }
    pause() { this.paused = true; }
    load() {}
  }
  class Hls {
    static isSupported() { return media === 'hls' || media === 'both'; }
    static Events = { ERROR: 'error', MANIFEST_PARSED: 'manifestParsed', LEVEL_UPDATED: 'levelUpdated', BUFFER_APPENDED: 'bufferAppended', BUFFER_FLUSHED: 'bufferFlushed' };
    constructor(config) { this.config = config; this.listeners = new Map(); this.levels = [{ height: 0 }, { height: 360 }]; }
    loadSource(url) { this.url = url; }
    attachMedia(video) { video.playbackRate = video.defaultPlaybackRate; video.attachedVolumes.push(video.volume); }
    on(event, callback) { this.listeners.set(event, callback); }
    emit(event) { this.listeners.get(event)?.(event, {}); }
    startLoad(position) { this.startPosition = position; this.loading = true; }
    stopLoad() { this.loading = false; }
    destroy() {}
  }
  const document = new Element();
  document.createElement = tag => tag === 'video' ? new Video() : new Element();
  document.createElementNS = document.createTextNode = () => new Element();
  document.querySelector('#player-template').content = { firstElementChild: new Element() };
  const localStorage = { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) };
  const players = new Map(), layoutWrites = [], volumeWrites = [], closeCalls = [], requests = [], intervals = [];
  let failLayoutSave = false, failVolumeSave = false, failProgressSave = false;
  const layoutKey = p => p.platform.trim().toLowerCase() + ':' + p.channel.trim().toLowerCase();
  const volumeKey = p => p.platform.trim() + ':' + p.channel.trim().toLowerCase();
  let opened = 0, target = { channel: 'streamer', platform: 'Twitch', isVod: false, isMp4: false };
  const context = vm.createContext({ document, localStorage, Event, URL, URLSearchParams, Option: Element, Hls, AbortController, DOMException, setTimeout, clearTimeout, Date: class extends Date { static now() { return now(); } },
    navigator: { userAgent, platform: 'Win32', maxTouchPoints: 0 },
    ResizeObserver: class { observe() {} disconnect() {} },
    window: Object.assign(new EventTarget(), { Hls, scrollTo() {} }), location: { hash: '', pathname: '/', search: '' },
    setInterval(callback, delay) { intervals.push({ callback, delay }); },
    fetch: async (url, options) => {
      const input = JSON.parse(options.body || '{}');
      requests.push({ url, input, keepalive: options.keepalive });
      const response = onRequest && await onRequest(url, input, options);
      if (response) return response;
      let result = {};
      if (url === '/api/open') {
        result = { ...target, id: `player-${++opened}`, key: `key-${opened}`, title: 'Display title',
          chatLayout: layouts.get(layoutKey(target)) ?? null, url: '/media/fixture', quality: input.quality, resume: 0 };
        players.set(result.id, { ...target });
      }
      if (url === '/api/chat/layout') {
        assert.equal(options.keepalive, true, 'a dispatched save can finish during page navigation');
        layoutWrites.push(input);
        if (failLayoutSave) return { ok: false, status: 500, json: async () => ({ error: 'Disk full' }) };
        const resolved = players.get(input.player); assert.ok(resolved, 'save is bound to an open server player');
        layouts.set(layoutKey(resolved), input.layout);
        result = { saved: true };
      }
      if (url === '/api/volume/save') {
        assert.equal(options.keepalive, true);
        const resolved = players.get(input.player); assert.ok(resolved, 'volume belongs to an open server player');
        volumeWrites.push(input);
        if (failVolumeSave) return { ok: false, status: 503, json: async () => ({ error: 'Volume save failed.' }) };
        volumes.set(volumeKey(resolved), input.volume);
        result = { saved: true };
      }
      if (url === '/api/progress' && failProgressSave)
        return { ok: false, status: 503, json: async () => ({ error: 'Progress save failed.' }) };
      if (url === '/api/replay') result = { available: true, key: 'replay-key' };
      if (url === '/api/close') { closeCalls.push(input.player); players.delete(input.player); }
      return { ok: true, text: async () => JSON.stringify(result) };
    }
  });
  // Only replace startup authentication. All opening, replacement, closing, audio
  // selection, event handlers and persistence run from the actual application.
  assert.ok(code.includes('  restoreSession();'));
  vm.runInContext(code.replace('  restoreSession();', '  globalThis.playerTest = { state, load, showPage, openPlayer, closePlayer, playerAction, selectPlayer, cyclePlayer, disconnect, flushVolumes, storeChatGeometry, resizeChat, positionChat, chatGeometry, normalizeHotkey, hotkeyFromEvent, loadHotkeys, setPreferences: value => Object.assign(preferences, value), seedVolumes: values => { for (const [key, volume] of values) streamVolumes.set(key, volume); }, flush: () => queue };'), context);
  const app = context.playerTest;
  app.state.authenticated = true;
  app.state.settings = { defaultQuality: 'best', qualities: ['best', '720p'], lowLatency: true };
  app.seedVolumes(volumes);
  return { ...app, storage, volumes, volumeWrites, layouts, layoutWrites, closeCalls, requests, localStorage, document, window: context.window,
    tick: async delay => { for (const interval of intervals) if (interval.delay === delay) await interval.callback(); },
    failSave: value => { failLayoutSave = value; },
    failVolumeSave: value => { failVolumeSave = value; },
    failProgressSave: value => { failProgressSave = value; },
    target: value => { target = { ...target, ...value }; },
    open: (input = {}) => app.openPlayer({ input: `${target.platform}:${target.channel}:${target.isVod ? 'vod' : 'live'}`, ...input }),
    events: async () => { while (pendingEvents.length) pendingEvents.shift()(); await app.flushVolumes(); }
  };
}

module.exports = { fixture, source };
