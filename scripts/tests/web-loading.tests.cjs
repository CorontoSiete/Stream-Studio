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
