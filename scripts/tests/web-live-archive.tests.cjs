'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/web-player-fixture.cjs');
const response = value => ({ ok: true, text: async () => JSON.stringify(value) });
const settle = async f => { await f.flush(); await new Promise(resolve => setImmediate(resolve)); await f.flush(); };
const refresh = p => p.video.dispatchEvent(new Event('progress'));

test('broadcast histories refresh for visible players and catch up on return', async () => {
  let now = Date.now();
  const f = fixture({ now: () => now, onRequest: (url, input) => url === '/api/live/archive'
    ? response({ available: true, key: input.player, duration: 120 }) : null });
  try {
    const first = await f.open();
    await settle(f);
    f.target({ channel: 'second' });
    const second = await f.open();
    await settle(f);
    const archiveCalls = () => f.requests.filter(r => r.url === '/api/live/archive').map(r => r.input.player);
    assert.deepEqual(archiveCalls(), [first.id, second.id]);

    now += 31000;
    await f.tick(250); await settle(f);
    assert.deepEqual(archiveCalls().slice(2), [second.id]);
    assert.equal(first.archive.duration, 120);
    f.selectPlayer(first.id);
    await settle(f);
    assert.deepEqual(archiveCalls().slice(2), [second.id, first.id]);

    await f.showPage('settings');
    now += 31000;
    await f.tick(250); await settle(f);
    assert.equal(archiveCalls().length, 4);
    f.selectPlayer(first.id);
    await settle(f);
    const grid = first.element.querySelector('.multistream-toggle');
    grid.checked = true;
    grid.dispatchEvent(new Event('change'));
    await settle(f);
    assert.deepEqual(archiveCalls().slice(4), [first.id, second.id]);

    f.document.hidden = true;
    f.document.dispatchEvent(new Event('visibilitychange'));
    now += 31000;
    await f.tick(250); await settle(f);
    assert.equal(archiveCalls().length, 6);
    f.document.hidden = false;
    f.document.dispatchEvent(new Event('visibilitychange'));
    await settle(f);
    assert.deepEqual(archiveCalls().slice(6), [first.id, second.id]);
  } finally { f.disconnect(); }
});

test('growing replay chat retries the published tail and finished VOD chat stops', async () => {
  for (const isLiveArchive of [true, false]) {
    let page = { outcome: 'Completed', messages: [], through: 60 };
    const f = fixture({ onRequest: url => url === '/api/chat' ? response(page) : null });
    try {
      f.target({ isVod: true, isLiveArchive });
      const p = await f.open();
      await settle(f);
      const offsets = () => f.requests.filter(r => r.url === '/api/chat').map(r => r.input.from);
      assert.deepEqual(offsets(), [0]);
      if (isLiveArchive) {
        assert.equal(p.chatDone, false);
        assert.ok(p.chatRetryAt > Date.now());
        assert.equal(p.frontier, 0, 'an empty last page cannot prove future coverage');
      } else assert.equal(p.chatDone, true);
      page = { outcome: 'Completed', messages: [{ offset: 2, message: { messageId: 'late', message: 'Hello' } }], through: 3 };
      p.video.currentTime = 40;
      p.nextChatPoll = 0;
      await f.tick(250); await settle(f);
      assert.deepEqual(offsets(), [0]);
      p.chatRetryAt = Date.now() - 1;
      await f.tick(250); await settle(f);
      assert.deepEqual(offsets(), isLiveArchive ? [0, 0] : [0]);
      if (isLiveArchive) {
        assert.equal(p.frontier, 3);
        assert.ok(p.chatRows.has('late'));
      }
    } finally { f.disconnect(); }
  }
});

