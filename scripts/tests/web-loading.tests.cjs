'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/web-player-fixture.cjs');

const json = result => ({ ok: true, text: async () => JSON.stringify(result) });
const turn = () => new Promise(resolve => setImmediate(resolve));

test('leaving a slow directory cancels its request and loads the selected page', async () => {
  let signal;
  const f = fixture({ onRequest: (url, _, options) => {
    if (url === '/api/following') return new Promise((_, reject) => {
      signal = options.signal;
      signal?.addEventListener('abort', () => reject(new DOMException('Canceled', 'AbortError')), { once: true });
    });
    if (url === '/api/downloads') return json({ items: [{ id: 'saved', title: 'Saved video', state: 'Completed' }] });
  } });
  try {
    const obsolete = f.load();
    await turn();
    const current = f.showPage('downloads');
    assert.equal(signal?.aborted, true, 'navigation must release the serialized request queue');
    await current;
    await obsolete;
    assert.equal(f.state.items[0].id, 'saved');
    assert.equal(f.state.page, 'downloads');
    assert.equal(f.state.busy, false);
  } finally { f.disconnect(); }
});

test('rapid navigation discards queued page reads before sending them', async () => {
  let release;
  const f = fixture({ onRequest: url => {
    if (url === '/api/open') return new Promise(resolve => { release = () => resolve(null); });
  } });
  try {
    const opening = f.open();
    await turn();
    const first = f.showPage('discover');
    const second = f.showPage('recent');
    const last = f.showPage('downloads');
    release();
    await Promise.all([opening, first, second, last]);
    assert.equal(f.requests.some(request => request.url === '/api/categories'), false);
    assert.equal(f.requests.some(request => request.url === '/api/recent'), false);
    assert.equal(f.requests.filter(request => request.url === '/api/downloads').length, 1);
    assert.equal(f.state.busy, false);
  } finally { release?.(); f.disconnect(); }
});

for (const destination of ['settings', 'disconnect']) test(`${destination} cancels an active page read`, async () => {
  let signal;
  const f = fixture({ onRequest: (url, _, options) => {
    if (url === '/api/following') return new Promise((_, reject) => {
      signal = options.signal;
      signal?.addEventListener('abort', () => reject(new DOMException('Canceled', 'AbortError')), { once: true });
    });
  } });
  try {
    const pending = f.load();
    await turn();
    if (destination === 'settings') await f.showPage('settings'); else f.disconnect();
    assert.equal(signal?.aborted, true);
    await pending;
    assert.equal(f.state.items.length, 0);
  } finally { f.disconnect(); }
});

test('navigation cancels a busy response retry without holding the next page in the queue', async ({ mock }) => {
  mock.timers.enable({ apis: ['setTimeout'] });
  const f = fixture({ onRequest: url => {
    if (url === '/api/following') return { status: 429, ok: false, text: async () => '' };
    if (url === '/api/downloads') return json({ items: [{ id: 'saved', state: 'Completed' }] });
  } });
  try {
    const oldPage = f.load();
    await turn();
    const nextPage = f.showPage('downloads');
    await turn();
    assert.equal(f.state.items[0]?.id, 'saved', 'the new page loads without advancing the retry timer');
    await Promise.all([oldPage, nextPage]);
    assert.equal(f.requests.filter(request => request.url === '/api/following').length, 1);
  } finally {
    f.disconnect();
    mock.timers.tick(1000);
  }
});

test('selecting the current page preserves its filters and cards until explicit refresh', async () => {
  const f = fixture({ onRequest: url => url === '/api/following' ? json({ items: [] }) : undefined });
  try {
    f.document.querySelector('#studio').dataset.page = 'following';
    f.document.querySelector('#query').value = 'favorite';
    f.document.querySelector('#live-filter').checked = true;
    const card = { id: 'channel', state: 'Live' };
    f.state.items = [card];
    await f.showPage('following');
    assert.equal(f.requests.length, 0);
    assert.equal(f.document.querySelector('#query').value, 'favorite');
    assert.equal(f.document.querySelector('#live-filter').checked, true);
    assert.equal(f.state.items[0], card);
    await f.load();
    assert.equal(f.requests.filter(request => request.url === '/api/following').length, 1);
    assert.equal(f.state.items.length, 0);
  } finally { f.disconnect(); }
});

test('a canceled provider that returns late cannot replace the selected page', async () => {
  let release;
  const f = fixture({ onRequest: url => {
    if (url === '/api/following') return new Promise(resolve => { release = () => resolve(json({ items: [{ id: 'old' }] })); });
    if (url === '/api/downloads') return json({ items: [{ id: 'new', state: 'Completed' }] });
  } });
  try {
    const old = f.load();
    await turn();
    const current = f.showPage('downloads');
    release();
    await Promise.all([old, current]);
    assert.equal(f.state.items.length, 1);
    assert.equal(f.state.items[0].id, 'new');
  } finally { release?.(); f.disconnect(); }
});

test('Discover and other notices expire, including after replacement or manual dismissal', async ({ mock }) => {
  mock.timers.enable({ apis: ['setTimeout'] });
  const message = 'Loaded 50 categories from Twitch.';
  const f = fixture({ onRequest: url => url === '/api/categories'
    ? json({ items: [{ id: '1', name: 'Category' }], message })
    : undefined });
  const box = f.document.querySelector('#notice');
  try {
    await f.showPage('discover');
    assert.equal(box.children[0]?.textContent, message);
    mock.timers.tick(4999);
    assert.equal(box.hidden, false);
    mock.timers.tick(1);
    assert.equal(box.hidden, true);
    assert.equal(box.childElementCount, 0);

    await f.load();
    mock.timers.tick(4000);
    const channelUrl = 'https://www.twitch.tv/streamer';
    await f.playerAction({ channelUrl }, 'share');
    mock.timers.tick(1000);
    assert.equal(box.hidden, false, 'the replaced Discover notice cannot dismiss the share notice');
    assert.equal(box.children[0]?.textContent, channelUrl);
    box.children[1].dispatchEvent(new Event('click'));
    assert.equal(box.hidden, true);

    await f.load();
    mock.timers.tick(4000);
    assert.equal(box.hidden, false, 'the dismissed share notice cannot dismiss the new Discover notice');
    mock.timers.tick(1000);
    assert.equal(box.hidden, true);
  } finally { f.disconnect(); }
});

test('repeated background errors let a notification expire and stay dismissed between reminders', async ({ mock }) => {
  mock.timers.enable({ apis: ['setTimeout'] });
  const f = fixture();
  const box = f.document.querySelector('#notice');
  f.target({ isVod: true });
  try {
    const player = await f.open();
    f.failProgressSave(true);
    await f.tick(2000);
    assert.equal(box.children[0]?.textContent, 'Progress save failed.');
    assert.equal(box.classList.contains('error'), true);

    mock.timers.tick(6000);
    assert.equal(box.hidden, false, 'errors allow extra time to read');
    player.lastSaved = 0;
    await f.tick(2000);
    mock.timers.tick(4000);
    assert.equal(box.hidden, true, 'a failed background retry does not extend the visible error');

    player.lastSaved = 0;
    await f.tick(2000);
    assert.equal(f.requests.filter(request => request.url === '/api/progress').length, 3);
    assert.equal(box.hidden, true, 'the next retry does not immediately reopen the dismissed error');
  } finally { f.disconnect(); }
});
