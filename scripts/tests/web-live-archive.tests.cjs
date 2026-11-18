'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/web-player-fixture.cjs');
const response = value => ({ ok: true, text: async () => JSON.stringify(value) });
const settle = async f => { await f.flush(); await new Promise(resolve => setImmediate(resolve)); await f.flush(); };
const refresh = p => p.video.dispatchEvent(new Event('progress'));

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