for (const platform of ['Twitch', 'Kick']) for (const media of ['hls', 'native']) {
  test(`${platform} ${media}: full broadcast history predates opening and starts at the requested position`, async () => {
    const f = fixture({ media, onRequest: url => url === '/api/live/archive' ? response({ available: true, key: 'full-archive', duration: 7200 }) : null });
    try {
      f.target({ platform });
      const live = await f.open();
      live.video.duration = Infinity;
      live.video.currentTime = 25;
      live.video.seekable = { length: 1, start: () => 0, end: () => 30 };
      if (live.hls) live.hls.latestLevelDetails = { live: true, fragmentStart: 0, edge: 30 };
      await settle(f);
      refresh(live);
      const slider = live.element.querySelector('.video-seek');
      assert.equal(Number(slider.min), 0);
      assert.equal(Number(slider.max), 7200, 'uses the archive, not the thirty seconds seen since opening');
      assert.equal(Number(slider.value), 7200, 'live has no fabricated offset into the separate VOD');
      slider.dispatchEvent(Object.assign(new Event('pointerdown'), { button: 0, pointerId: 1 }));
      slider.value = 10;
      slider.dispatchEvent(new Event('input'));
      slider.value = 0;
      slider.dispatchEvent(new Event('input'));
      assert.equal(f.requests.filter(r => r.url === '/api/open').length, 1, 'dragging previews without repeatedly opening players');
      f.target({ isVod: true, isLiveArchive: true });
      slider.dispatchEvent(new Event('change'));
      await settle(f);
      assert.equal(f.requests.filter(r => r.url === '/api/open').length, 1, 'a premature change must not commit before release');
      f.window.dispatchEvent(Object.assign(new Event('pointerup'), { pointerId: 1 }));
      await new Promise(resolve => setTimeout(resolve, 0));
      await settle(f);
      const archive = f.state.players[0];
      assert.equal(archive.isLiveArchive, true);
      assert.equal(archive.resume, 0, 'zero is an explicit seek and overrides any bookmark');
      assert.equal(archive.video.paused, false);
      assert.equal(live.closed, true);
      assert.equal(f.requests.filter(r => r.url === '/api/open').length, 2);
      assert.equal(f.requests.findLast(r => r.url === '/api/open').input.key, 'full-archive');
      if (archive.hls) {
        assert.equal(archive.hls.config.startPosition, 0);
        assert.equal(archive.hls.config.liveDurationInfinity, true);
        assert.equal(archive.hls.config.lowLatencyMode, false);
        archive.video.duration = Infinity;
        archive.hls.latestLevelDetails = { live: true, fragmentStart: 0, edge: 7210 };
        archive.hls.emit('levelUpdated');
        assert.equal(Number(archive.element.querySelector('.video-seek').max), 7210, 'new archived segments extend the range');
      }
      f.target({ isVod: false, isLiveArchive: false });
      await f.playerAction(archive, 'live');
      const returned = f.state.players[0];
      assert.equal(returned.isVod, false);
      assert.equal(f.requests.findLast(r => r.url === '/api/open').input.input, live.source.input);
    } finally { f.disconnect(); }
  });
}

test('archive rewind preserves pause, speed, volume, and quality reload position', async () => {
  const f = fixture({ onRequest: url => url === '/api/live/archive' ? response({ available: true, key: 'full-archive', duration: 3600 }) : null });
  try {
    const p = await f.open(); await settle(f);
    p.video.pause(); p.video.playbackRate = 1.5; p.video.volume = 0.37;
    f.target({ isVod: true, isLiveArchive: true });
    await f.playerAction(p, 'back');
    const archive = f.state.players[0];
    assert.equal(archive.resume, 3570);
    assert.equal(archive.video.paused, true);
    assert.equal(archive.video.playbackRate, 1.5);
    assert.equal(archive.video.volume, 0.37);
    archive.video.currentTime = 120;
    const quality = archive.element.querySelector('.quality'); quality.value = '720p';
    quality.dispatchEvent(new Event('change')); await settle(f);
    assert.equal(f.state.players[0].resume, 120);
    assert.equal(f.state.players[0].quality, '720p');
  } finally { f.disconnect(); }
});

for (const media of ['hls', 'native']) test(`${media}: Live uses the live window while full archive history is available`, async () => {
  const f = fixture({ media, onRequest: url => url === '/api/live/archive'
    ? response({ available: true, key: 'full-archive', duration: 7200 }) : null });
  try {
    const p = await f.open(); await settle(f);
    p.video.duration = Infinity; p.video.currentTime = 1010;
    p.video.seekable = { length: 1, start: () => 1000, end: () => 1030 };
    if (p.hls) {
      p.hls.latestLevelDetails = { live: true, fragmentStart: 1000, edge: 1030 };
      p.hls.liveSyncPosition = 1027;
    }
    const slider = p.element.querySelector('.video-seek');
    slider.value = 15; slider.dispatchEvent(new Event('input'));
    p.video.pause();
    await f.playerAction(p, 'live');
    assert.ok(p.video.currentTime > 1025 && p.video.currentTime < 1030, 'seek the live media clock to its actual live edge');
    assert.equal(p.archiveSeek, null, 'discard any pending archive seek');
    assert.equal(Number(slider.value), 7200, 'show the live edge on the full-history slider');
    assert.equal(p.video.paused, true, 'preserve the playback state');
    slider.dispatchEvent(new Event('blur')); await settle(f);
    assert.equal(f.requests.filter(r => r.url === '/api/open').length, 1, 'leaving the slider cannot reopen the canceled replay');

    p.video.currentTime = 1010;
    slider.value = 7200;
    slider.dispatchEvent(new Event('input'));
    slider.dispatchEvent(new Event('change'));
    await settle(f);
    assert.ok(p.video.currentTime > 1025 && p.video.currentTime < 1030, 'committing the end of the slider also seeks to live');
    assert.equal(p.video.paused, true);
    assert.equal(f.requests.filter(r => r.url === '/api/open').length, 1);
  } finally { f.disconnect(); }
});

