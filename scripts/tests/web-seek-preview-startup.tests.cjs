'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { setTimeout: delay } = require('node:timers/promises');
const { fixture } = require('./helpers/web-player-fixture.cjs');

async function firstPreview(media) {
  const f = fixture({ media });
  f.target({ isVod: true });
  const p = await f.open();
  p.video.duration = 100; p.video.currentTime = 12;
  p.video.dispatchEvent(new Event('durationchange'));
  const seek = p.element.querySelector('.video-seek'), event = new Event('pointermove');
  Object.defineProperty(event, 'clientX', { value: 7 + 0.5 * (seek.clientWidth - 14) });
  seek.dispatchEvent(event);
  await delay(180);
  const preview = p.seekPreview, video = preview.video;
  preview.hls?.emit('manifestParsed');
  video.readyState = 2; video.videoWidth = 640; video.videoHeight = 360;
  let available = false, draws = 0;
  const context = { clearRect() {}, drawImage() { draws++; }, getImageData: () => ({ data: new Uint8ClampedArray([0, 0, 0, available ? 255 : 0]) }) };
  preview.canvas.getContext = () => context;
  return { f, p, preview, video, seek, available: () => { available = true; }, draws: () => draws };
}

for (const media of ['native', 'hls']) test(`${media}: first hover waits for pixels and recovers without moving the pointer`, async () => {
  const x = await firstPreview(media);
  try {
    x.video.dispatchEvent(new Event('loadedmetadata'));
    x.video.dispatchEvent(new Event('seeked'));
    await delay(70);
    assert.equal(x.preview.canvas.hidden, true, 'an empty draw must not become a black preview');
    assert.equal(x.preview.status.hidden, false);
    if (x.preview.hls) assert.equal(x.preview.hls.loading, true, 'keep loading until capture succeeds');
    x.available();
    await delay(100);
    assert.equal(x.preview.canvas.hidden, false, 'retry capture when no further media event fires');
    assert.equal(x.preview.canvas.dataset.time, '50');
    assert.equal(x.preview.status.hidden, true);
    if (x.preview.hls) assert.equal(x.preview.hls.loading, false);
    assert.equal(x.p.video.currentTime, 12); assert.equal(x.p.video.paused, false);
  } finally { x.f.disconnect(); }
});

test('a genuine opaque black scene is a valid preview', async () => {
  const x = await firstPreview('hls');
  try {
    x.available();
    x.video.dispatchEvent(new Event('loadedmetadata'));
    x.video.dispatchEvent(new Event('seeked'));
    await delay(70);
    assert.equal(x.preview.canvas.hidden, false);
    assert.equal(x.preview.hls.loading, false);
    const draws = x.draws();
    x.video.dispatchEvent(new Event('canplay'));
    assert.equal(x.draws(), draws, 'later readiness events cannot overwrite a verified capture');
  } finally { x.f.disconnect(); }
});

test('a new decoder waits for frame presentation even when media events report it ready', async () => {
  const x = await firstPreview('hls');
  try {
    const callbacks = new Map(); let nextCallback = 0;
    x.video.requestVideoFrameCallback = callback => { callbacks.set(++nextCallback, callback); return nextCallback; };
    x.video.cancelVideoFrameCallback = id => callbacks.delete(id);
    x.available();
    x.video.dispatchEvent(new Event('loadedmetadata'));
    x.video.dispatchEvent(new Event('seeked'));
    assert.equal(x.draws(), 0, 'media events do not prove the first frame has been presented');
    assert.equal(x.preview.canvas.hidden, true);
    assert.equal(callbacks.size, 1);
    callbacks.values().next().value();
    assert.equal(x.preview.canvas.hidden, false);
    assert.equal(x.preview.hls.loading, false);
  } finally { x.f.disconnect(); }
});

