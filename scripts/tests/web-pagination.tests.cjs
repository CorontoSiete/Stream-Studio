'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/web-player-fixture.cjs');

function pages(responses) {
  const f = fixture({ onRequest: (url, input) => {
    if (url !== '/api/vods') return;
    const response = responses.shift();
    assert.ok(response, 'unexpected page request');
    assert.equal(input.cursor, response.cursor);
    if (response.error) throw new Error(response.error);
    return { ok: true, text: async () => JSON.stringify({
      items: response.keys.map(key => ({ key, title: key })), nextCursor: response.next,
      available: response.available, message: response.message
    }) };
  } });
  f.state.page = 'vods';
  f.state.vodChannel = { platform: 'kick', channel: 'streamer' };
  return f;
}

test('browser pagination deduplicates each page and stops repeated cursor cycles', async () => {
  const f = pages([
    { cursor: '', keys: ['one', 'one'], next: 'a' },
    { cursor: 'a', keys: ['one', 'two', 'two'], next: 'b' },
    { cursor: 'b', keys: ['three'], next: 'a' },
    { cursor: '', keys: ['fresh'], next: 'a' }
  ]);
  try {
    await f.load();
    assert.deepEqual(Array.from(f.state.items, item => item.key), ['one']);
    await f.load(true);
    assert.deepEqual(Array.from(f.state.items, item => item.key), ['one', 'two']);
    await f.load(true);
    assert.equal(f.state.cursor, '');
    assert.equal(f.document.querySelector('#more').hidden, true);
    await f.load();
    assert.deepEqual(Array.from(f.state.items, item => item.key), ['fresh']);
    assert.equal(f.state.cursor, 'a', 'refresh starts a new pagination history');
  } finally { f.disconnect(); }
});

test('a failed browser page keeps Load more available for retry', async () => {
  const f = pages([
    { cursor: '', keys: ['one'], next: 'a' },
    { cursor: 'a', error: 'Provider temporarily unavailable.' },
    { cursor: 'a', keys: ['two'], next: 'a' }
  ]);
  try {
    await f.load();
    await f.load(true);
    assert.equal(f.state.cursor, 'a');
    assert.equal(f.document.querySelector('#more').hidden, false);
    await f.load(true);
    assert.deepEqual(Array.from(f.state.items, item => item.key), ['one', 'two']);
    assert.equal(f.state.cursor, '', 'a page pointing to itself ends pagination');
    assert.equal(f.document.querySelector('#more').hidden, true);
  } finally { f.disconnect(); }
});

test('provider failures reported in a successful response preserve cards and pagination', async () => {
  const failure = { keys: [], next: '', available: false, message: 'Provider temporarily unavailable.' };
  const f = pages([
    { cursor: '', keys: ['one'], next: 'a', available: true },
    { ...failure, cursor: '' },
    { ...failure, cursor: 'a' },
    { cursor: 'a', keys: ['two'], next: '', available: true }
  ]);
  try {
    await f.load();
    await f.load();
    await f.load(true);
    assert.deepEqual(Array.from(f.state.items, item => item.key), ['one']);
    assert.equal(f.state.cursor, 'a');
    assert.equal(f.document.querySelector('#more').hidden, false);
    assert.equal(f.document.querySelector('#notice').children[0]?.textContent, failure.message);
    await f.load(true);
    assert.deepEqual(Array.from(f.state.items, item => item.key), ['one', 'two']);
  } finally { f.disconnect(); }
});

test('provider cursor cycles end pagination even when every page has new items', async () => {
  const f = pages([
    { cursor: '', keys: ['one'], next: 'a' },
    { cursor: 'a', keys: ['two'], next: 'b' },
    { cursor: 'b', keys: ['three'], next: 'a' }
  ]);
  try {
    await f.load(); await f.load(true); await f.load(true);
    assert.equal(f.state.cursor, '');
    assert.equal(f.document.querySelector('#more').hidden, true);
    assert.deepEqual(Array.from(f.state.items, item => item.key), ['one', 'two', 'three']);
  } finally { f.disconnect(); }
});

