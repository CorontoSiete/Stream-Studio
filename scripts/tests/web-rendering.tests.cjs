'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/web-player-fixture.cjs');

const json = items => ({ ok: true, text: async () => JSON.stringify({ items, nextCursor: 'next' }) });
const channel = (key, title = key) => ({ key, channel: key, title, platform: 'Twitch', state: 'Live', thumbnailUrl: 'https://example.com/' + key + '.jpg' });

test('Load more creates only new cards and preserves existing controls and artwork', async () => {
  let page = 0;
  const f = fixture({ onRequest: url => url === '/api/following' ? json(page++ ? [channel('two'), channel('three')] : [channel('one'), channel('two')]) : undefined });
  try {
    await f.load();
    const cards = f.document.querySelector('#cards');
    const original = [...cards.children];
    const control = original[0].children[0];
    const artwork = control.children[0].children[0];
    control.disabled = true;
    await f.load(true);
    assert.equal(cards.children.length, 3);
    assert.equal(cards.children[0], original[0]);
    assert.equal(cards.children[1], original[1]);
    assert.equal(cards.children[0].children[0], control);
    assert.equal(control.children[0].children[0], artwork);
    assert.equal(control.disabled, true, 'rendering must preserve an in-flight control');
  } finally { f.disconnect(); }
});

test('an unchanged refresh reuses cards while changed metadata replaces only its card', async () => {
  let items = [channel('one'), channel('two')];
  const f = fixture({ onRequest: url => url === '/api/following' ? json(items) : undefined });
  try {
    await f.load();
    const cards = f.document.querySelector('#cards');
    const original = [...cards.children];
    await f.load();
    assert.deepEqual(cards.children, original);
    items = [channel('one'), channel('two', 'Updated broadcast')];
    await f.load();
    assert.equal(cards.children[0], original[0]);
    assert.notEqual(cards.children[1], original[1]);
    assert.equal(original[1].parentElement, null);
  } finally { f.disconnect(); }
});

test('Following labels live and offline groups and keeps retained cards through filtering', async () => {
  const live = channel('live');
  const offline = { ...channel('offline'), state: 'Offline' };
  const unavailable = { ...channel('unavailable'), state: 'Unavailable' };
  const f = fixture({ onRequest: url => url === '/api/following' ? json([unavailable, live, offline]) : undefined });
  try {
    await f.load();
    const cards = f.document.querySelector('#cards');
    const liveHeading = f.document.querySelector('#live-heading');
    assert.equal(liveHeading.hidden, false);
    const [liveCard, offlineHeading, firstOffline, secondOffline] = cards.children;
    assert.equal(liveCard.dataset.key, 'live');
    assert.equal(offlineHeading.textContent, 'Offline channels');
    assert.deepEqual([firstOffline.dataset.key, secondOffline.dataset.key], ['unavailable', 'offline']);

    const filter = f.document.querySelector('#live-filter');
    filter.checked = true;
    filter.dispatchEvent(new Event('change'));
    assert.deepEqual(cards.children, [liveCard]);

    filter.checked = false;
    filter.dispatchEvent(new Event('change'));
    assert.equal(cards.children[0], liveCard);
    assert.equal(cards.children[1], offlineHeading);
    await f.showPage('downloads');
    assert.equal(liveHeading.hidden, true);
    assert.equal(offlineHeading.parentElement, null);
  } finally { f.disconnect(); }
});

test('refresh reorders retained cards and removes cards absent from the new response', async () => {
  let items = [channel('one'), channel('two'), channel('three')];
  const f = fixture({ onRequest: url => url === '/api/following' ? json(items) : undefined });
  try {
    await f.load();
    const cards = f.document.querySelector('#cards');
    const original = [...cards.children];
    original[2].children[0].focus();
    items = [channel('three'), channel('one')];
    await f.load();
    assert.deepEqual(cards.children, [original[2], original[0]]);
    assert.equal(original[1].parentElement, null);
    assert.equal(f.document.activeElement, original[2].children[0], 'moving a retained card restores its focused control');
  } finally { f.disconnect(); }
});

test('refresh retries failed artwork without replacing retained controls or healthy images', async () => {
  const f = fixture({ onRequest: url => url === '/api/following' ? json([channel('one')]) : undefined });
  try {
    await f.load();
    const cards = f.document.querySelector('#cards');
    const card = cards.children[0], control = card.children[0], media = control.children[0];
    const failed = media.children[0], badge = media.children[1];
    control.disabled = true;
    failed.dispatchEvent(new Event('error'));
    assert.equal(failed.parentElement, null);
    const placeholder = media.children[0];

    await f.load();
    assert.equal(cards.children[0], card);
    assert.equal(card.children[0], control);
    assert.equal(control.disabled, true);
    const retried = media.children[0];
    assert.notEqual(retried, placeholder, 'refresh retries a failed image even when metadata is unchanged');
    assert.notEqual(retried, failed);
    assert.equal(retried.src, 'https://example.com/one.jpg');
    assert.equal(placeholder.parentElement, null);
    assert.equal(media.children[1], badge, 'retry preserves badges and the rest of the media controls');
    await f.load();
    assert.equal(media.children[0], retried, 'pending and healthy images are reused');
  } finally { f.disconnect(); }
});

test('a card with the same key on another page receives that page\'s controls', async () => {
  const item = { ...channel('one'), id: 'one', state: 'Completed' };
  const f = fixture({ onRequest: url => ['/api/following', '/api/downloads'].includes(url) ? json([item]) : undefined });
  try {
    await f.load();
    const cards = f.document.querySelector('#cards');
    const original = cards.children[0];
    f.state.page = 'downloads';
    await f.load();
    assert.notEqual(cards.children[0], original);
    assert.equal(cards.children[0].children[1].className, 'download-progress');
  } finally { f.disconnect(); }
});