for (const media of ['native', 'hls']) test(`${media}: reusing a preview decoder waits for the newly requested frame`, async () => {
  const x = await firstPreview(media);
  try {
    const callbacks = new Map(); let nextCallback = 0;
    x.video.requestVideoFrameCallback = callback => { callbacks.set(++nextCallback, callback); return nextCallback; };
    x.video.cancelVideoFrameCallback = id => callbacks.delete(id);
    const present = time => {
      const [id, callback] = callbacks.entries().next().value;
      callbacks.delete(id);
      callback(0, { mediaTime: time });
    };
    x.available();
    x.video.dispatchEvent(new Event('loadedmetadata'));
    x.video.dispatchEvent(new Event('seeked'));
    present(50);
    assert.equal(x.preview.canvas.dataset.time, '50');
    const draws = x.draws();

    x.video.seeking = true;
    const event = new Event('pointermove');
    Object.defineProperty(event, 'clientX', { value: 7 + 0.75 * (x.seek.clientWidth - 14) });
    x.seek.dispatchEvent(event);
    await delay(180);
    assert.equal(x.preview.video, x.video, 'retain the decoder while scrubbing');
    x.video.seeking = false;
    x.video.dispatchEvent(new Event('seeked'));
    assert.equal(x.draws(), draws, 'seek completion must not capture pixels from the previous timestamp');
    assert.equal(x.preview.canvas.hidden, true);

    present(75);
    assert.equal(x.preview.canvas.hidden, false);
    assert.equal(x.preview.canvas.dataset.time, '75');
    assert.equal(x.p.video.currentTime, 12);
    if (x.preview.hls) assert.equal(x.preview.hls.loading, false);
  } finally { x.f.disconnect(); }
});

test('a paused preview can present its frame before seeking finishes', async () => {
  const x = await firstPreview('hls');
  try {
    let present;
    x.video.requestVideoFrameCallback = callback => { present = callback; return 1; };
    x.video.cancelVideoFrameCallback = () => {};
    x.available();
    x.video.seeking = true; x.video.readyState = 1;
    x.video.dispatchEvent(new Event('loadedmetadata'));
    present();
    assert.equal(x.preview.canvas.hidden, true, 'wait for the seek even when its frame has been presented');
    x.video.seeking = false; x.video.readyState = 2;
    x.video.dispatchEvent(new Event('seeked'));
    assert.equal(x.preview.canvas.hidden, false, 'a paused decoder need not present the same frame again');
    assert.equal(x.preview.canvas.dataset.time, '50');
    assert.equal(x.preview.hls.loading, false);
  } finally { x.f.disconnect(); }
});

test('dismissal cancels a pending first-frame capture', async () => {
  const x = await firstPreview('hls');
  try {
    x.video.dispatchEvent(new Event('loadedmetadata'));
    x.video.dispatchEvent(new Event('seeked'));
    await delay(70);
    x.seek.dispatchEvent(new Event('pointerleave'));
    const draws = x.draws();
    x.available();
    x.video.dispatchEvent(new Event('canplay'));
    await delay(100);
    assert.equal(x.draws(), draws);
    assert.equal(x.preview.video, null);
    assert.equal(x.preview.element.hidden, true);
  } finally { x.f.disconnect(); }
});

test('a frame that never arrives times out and releases the decoder', async () => {
  const x = await firstPreview('hls');
  try {
    x.video.dispatchEvent(new Event('loadedmetadata'));
    x.video.dispatchEvent(new Event('seeked'));
    await delay(8100);
    assert.equal(x.preview.canvas.hidden, true);
    assert.equal(x.preview.status.textContent, 'Preview unavailable');
    assert.equal(x.preview.video, null);
    const draws = x.draws();
    x.available();
    await delay(100);
    assert.equal(x.draws(), draws, 'timeout also cancels pending retries');
  } finally { x.f.disconnect(); }
});

test('moving to a new target cancels the old first-frame callback', async () => {
  const x = await firstPreview('hls');
  try {
    const callbacks = new Map(), cancelled = []; let nextCallback = 0;
    x.video.requestVideoFrameCallback = callback => { callbacks.set(++nextCallback, callback); return nextCallback; };
    x.video.cancelVideoFrameCallback = id => { cancelled.push(id); callbacks.delete(id); };
    x.video.dispatchEvent(new Event('loadedmetadata'));
    x.video.dispatchEvent(new Event('seeked'));
    const staleCallback = callbacks.values().next().value;
    const event = new Event('pointermove');
    Object.defineProperty(event, 'clientX', { value: 7 + 0.75 * (x.seek.clientWidth - 14) });
    x.seek.dispatchEvent(event);
    assert.deepEqual(cancelled, [1]);
    await delay(180);
    staleCallback();
    assert.equal(x.preview.frameCallback, 2, 'an obsolete callback cannot clear the new capture request');
    assert.equal(x.preview.canvas.hidden, true);
    x.available();
    callbacks.get(2)();
    assert.equal(x.preview.canvas.dataset.time, '75');
    assert.equal(x.preview.canvas.hidden, false);
  } finally { x.f.disconnect(); }
});
