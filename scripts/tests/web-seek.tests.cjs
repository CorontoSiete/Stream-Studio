'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/web-player-fixture.cjs');

const ranges = (...values) => ({ length: values.length, start: i => values[i][0], end: i => values[i][1] });
const refresh = p => p.video.dispatchEvent(new Event('progress'));
function seek(p, position) {
  const slider = p.element.querySelector('.video-seek');
  slider.value = position;
  slider.dispatchEvent(new Event('input'));
}

for (const platform of ['Twitch', 'Kick']) {
  for (const media of ['hls', 'native']) {
    test(`${platform} ${media}: live seekbar scrubs infinite duration and returns to live`, async () => {
      const f = fixture({ media });
      try {
        f.target({ platform });
        const p = await f.open();
        p.video.duration = Infinity;
        p.video.currentTime = 125;
        p.video.seekable = ranges([100, 140]);
        if (p.hls) { p.hls.latestLevelDetails = { live: true, fragmentStart: 100, edge: 140 }; p.hls.liveSyncPosition = 137; }
        refresh(p);
        const slider = p.element.querySelector('.video-seek');
        assert.equal(slider.hidden, false);
        assert.equal(slider.disabled, false);
        assert.equal(Number(slider.min), 100);
        assert.equal(Number(slider.max), 140);
        seek(p, 110);
        assert.equal(p.video.currentTime, 110);
        await f.playerAction(p, 'back');
        assert.equal(p.video.currentTime, 100);
        await f.playerAction(p, 'forward');
        assert.equal(p.video.currentTime, 130);
        await f.playerAction(p, 'forward');
        assert.ok(p.video.currentTime > 130 && p.video.currentTime < 140);
        await f.playerAction(p, 'live');
        assert.ok(p.video.currentTime > 130 && p.video.currentTime < 140);
        assert.ok(!p.element.querySelector('.video-time').textContent.includes('Infinity'));
        assert.equal(f.requests.filter(r => r.url === '/api/replay').length, 0);
      } finally { f.disconnect(); }
    });
  }
}

test('live seeks use the sliding playlist and retained buffers, excluding expired media and gaps', async () => {
  const f = fixture();
  try {
    const p = await f.open();
    p.video.duration = 300; // MSE's default finite duration is not a seekable live window.
    p.video.seekable = ranges([0, 300]);
    p.video.buffered = ranges([160, 180], [220, 260]);
    p.hls.latestLevelDetails = { live: true, fragmentStart: 240, edge: 300 };
    p.hls.liveSyncPosition = 294;
    p.video.currentTime = 275;
    p.hls.emit('levelUpdated');
    const slider = p.element.querySelector('.video-seek');
    assert.equal(Number(slider.min), 160);
    assert.equal(Number(slider.max), 300);
    seek(p, 165);
    assert.equal(p.video.currentTime, 165, 'retained video remains playable after leaving the playlist');
    seek(p, 205);
    assert.equal(p.video.currentTime, 220, 'do not seek into an unavailable gap');
    seek(p, 0);
    assert.equal(p.video.currentTime, 160, 'expired segments must not be requested');
    seek(p, 300);
    assert.equal(p.video.currentTime, 294, 'the right edge lands on a safe live position');
    p.video.buffered = ranges([250, 280]);
    p.hls.latestLevelDetails = { live: true, fragmentStart: 270, edge: 330 };
    p.hls.emit('bufferFlushed');
    assert.equal(Number(slider.min), 250);
    assert.equal(Number(slider.max), 330);
  } finally { f.disconnect(); }
});

test('live controls wait for a real range and ignore invalid seek input', async () => {
  const f = fixture({ media: 'native' });
  try {
    const p = await f.open();
    p.video.duration = NaN;
    refresh(p);
    const slider = p.element.querySelector('.video-seek');
    assert.equal(slider.hidden, false);
    assert.equal(slider.disabled, true);
    seek(p, 100);
    await f.playerAction(p, 'forward');
    await f.playerAction(p, 'back');
    assert.equal(p.video.currentTime, 0);
    p.video.seekable = ranges([50, 80]);
    refresh(p);
    assert.equal(slider.disabled, false);
    seek(p, NaN);
    assert.equal(p.video.currentTime, 0);
    seek(p, 65);
    assert.equal(p.video.currentTime, 65);
    p.stopped = true;
    refresh(p);
    assert.equal(slider.disabled, true);
    seek(p, 70);
    assert.equal(p.video.currentTime, 65);
  } finally { f.disconnect(); }
});

test('a live seek keeps its range during a drag and updates after a release outside the slider', async () => {
  const f = fixture();
  try {
    const p = await f.open();
    p.video.duration = Infinity;
    p.hls.latestLevelDetails = { live: true, fragmentStart: 100, edge: 160 };
    refresh(p);
    const slider = p.element.querySelector('.video-seek');
    slider.dispatchEvent(Object.assign(new Event('pointerdown'), { button: 0, pointerId: 1 }));
    slider.value = 120;
    p.hls.latestLevelDetails = { live: true, fragmentStart: 130, edge: 190 };
    p.hls.emit('levelUpdated');
    assert.equal(Number(slider.min), 100);
    assert.equal(Number(slider.max), 160);
    assert.equal(Number(slider.value), 120);
    slider.dispatchEvent(new Event('input'));
    assert.equal(p.video.currentTime, 130, 'clamp to the current range even while the displayed range is frozen');
    f.window.dispatchEvent(Object.assign(new Event('pointerup'), { pointerId: 1 }));
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(Number(slider.min), 130);
    assert.equal(Number(slider.max), 190);
  } finally { f.disconnect(); }
});

test('VOD seeking and skips still cover the full finite duration', async () => {
  const f = fixture();
  try {
    f.target({ isVod: true });
    const p = await f.open();
    p.video.duration = 600;
    p.video.currentTime = 590;
    refresh(p);
    const slider = p.element.querySelector('.video-seek');
    assert.equal(Number(slider.min), 0);
    assert.equal(Number(slider.max), 600);
    seek(p, 20);
    assert.equal(p.video.currentTime, 20);
    await f.playerAction(p, 'back');
    assert.equal(p.video.currentTime, 0);
    seek(p, 590);
    await f.playerAction(p, 'forward');
    assert.equal(p.video.currentTime, 600);
  } finally { f.disconnect(); }
});

test('range updates keep unchanged button contents in place during pointer clicks', async () => {
  const f = fixture();
  try {
    const p = await f.open();
    refresh(p);
    const play = p.element.querySelector('[data-action=play]'), mute = p.element.querySelector('[data-action=mute]');
    const playIcon = play.children[0], muteIcon = mute.children[0];
    for (const name of ['progress', 'timeupdate', 'durationchange']) p.video.dispatchEvent(new Event(name));
    assert.equal(play.children[0], playIcon, 'replacing a pressed SVG cancels its pending click in the browser');
    assert.equal(mute.children[0], muteIcon);
    p.video.pause();
    p.video.dispatchEvent(new Event('pause'));
    assert.notEqual(play.children[0], playIcon, 'a real state change still updates the button');
  } finally { f.disconnect(); }
});