test('missing archives retain the actual live window and retry without interrupting playback', async () => {
  let available = false;
  const f = fixture({ onRequest: url => url === '/api/live/archive' ? response(available ? { available: true, key: 'archive', duration: 200 } : { available: false, message: 'No archive yet.' }) : null });
  try {
    const p = await f.open(); await settle(f);
    p.hls.latestLevelDetails = { live: true, fragmentStart: 30, edge: 60 }; refresh(p);
    assert.equal(Number(p.element.querySelector('.video-seek').min), 30);
    assert.equal(Number(p.element.querySelector('.video-seek').max), 60);
    assert.equal(p.video.paused, false);
    available = true; p.archiveRetryAt = 0;
    await f.tick(250); await settle(f);
    assert.equal(Number(p.element.querySelector('.video-seek').max), 200);
  } finally { f.disconnect(); }
});

test('closing while archive lookup is pending ignores its late result', async () => {
  const pending = Promise.withResolvers(), started = Promise.withResolvers();
  const f = fixture({ onRequest: async url => { if (url === '/api/live/archive') { started.resolve(); return await pending.promise; } } });
  try {
    const p = await f.open(); await started.promise;
    f.disconnect();
    pending.resolve(response({ available: true, key: 'archive', duration: 900 }));
    await settle(f);
    assert.equal(p.archive, undefined);
    assert.equal(f.state.players.length, 0);
    assert.equal(f.requests.filter(r => r.url === '/api/open').length, 1);
  } finally { pending.resolve(response({})); f.disconnect(); }
});

test('archive refresh keeps pending history and drops a withdrawn timeline and preview', async () => {
  let archive = { available: true, key: 'archive', url: '/media/live/archive', duration: 900 };
  const f = fixture({ onRequest: url => url === '/api/live/archive' ? response(archive) : null });
  try {
    const p = await f.open(); await settle(f);
    p.hls.latestLevelDetails = { live: true, fragmentStart: 30, edge: 60 };
    const slider = p.element.querySelector('.video-seek');
    archive = { pending: true }; p.archiveRetryAt = 0;
    await f.tick(250); await settle(f);
    assert.equal(Number(slider.max), 900);
    slider.dispatchEvent(Object.assign(new Event('pointerdown'), { button: 0, pointerId: 1 }));
    slider.value = 10; slider.dispatchEvent(new Event('input'));
    archive = { available: false }; p.archiveRetryAt = 0;
    await f.tick(250); await settle(f);
    assert.equal(Number(slider.min), 30);
    assert.equal(Number(slider.max), 60);
    assert.equal(p.seekPreview.element.hidden, true);
    slider.dispatchEvent(new Event('blur')); await settle(f);
    assert.equal(f.requests.filter(r => r.url === '/api/open').length, 1);
  } finally { f.disconnect(); }
});

test('reloading a stopped broadcast archive resumes where it stopped', async () => {
  const f = fixture();
  try {
    f.target({ isVod: true, isLiveArchive: true });
    const p = await f.open({ key: 'archive', liveArchive: true, startPosition: 10 });
    p.video.currentTime = 75;
    await f.playerAction(p, 'stop');
    p.video.currentTime = 0; // Removing the source resets the browser media clock.
    await f.playerAction(p, 'reload');
    assert.equal(f.state.players[0].resume, 75);
  } finally { f.disconnect(); }
});

test('cancelling a drag leaves the live player at live', async () => {
  const f = fixture({ onRequest: url => url === '/api/live/archive' ? response({ available: true, key: 'archive', duration: 900 }) : null });
  try {
    const p = await f.open(); await settle(f);
    const slider = p.element.querySelector('.video-seek');
    slider.dispatchEvent(Object.assign(new Event('pointerdown'), { button: 0, pointerId: 1 }));
    slider.value = 15; slider.dispatchEvent(new Event('input'));
    f.window.dispatchEvent(Object.assign(new Event('pointercancel'), { pointerId: 1 }));
    slider.dispatchEvent(new Event('change'));
    await settle(f);
    assert.equal(f.requests.filter(r => r.url === '/api/open').length, 1);
    slider.value = 900; slider.dispatchEvent(new Event('input')); slider.dispatchEvent(new Event('change'));
    await settle(f);
    assert.equal(f.requests.filter(r => r.url === '/api/open').length, 1, 'the right edge stays live');
  } finally { f.disconnect(); }
});
