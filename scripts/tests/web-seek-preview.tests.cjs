'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { setTimeout: delay } = require('node:timers/promises');
const { fixture } = require('./helpers/web-player-fixture.cjs');

function hover(p, fraction) {
  const seek = p.element.querySelector('.video-seek');
  const event = new Event('pointermove');
  Object.defineProperty(event, 'clientX', { value: 7 + fraction * (seek.clientWidth - 14) });
  seek.dispatchEvent(event);
}
const refresh = p => p.video.dispatchEvent(new Event('durationchange'));

for (const platform of ['Twitch', 'Kick']) for (const media of ['hls', 'native']) {
  test(`${platform} ${media}: live history hover decodes the archive timeline without opening a replay`, async () => {
    const archive = { available: true, key: 'archive', duration: 21600, url: '/media/player-1/archive.m3u8' };
    const f = fixture({ media, onRequest: url => url === '/api/live/archive'
      ? { ok: true, text: async () => JSON.stringify(archive) } : null });
    try {
      f.target({ platform });
      const p = await f.open(); await new Promise(resolve => setImmediate(resolve));
      p.video.duration = Infinity; p.video.currentTime = 275; refresh(p);
      hover(p, 0.5); await delay(180);
      const preview = p.seekPreview, video = preview.video;
      assert.ok(video, 'full live history must request an archive frame');
      assert.equal(preview.label.textContent, '3:00:00');
      assert.equal(preview.hls?.url ?? video.url, archive.url, 'never decode the unrelated live playlist');
      if (preview.hls) preview.hls.emit('manifestParsed');
      video.duration = archive.duration; video.readyState = 2; video.videoWidth = 640;
      let drawn;
      preview.canvas.getContext = () => ({ clearRect() {}, drawImage: source => { drawn = source; }, getImageData: () => ({ data: [0, 0, 0, 255] }) });
      video.dispatchEvent(new Event('loadedmetadata')); video.dispatchEvent(new Event('seeked'));
      assert.equal(video.currentTime, 10800); assert.equal(drawn, video);
      assert.equal(preview.canvas.hidden, false); assert.equal(preview.canvas.dataset.time, '10800');
      assert.equal(p.video.currentTime, 275); assert.equal(p.video.paused, false);
      assert.equal(f.requests.filter(r => ['/api/open', '/api/started', '/api/progress'].includes(r.url)).length, 1);
      p.element.querySelector('.video-seek').dispatchEvent(new Event('pointerleave'));
      assert.equal(preview.video, null);
    } finally { f.disconnect(); }
  });
}

test('archive URL renewal dismisses the old frame and releases its decoder before another hover', async () => {
  let url = '/media/player-1/first.m3u8';
  const f = fixture({ onRequest: path => path === '/api/live/archive'
    ? { ok: true, text: async () => JSON.stringify({ available: true, key: 'archive', duration: 100, url }) } : null });
  try {
    const p = await f.open(); await new Promise(resolve => setImmediate(resolve));
    hover(p, 0.5); await delay(180);
    const oldVideo = p.seekPreview.video, oldHls = p.seekPreview.hls;
    let destroyed = false; oldHls.destroy = () => { destroyed = true; };
    url = '/media/player-1/renewed.m3u8'; p.archiveRetryAt = 0;
    await f.tick(250); await new Promise(resolve => setImmediate(resolve));
    assert.equal(destroyed, true); assert.equal(p.seekPreview.video, null);
    assert.equal(p.seekPreview.element.hidden, true);
    hover(p, 0.5); await delay(180);
    const newVideo = p.seekPreview.video;
    assert.notEqual(newVideo, oldVideo); assert.equal(p.seekPreview.hls.url, url);
    oldVideo.dispatchEvent(new Event('error'));
    assert.equal(p.seekPreview.video, newVideo, 'events from the expired source cannot cancel the replacement');
  } finally { f.disconnect(); }
});

test('an archive without a preview URL never substitutes an unrelated live frame', async () => {
  const f = fixture({ onRequest: path => path === '/api/live/archive'
    ? { ok: true, text: async () => JSON.stringify({ available: true, key: 'archive', duration: 21600 }) } : null });
  try {
    const p = await f.open(); await new Promise(resolve => setImmediate(resolve));
    hover(p, 0.5); await delay(180);
    assert.equal(p.seekPreview.video, null);
    assert.equal(p.seekPreview.label.textContent, '3:00:00');
    assert.equal(p.seekPreview.status.textContent, 'Preview unavailable');
    assert.equal(p.video.paused, false);
  } finally { f.disconnect(); }
});

test('VOD time shows elapsed and total, metadata fallback, unknown duration, and hours', async () => {
  const f = fixture();
  try {
    f.target({ isVod: true, duration: 14600 });
    const p = await f.open(), text = () => p.element.querySelector('.video-time').textContent;
    p.video.duration = NaN; refresh(p);
    assert.equal(text(), '0:00:00 / 4:03:20');
    p.duration = 0; refresh(p);
    assert.equal(text(), '0:00:00 / --:--:--');
    p.video.duration = 14400; p.video.currentTime = 3723; refresh(p);
    assert.equal(text(), '1:02:03 / 4:00:00');
    p.video.currentTime = 14400; refresh(p);
    assert.equal(text(), '4:00:00 / 4:00:00');
  } finally { f.disconnect(); }
});