test('a failed refresh keeps the displayed page and its next cursor usable', async () => {
  const f = pages([
    { cursor: '', keys: ['one'], next: 'a' },
    { cursor: '', error: 'Provider temporarily unavailable.' },
    { cursor: 'a', keys: ['two'], next: '' }
  ]);
  try {
    await f.load();
    await f.load();
    assert.deepEqual(Array.from(f.state.items, item => item.key), ['one']);
    assert.equal(f.document.querySelector('#result-count').textContent, '1 result');
    assert.equal(f.document.querySelector('#more').hidden, false);
    await f.load(true);
    assert.deepEqual(Array.from(f.state.items, item => item.key), ['one', 'two']);
  } finally { f.disconnect(); }
});

test('a failed refresh preserves pagination through an empty first page', async () => {
  const f = pages([
    { cursor: '', keys: [], next: 'a' },
    { cursor: '', error: 'Provider temporarily unavailable.' },
    { cursor: 'a', keys: ['one'], next: '' }
  ]);
  try {
    await f.load();
    await f.load();
    assert.equal(f.document.querySelector('#result-count').textContent, '0 results');
    assert.equal(f.document.querySelector('#more').hidden, false);
    await f.load(true);
    assert.deepEqual(Array.from(f.state.items, item => item.key), ['one']);
  } finally { f.disconnect(); }
});

test('Discover requests the next page with the query that produced its cursor', async () => {
  const requests = [];
  const f = fixture({ onRequest: (url, input) => {
    if (url !== '/api/categories') return;
    requests.push(input);
    if (requests.length === 2) throw new Error('Provider temporarily unavailable.');
    const first = !input.cursor;
    const name = first ? 'one' : 'two';
    return { ok: true, text: async () => JSON.stringify({ items: [{ id: name, name }], nextCursor: first ? 'next' : '' }) };
  } });
  f.state.page = 'discover';
  const query = f.document.querySelector('#query');
  try {
    query.value = 'original';
    await f.load();
    query.value = 'edited';
    await f.load();
    await f.load(true);
    assert.deepEqual(requests.map(({ query, cursor }) => [query, cursor]), [
      ['original', ''], ['edited', ''], ['original', 'next']
    ]);
    assert.deepEqual(Array.from(f.state.items, item => item.id), ['one', 'two']);
  } finally { f.disconnect(); }
});

test('a failed Discover refresh resumes viewer counts for the retained categories', async () => {
  let pages = 0;
  const countRequests = [];
  const f = fixture({ onRequest: (url, input, { signal }) => {
    if (url === '/api/categories') {
      if (++pages > 1) throw new Error('Provider temporarily unavailable.');
      return { ok: true, text: async () => JSON.stringify({ items: [{ id: 'first', name: 'First category' }] }) };
    }
    if (url === '/api/categories/viewers') {
      countRequests.push(signal);
      if (countRequests.length === 1) return new Promise(resolve => signal.addEventListener('abort', () => resolve(null), { once: true }));
      return { ok: true, text: async () => JSON.stringify({ available: true, items: [{ categoryId: input.categoryIds[0], viewerCount: 23 }] }) };
    }
  } });
  try {
    await f.showPage('discover', { platform: 'twitch' });
    const category = f.state.items[0];
    assert.equal(countRequests.length, 1);
    await f.load();
    assert.equal(countRequests[0].aborted, true);
    assert.equal(countRequests.length, 2);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(category.viewerCount, 23);
  } finally { f.disconnect(); }
});

test('different downloads of the same VOD retain their individual library entries', async () => {
  const f = fixture({ onRequest: url => {
    if (url === '/api/downloads') return { ok: true, text: async () => JSON.stringify({ items: [
      { id: 'high', key: 'same-vod', title: '1080p copy', state: 'Completed' },
      { id: 'low', key: 'same-vod', title: '720p copy', state: 'Completed' }
    ] }) };
  } });
  try {
    f.state.page = 'downloads';
    await f.load();
    assert.deepEqual(Array.from(f.state.items, item => item.id), ['high', 'low']);
  } finally { f.disconnect(); }
});