test('hover maps to the thumb track without seeking and clamps at both ends', async () => {
  const f = fixture();
  try {
    f.target({ isVod: true });
    const p = await f.open(); p.video.duration = 7200; p.video.currentTime = 123; refresh(p);
    for (const [fraction, expected] of [[-1, '0:00:00'], [0.5, '1:00:00'], [2, '2:00:00']]) {
      hover(p, fraction);
      assert.equal(p.seekPreview.label.textContent, expected);
      assert.equal(p.video.currentTime, 123);
      assert.equal(p.seekPreview.element.hidden, false);
    }
    p.element.querySelector('.video-seek').dispatchEvent(new Event('pointerleave'));
    assert.equal(p.seekPreview.element.hidden, true);
    assert.equal(p.seekPreview.video, null, 'rapid hover never creates a decoder');
  } finally { f.disconnect(); }
});

test('a settled hover decodes independently, stops loading, and releases media on dismissal', async () => {
  const f = fixture();
  try {
    f.target({ isVod: true });
    const p = await f.open(); p.video.duration = 100; p.video.currentTime = 12; refresh(p);
    hover(p, 0.5); await delay(180);
    const preview = p.seekPreview, video = preview.video, hls = preview.hls;
    assert.notEqual(video, p.video); assert.equal(video.muted, true); assert.equal(video.paused, true);
    assert.equal(hls.config.autoStartLoad, false);
    hls.emit('manifestParsed'); assert.equal(hls.startPosition, 50);
    assert.equal(hls.startLevel, 1, 'skip audio-only renditions');
    let drawn;
    preview.canvas.getContext = () => ({ clearRect() {}, drawImage: source => { drawn = source; }, getImageData: () => ({ data: [0, 0, 0, 255] }) });
    video.readyState = 2; video.videoWidth = 640; video.dispatchEvent(new Event('loadedmetadata'));
    video.dispatchEvent(new Event('seeked'));
    assert.equal(drawn, video); assert.equal(preview.canvas.hidden, false);
    assert.equal(preview.canvas.dataset.time, '50'); assert.equal(hls.loading, false);
    assert.equal(p.video.currentTime, 12);
    let destroyed = false; hls.destroy = () => { destroyed = true; };
    p.element.querySelector('.video-seek').dispatchEvent(new Event('pointerleave'));
    assert.equal(destroyed, true); assert.equal(preview.video, null);
    video.dispatchEvent(new Event('seeked'));
    assert.equal(preview.element.hidden, true, 'late events cannot reopen the card');
    assert.equal(f.requests.filter(r => ['/api/open', '/api/started', '/api/progress'].includes(r.url)).length, 1);
  } finally { f.disconnect(); }
});

for (const media of ['native', 'hls']) test(`${media} preview errors preserve the hover timestamp and main playback`, async () => {
  const f = fixture({ media });
  try {
    f.target({ isVod: true });
    const p = await f.open(); p.video.duration = 100; p.video.currentTime = 25; refresh(p);
    hover(p, 0.75); await delay(180);
    const preview = p.seekPreview, video = preview.video;
    video.dispatchEvent(new Event('error'));
    assert.equal(preview.video, null); assert.equal(preview.canvas.hidden, true);
    assert.equal(preview.status.textContent, 'Preview unavailable');
    assert.equal(preview.label.textContent, '0:01:15');
    assert.equal(p.video.currentTime, 25); assert.equal(p.video.paused, false);
    video.dispatchEvent(new Event('loadedmetadata'));
    assert.equal(preview.video, null, 'late metadata cannot restore failed media');
    hover(p, 0.5); await delay(180);
    assert.ok(preview.video, 'a different hover can retry');
    await f.playerAction(p, 'stop');
    assert.equal(preview.video, null); assert.equal(preview.element.hidden, true);
  } finally { f.disconnect(); }
});

test('live hover timestamps use the same available ranges and gaps as actual seeking', async () => {
  const f = fixture();
  try {
    const p = await f.open(); p.video.duration = Infinity; p.video.currentTime = 275;
    p.hls.latestLevelDetails = { live: true, fragmentStart: 240, edge: 300 };
    p.hls.liveSyncPosition = 294;
    p.video.buffered = { length: 1, start: () => 160, end: () => 180 }; refresh(p);
    hover(p, (205 - 160) / 140);
    assert.equal(p.seekPreview.time, 179.95);
    assert.equal(p.seekPreview.label.textContent, '−0:01:54');
    hover(p, 1); assert.equal(p.seekPreview.time, 294); assert.equal(p.seekPreview.label.textContent, 'LIVE');
    p.stopped = true; refresh(p); assert.equal(p.seekPreview.element.hidden, true);
  } finally { f.disconnect(); }
});
