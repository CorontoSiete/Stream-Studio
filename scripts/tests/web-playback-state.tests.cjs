'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/web-player-fixture.cjs');

test('manual sign-in supersedes a pending session restore without inheriting preferences', async () => {
  const reply = body => ({ ok: true, text: async () => JSON.stringify(body) });
  const settings = { defaultQuality: 'best', qualities: ['best'], accounts: {}, preferences: {} };
  for (const phase of ['session', 'settings']) {
    const started = Promise.withResolvers(), release = Promise.withResolvers();
    let restoreSignal;
    const f = fixture({ onRequest: async (url, input, options) => {
      if (url === '/api/' + phase && (phase === 'session' || options.headers['X-CSRF-Token'] === 'previous')) {
        restoreSignal = options.signal;
        started.resolve();
        await release.promise;
        return reply(phase === 'session' ? { csrf: 'previous', username: 'Previous' } : settings);
      }
      if (url === '/api/session') return reply({ csrf: 'previous', username: 'Previous' });
      if (url === '/api/login') {
        assert.equal(input.username, 'Current');
        assert.equal(input.password, 'fixture password');
        return reply({ csrf: 'current', username: 'Current' });
      }
      if (url === '/api/settings') return reply(settings);
    } });
    f.disconnect();
    f.setPreferences({ volumes: { 'Twitch:streamer': 0.2 }, size: 29 });
    const restoring = f.restoreSession();
    try {
      await started.promise;
      f.document.querySelector('#username').value = 'Current';
      f.document.querySelector('#password').value = 'fixture password';
      f.document.querySelector('#login-form').dispatchEvent(new Event('submit', { cancelable: true }));
      await new Promise(setImmediate);
      await f.flush();
      assert.equal(restoreSignal?.aborted, true, phase);
      release.resolve();
      await restoring;
      assert.equal(f.state.csrf, 'current', phase);
      assert.equal(f.document.querySelector('#signed-in-user').textContent, 'Current');
      assert.equal(f.document.querySelector('#chat-size').value, 16);
      assert.equal((await f.open()).video.volume, 1);
    } finally {
      release.resolve();
      await restoring;
      f.disconnect();
    }
  }
});

test('live playback retries recording watch history after a temporary failure', async () => {
  let attempts = 0;
  let time = Date.now();
  const f = fixture({ now: () => time, onRequest: url => {
    if (url !== '/api/started' || ++attempts > 1) return;
    return { ok: false, status: 503, json: async () => ({ error: 'Could not save history.' }) };
  } });
  try {
    const player = await f.open();
    player.video.dispatchEvent(new Event('playing'));
    player.video.dispatchEvent(new Event('playing'));
    await f.flush();
    assert.equal(attempts, 1);
    await f.tick(2000);
    assert.equal(attempts, 1, 'wait before retrying a failed write');

    time += 6000;
    await f.tick(2000);
    assert.equal(attempts, 2, 'retry while the original stream keeps playing');
    time += 6000;
    player.video.dispatchEvent(new Event('playing'));
    await f.tick(2000);
    assert.equal(attempts, 2, 'a successful write ends retries for this player');
  } finally { f.disconnect(); }
});

test('Play restarts a stopped player using a fresh media session', async () => {
  const f = fixture();
  try {
    const stopped = await f.open();
    await f.playerAction(stopped, 'stop');
    assert.deepEqual(f.closeCalls, [stopped.id]);

    await f.playerAction(stopped, 'play');
    assert.equal(f.state.players.length, 1);
    const restarted = f.state.players[0];
    assert.notEqual(restarted.id, stopped.id);
    assert.equal(restarted.video.paused, false);
    assert.equal(f.state.selected, restarted.id);
  } finally { f.disconnect(); }
});

test('stopping or closing a player cancels its pending channel details request', { timeout: 5000 }, async () => {
  for (const action of ['stop', 'close']) {
    const started = Promise.withResolvers();
    const response = Promise.withResolvers();
    let profileSignal;
    const f = fixture({ onRequest: async (url, input, options) => {
      if (url !== '/api/channel/profile') return;
      profileSignal = options.signal;
      profileSignal?.addEventListener('abort', () => response.reject(profileSignal.reason), { once: true });
      started.resolve();
      await response.promise;
    } });
    try {
      const player = await f.open();
      await started.promise;
      const ending = f.playerAction(player, action);
      assert.equal(profileSignal?.aborted, true, action);
      await ending;
      assert.deepEqual(f.closeCalls, [player.id]);
      assert.equal(player.element.querySelector('.channel-profile-status').childElementCount, 0);
    } finally {
      response.resolve();
      f.disconnect();
      await f.flush();
    }
  }
});

test('switching from fullscreen skips a player closed during the transition', async () => {
  const f = fixture();
  const exit = Promise.withResolvers();
  try {
    const first = await f.open();
    f.target({ channel: 'other' });
    const next = await f.open();
    f.selectPlayer(first.id);
    f.document.fullscreenElement = first.element;
    f.document.exitFullscreen = async () => {
      await exit.promise;
      f.document.fullscreenElement = null;
    };
    let fullscreenRequests = 0;
    next.element.requestFullscreen = () => { fullscreenRequests++; };
    const switching = f.cyclePlayer(1);

    await f.closePlayer(next);
    exit.resolve();
    await switching;
    assert.equal(f.state.selected, first.id);
    assert.equal(first.element.hidden, false);
    assert.equal(fullscreenRequests, 0);
  } finally {
    exit.resolve();
    f.disconnect();
    await f.flush();
  }
});

test('sign-out releases the session and its players even when saving state fails', async () => {
  for (const failure of ['volume', 'progress']) {
    const f = fixture();
    try {
      f.target({ isVod: true });
      const p = await f.open();
      p.video.currentTime = 23;
      p.video.volume = 0.42;
      if (failure === 'volume') f.failVolumeSave(true);
      else f.failProgressSave(true);
      f.document.querySelector('#logout').dispatchEvent(new Event('click'));
      await new Promise(resolve => setImmediate(resolve));
      await f.flush();
      assert.equal(f.requests.filter(r => r.url === '/api/logout').length, 1, failure);
      assert.equal(f.state.authenticated, false);
      assert.equal(p.closed, true);
      assert.equal(p.video.paused, true);
      assert.equal(f.state.players.length, 0);
    } finally { f.disconnect(); }
  }
});

test('sign-out stops local playback when the desktop cannot process logout', async () => {
  const f = fixture({ onRequest: url => {
    if (url === '/api/logout') throw new TypeError('Desktop disconnected.');
  } });
  try {
    const p = await f.open();
    f.document.querySelector('#logout').dispatchEvent(new Event('click'));
    await new Promise(resolve => setImmediate(resolve));
    await f.flush();
    assert.equal(f.state.authenticated, false);
    assert.equal(f.state.csrf, '');
    assert.equal(p.closed, true);
    assert.equal(p.video.paused, true);
    assert.equal(f.state.players.length, 0);
  } finally { f.disconnect(); }
});

test('a delayed sign-out cannot log out a newly connected account', { timeout: 5000 }, async () => {
  for (const phase of ['progress', 'logout']) {
    const started = Promise.withResolvers(), pending = Promise.withResolvers();
    let first = true;
    const f = fixture({ onRequest: async url => {
      if (url === '/api/' + phase && first) { first = false; started.resolve(); await pending.promise; }
    } });
    try {
      f.target({ isVod: true });
      await f.open();
      f.document.querySelector('#logout').dispatchEvent(new Event('click'));
      await started.promise;
      f.disconnect();
      f.state.authenticated = true;
      f.state.csrf = 'new-session';
      const current = await f.open();
      pending.resolve();
      await new Promise(resolve => setImmediate(resolve));
      await f.flush();
      assert.equal(f.state.authenticated, true, phase);
      assert.equal(f.state.csrf, 'new-session', phase);
      assert.equal(current.closed, false, phase);
      assert.equal(f.requests.filter(r => r.url === '/api/logout').length, phase === 'logout' ? 1 : 0,
        'the old sign-out must not send a logout request with the new account');
    } finally { pending.resolve(); f.disconnect(); }
  }
});

test('a pending bookmark from a disconnected session does not block a new session', { timeout: 5000 }, async () => {
  const started = Promise.withResolvers(), pending = Promise.withResolvers();
  let first = true;
  const f = fixture({ onRequest: async url => {
    if (url === '/api/progress' && first) { first = false; started.resolve(); await pending.promise; }
  } });
  let oldTick;
  try {
    f.target({ isVod: true });
    await f.open();
    oldTick = f.tick(2000);
    await started.promise;
    f.disconnect();
    f.state.authenticated = true;
    f.state.csrf = 'new-session';
    const current = await f.open();
    current.video.currentTime = 47;
    await f.tick(2000);
    assert.equal(f.requests.findLast(r => r.url === '/api/progress')?.input.player, current.id);
    pending.resolve();
    await oldTick;
    assert.equal(f.document.querySelector('#notice').hidden, true, 'old cancellation must not notify the new account');
  } finally { pending.resolve(); if (oldTick) await oldTick; f.disconnect(); }
});

test('a pending stop does not close a player under a new session', { timeout: 5000 }, async () => {
  const started = Promise.withResolvers(), pending = Promise.withResolvers();
  const f = fixture({ onRequest: async url => {
    if (url === '/api/progress') { started.resolve(); await pending.promise; }
  } });
  let stopping;
  try {
    f.target({ isVod: true });
    const old = await f.open();
    stopping = f.playerAction(old, 'stop').catch(error => {
      assert.equal(error.name, 'AbortError');
    });
    await started.promise;
    f.disconnect();
    f.state.authenticated = true;
    f.state.csrf = 'new-session';
    const current = await f.open();
    pending.resolve();
    await stopping;
    assert.deepEqual(f.closeCalls, []);
    assert.equal(f.state.selected, current.id);
    assert.equal(current.video.paused, false);
  } finally {
    pending.resolve();
    if (stopping) await stopping;
    f.disconnect();
  }
});

test('periodic progress saving attempts every due VOD after one save fails', async () => {
  const f = fixture();
  try {
    f.target({ isVod: true });
    const first = await f.open();
    f.target({ channel: 'other' });
    const second = await f.open();
    first.video.currentTime = 27; second.video.currentTime = 51;
    f.failProgressSave(true);
    await f.tick(2000);
    assert.deepEqual(f.requests.filter(r => r.url === '/api/progress').map(r => [r.input.player, r.input.position]),
      [[first.id, 27], [second.id, 51]]);
  } finally { f.disconnect(); }
});

test('a failed volume save does not skip the remaining players', async () => {
  const f = fixture();
  try {
    const first = await f.open();
    f.target({ channel: 'other' });
    const second = await f.open();
    for (const p of [first, second]) {
      p.video.volume = 0.42;
      p.video.dispatchEvent(new Event('volumechange'));
    }
    f.failVolumeSave(true);
    await assert.rejects(f.flushVolumes(), /Volume save failed/);
    assert.deepEqual(f.volumeWrites.map(write => write.player), [first.id, second.id]);
  } finally { f.disconnect(); }
});

test('backgrounding attempts every VOD bookmark even when another state save fails', async () => {
  for (const failure of ['volume', 'progress']) {
    const f = fixture();
    try {
      f.target({ isVod: true });
      const first = await f.open();
      f.target({ channel: 'other' });
      const second = await f.open();
      first.video.currentTime = 27; second.video.currentTime = 51;
      first.video.volume = 0.42;
      first.video.dispatchEvent(new Event('volumechange'));
      if (failure === 'volume') f.failVolumeSave(true);
      else f.failProgressSave(true);
      f.document.hidden = true;
      f.document.dispatchEvent(new Event('visibilitychange'));
      await new Promise(resolve => setImmediate(resolve));
      await f.flush();
      assert.deepEqual(f.requests.filter(r => r.url === '/api/progress').map(r => [r.input.player, r.input.position]),
        [[first.id, 27], [second.id, 51]], failure + ' failure must not skip another bookmark');
    } finally { f.disconnect(); }
  }
});

test('page exit captures the latest volume and VOD position with navigation-safe requests', async () => {
  const f = fixture();
  try {
    f.target({ isVod: true });
    const p = await f.open();
    p.video.currentTime = 39;
    p.video.volume = 0.24; // The browser's volumechange event has not been delivered yet.
    f.window.dispatchEvent(new Event('pagehide'));
    await new Promise(resolve => setImmediate(resolve));
    await f.flush();
    assert.equal(f.volumes.get('Twitch:streamer'), 0.24);
    const progress = f.requests.findLast(r => r.url === '/api/progress');
    assert.equal(progress?.input.position, 39);
    assert.equal(progress.keepalive, true, 'a dispatched bookmark save survives normal page exit');
  } finally { f.disconnect(); }
});

test('closing an ended VOD preserves completion after its ended event was saved', async () => {
  const f = fixture();
  f.target({ isVod: true });
  const p = await f.open();
  p.video.currentTime = p.video.duration;
  p.video.ended = true;
  p.video.dispatchEvent(new Event('ended'));
  await f.flush();
  assert.equal(f.requests.findLast(r => r.url === '/api/progress').input.ended, true);
  await f.closePlayer(p);
  assert.equal(f.requests.findLast(r => r.url === '/api/progress').input.ended, true,
    'closing or periodically saving the finished video must not restore an end-of-video resume point');
  f.disconnect();
});

test('rewatching an ended VOD saves a new resume position', async () => {
  const f = fixture();
  f.target({ isVod: true });
  const p = await f.open();
  p.video.currentTime = p.video.duration;
  p.video.ended = true;
  p.video.dispatchEvent(new Event('ended'));
  await f.flush();
  p.video.currentTime = 25;
  p.video.ended = false;
  await f.closePlayer(p);
  const saved = f.requests.findLast(r => r.url === '/api/progress').input;
  assert.equal(saved.position, 25);
  assert.equal(saved.ended, false, 'seeking away from the end starts another playback pass');
  f.disconnect();
});

test('stopping or closing during replay discovery discards its late response', { timeout: 5000 }, async () => {
  for (const action of ['stop', 'close']) {
    const started = Promise.withResolvers(), response = Promise.withResolvers();
    const f = fixture({ onRequest: async url => {
      if (url === '/api/replay') { started.resolve(); await response.promise; }
    } });
    const p = await f.open();
    const replaying = f.playerAction(p, 'replay');
    await started.promise;
    const stopping = f.playerAction(p, action);
    response.resolve();
    await Promise.all([replaying, stopping]);
    assert.equal(f.requests.filter(r => r.url === '/api/open').length, 1,
      action + ' must cancel the pending switch to replay');
    assert.equal(f.state.players.length, action === 'close' ? 0 : 1);
    assert.equal(p.video.paused, true);
    f.disconnect();
  }
});

test('replay discovery replaces a playing live stream normally', async () => {
  const f = fixture(), p = await f.open();
  f.target({ isVod: true });
  await f.playerAction(p, 'replay');
  assert.equal(f.state.players.length, 1);
  assert.notEqual(f.state.players[0], p);
  assert.equal(f.state.players[0].isVod, true);
  assert.equal(f.state.players[0].video.paused, false);
  f.disconnect();
});

test('picture-in-picture keeps inactive video playing and reapplies pausing when it closes', async () => {
  for (const isVod of [false, true]) {
    const f = fixture();
    f.setPreferences({ keepInactive: false });
    f.target({ isVod });
    const first = await f.open();
    f.document.pictureInPictureElement = first.video;
    first.video.dispatchEvent(new Event('enterpictureinpicture'));
    f.target({ channel: 'other' });
    await f.open();
    assert.equal(first.video.paused, false, 'the detached video remains visible and playing');
    f.document.pictureInPictureElement = null;
    first.video.dispatchEvent(new Event('leavepictureinpicture'));
    assert.equal(first.video.paused, true, 'closing picture-in-picture restores the inactive-tab preference');
    f.document.pictureInPictureElement = first.video;
    first.video.dispatchEvent(new Event('enterpictureinpicture'));
    assert.equal(first.video.paused, false, 'reopening picture-in-picture resumes an automatic pause');
    first.video.pause();
    f.selectPlayer(first.id);
    assert.equal(first.video.paused, true, 'a manually paused detached video stays paused');
    f.disconnect();
  }
});

test('disposing a replaced player cancels its pending volume save', async () => {
  const f = fixture(), first = await f.open();
  first.video.volume = 0.42;
  first.video.dispatchEvent(new Event('volumechange'));
  await f.closePlayer(first, true);
  await new Promise(resolve => setTimeout(resolve, 300));
  await f.flush();
  assert.equal(f.volumeWrites.length, 0, 'a released player cannot send a delayed account write');
  f.disconnect();
});

test('inactive live streams keep playing muted by default', async () => {
  const f = fixture();
  try {
    const first = await f.open();
    f.target({ channel: 'other' });
    await f.open();
    assert.equal(first.video.paused, false);
    assert.equal(first.video.muted, true);
  } finally { f.disconnect(); }
});

test('inactive VOD playback follows its own setting for every live-tab preference', async () => {
  for (const keepInactive of [false, true]) for (const pauseInactiveVod of [false, true]) {
    const f = fixture();
    f.setPreferences({ keepInactive, pauseInactiveVod });
    f.target({ isVod: true });
    const vod = await f.open();
    f.target({ channel: 'live', isVod: false });
    await f.open();
    assert.equal(vod.video.paused, pauseInactiveVod,
      `VOD pause=${pauseInactiveVod} must be independent of live keepInactive=${keepInactive}`);
    f.selectPlayer(vod.id);
    assert.equal(vod.video.paused, false, 'selection resumes an automatically paused VOD');
    f.disconnect();
  }
});

test('always keep audio on resumes an automatically paused stream and preserves manual mute', async () => {
  const f = fixture();
  f.setPreferences({ keepInactive: false });
  const first = await f.open();
  f.target({ channel: 'other' });
  await f.open();
  assert.equal(first.video.paused, true);
  const toggle = first.element.querySelector('.never-mute');
  toggle.checked = true;
  toggle.dispatchEvent(new Event('change'));
  assert.equal(first.video.muted, false);
  assert.equal(first.video.paused, false);
  await f.events();
  await f.playerAction(first, 'mute');
  assert.equal(first.video.muted, true, 'manual mute remains an explicit user choice');
  assert.equal(first.video.paused, false);
  toggle.checked = false;
  toggle.dispatchEvent(new Event('change'));
  assert.equal(first.video.muted, true);
  assert.equal(first.video.paused, true, 'turning protection off restores inactive-tab pausing');
  f.disconnect();
});

test('audio protection and tab selection preserve manual pause and stopped playback', async () => {
  const f = fixture(), first = await f.open();
  await f.playerAction(first, 'play');
  assert.equal(first.video.paused, true);
  f.target({ channel: 'other' });
  await f.open();
  const toggle = first.element.querySelector('.never-mute');
  toggle.checked = true;
  toggle.dispatchEvent(new Event('change'));
  f.selectPlayer(first.id);
  assert.equal(first.video.paused, true, 'a manual pause survives audio protection and selection');
  await f.playerAction(first, 'stop');
  f.selectPlayer(first.id);
  assert.equal(first.video.paused, true);
  f.disconnect();
});

test('overlapping reloads replace a player only once while its progress is being saved', async () => {
  const f = fixture();
  f.target({ isVod: true });
  const first = await f.open();
  await Promise.all([f.openPlayer(first.source, first), f.openPlayer(first.source, first)]);
  assert.equal(f.requests.filter(r => r.url === '/api/open').length, 2, 'one initial open and one replacement');
  assert.equal(f.state.players.length, 1);
  f.disconnect();
});

test('an open response arriving after disconnect cannot recreate a player', { timeout: 5000 }, async () => {
  const started = Promise.withResolvers(), response = Promise.withResolvers();
  const f = fixture({ onRequest: async url => { if (url === '/api/open') { started.resolve(); await response.promise; } } });
  const opening = f.open();
  await started.promise;
  f.disconnect();
  response.resolve();
  await opening.catch(error => { assert.equal(error.name, 'AbortError'); });
  assert.equal(f.state.players.length, 0);
  assert.equal(f.state.selected, null);
});

test('a late network failure belongs to the disconnected session', { timeout: 5000 }, async () => {
  const started = Promise.withResolvers(), response = Promise.withResolvers();
  let opens = 0;
  const f = fixture({ onRequest: async url => {
    if (url === '/api/open' && ++opens === 1) {
      started.resolve(); await response.promise;
      throw new TypeError('Old connection failed.');
    }
  } });
  const opening = f.open();
  // Attach the rejection handler before deliberately failing the old request.
  const canceled = assert.rejects(opening, { name: 'AbortError' });
  try {
    await started.promise;
    f.disconnect();
    f.state.authenticated = true;
    f.state.csrf = 'new-session';
    const current = await f.open();
    response.resolve();
    await canceled;
    assert.equal(f.state.players[0], current);
    assert.equal(current.closed, false);
  } finally { response.resolve(); await canceled.catch(() => {}); f.disconnect(); }
});

test('queued work from a disconnected account cannot run under a new session', { timeout: 5000 }, async () => {
  const started = Promise.withResolvers(), response = Promise.withResolvers();
  const f = fixture({ onRequest: async url => { if (url === '/api/open') { started.resolve(); await response.promise; } } });
  const opening = f.open();
  await started.promise;
  const queued = f.open({ input: 'another stream' });
  f.disconnect();
  f.state.authenticated = true;
  f.state.csrf = 'new-session';
  response.resolve();
  await Promise.all([opening, queued].map(task => task.catch(error => { assert.equal(error.name, 'AbortError'); })));
  assert.equal(f.requests.filter(r => r.url === '/api/open').length, 1);
  assert.equal(f.state.players.length, 0);
  f.disconnect();
});

test('a new session can open a stream while an old session request is still pending', { timeout: 5000 }, async () => {
  const started = Promise.withResolvers(), response = Promise.withResolvers();
  let opens = 0;
  const f = fixture({ onRequest: async url => {
    if (url === '/api/open' && ++opens === 1) { started.resolve(); await response.promise; }
  } });
  const oldOpening = f.open();
  await started.promise;
  f.disconnect();
  f.state.authenticated = true;
  f.state.csrf = 'new-session';
  const player = await f.open();
  assert.equal(f.state.players.length, 1);
  response.resolve();
  await assert.rejects(oldOpening, { name: 'AbortError' });
  assert.equal(f.state.players[0], player);
  f.disconnect();
});

test('closing a player during reload also closes the late replacement', { timeout: 5000 }, async () => {
  const started = Promise.withResolvers(), response = Promise.withResolvers();
  let opens = 0;
  const f = fixture({ onRequest: async url => {
    if (url === '/api/open' && ++opens === 2) { started.resolve(); await response.promise; }
  } });
  const first = await f.open();
  const reloading = f.openPlayer(first.source, first);
  await started.promise;
  const closing = f.closePlayer(first);
  response.resolve();
  await Promise.all([reloading, closing]);
  assert.equal(f.state.players.length, 0);
  assert.ok(f.closeCalls.includes('player-2'), 'the late server player is released too');
  f.disconnect();
});

test('a failed pre-reload save keeps the original video usable and allows retry', async () => {
  const f = fixture(), first = await f.open();
  first.video.volume = 0.42;
  f.failVolumeSave(true);
  await assert.rejects(f.openPlayer(first.source, first), /Volume save failed/);
  assert.equal(first.reloading, false);
  assert.equal(first.video.paused, false);
  assert.ok(first.hls);
  f.failVolumeSave(false);
  await f.openPlayer(first.source, first);
  assert.equal(f.volumes.get('Twitch:streamer'), 0.42, 'retry still saves the dirty volume after its failed first attempt');
  assert.equal(f.state.players.length, 1);
  assert.notEqual(f.state.players[0], first);
  f.disconnect();
});

test('a failed quality reload keeps the video playing and restores its quality selector', async () => {
  let fail = false;
  const f = fixture({ onRequest: url => {
    if (url === '/api/open' && fail) throw new Error('Could not resolve the stream.');
  } });
  try {
    const first = await f.open();
    first.element.querySelector('.quality').value = '720p';
    const hls = first.hls;
    fail = true;
    await assert.rejects(f.openPlayer({ ...first.source, quality: '720p' }, first), /Could not resolve/);
    assert.equal(f.state.players[0], first);
    assert.equal(first.hls, hls);
    assert.equal(first.video.paused, false);
    assert.equal(first.element.querySelector('.quality').value, first.quality);
    assert.equal(first.element.querySelector('.quality').disabled, false);
  } finally { f.disconnect(); }
});

test('quality cannot be changed while the player is being replaced', async () => {
  const started = Promise.withResolvers(), response = Promise.withResolvers();
  const f = fixture({ onRequest: async (url, input) => {
    if (url === '/api/open' && input.replacePlayer) { started.resolve(); await response.promise; }
  } });
  let reloading;
  try {
    const first = await f.open();
    reloading = f.openPlayer({ ...first.source, quality: '720p' }, first);
    await started.promise;
    assert.equal(first.element.querySelector('.quality').disabled, true);
    response.resolve();
    const replacement = await reloading;
    assert.equal(replacement.element.querySelector('.quality').value, '720p');
    assert.equal(Boolean(replacement.element.querySelector('.quality').disabled), false);
  } finally { response.resolve(); await reloading?.catch(() => {}); f.disconnect(); }
});

test('repeated opens share one pending player and select the existing player afterward', async () => {
  for (const source of [{ key: 'channel-key' }, { downloadId: 'download-id' }, { input: 'twitch.tv/streamer' }]) {
    const f = fixture();
    const [first, repeated] = await Promise.all([f.open(source), f.open(source)]);
    assert.equal(repeated, first);
    assert.equal(await f.open(source), first);
    assert.equal(f.requests.filter(r => r.url === '/api/open').length, 1);
    assert.equal(f.state.players.length, 1);
    f.disconnect();
  }
});

test('a failed open can be retried for the same source', async () => {
  let opens = 0;
  const f = fixture({ onRequest: url => { if (url === '/api/open' && ++opens === 1) throw new Error('Resolution failed.'); } });
  await assert.rejects(f.open({ key: 'channel-key' }), /Resolution failed/);
  const player = await f.open({ key: 'channel-key' });
  assert.equal(f.state.players[0], player);
  f.disconnect();
});

test('stopping during reload discards and releases the late replacement', { timeout: 5000 }, async () => {
  const started = Promise.withResolvers(), response = Promise.withResolvers();
  let opens = 0;
  const f = fixture({ onRequest: async url => {
    if (url === '/api/open' && ++opens === 2) { started.resolve(); await response.promise; }
  } });
  const first = await f.open();
  const reloading = f.openPlayer(first.source, first);
  await started.promise;
  const stopping = f.playerAction(first, 'stop');
  response.resolve();
  await Promise.all([reloading, stopping]);
  assert.equal(f.state.players.length, 1);
  assert.equal(f.state.players[0], first);
  assert.equal(first.video.paused, true);
  assert.equal(first.stopped, true);
  assert.equal(first.reloading, false);
  assert.ok(f.closeCalls.includes('player-2'), 'the unused replacement releases its server resources');
  await f.openPlayer(first.source, first);
  assert.notEqual(f.state.players[0], first, 'reload still works after the stop finishes');
  f.disconnect();
});

test('quality replacement preserves manual pause and playback speed', async () => {
  const f = fixture();
  f.target({ isVod: true });
  const first = await f.open();
  first.video.playbackRate = 1.5;
  await f.playerAction(first, 'play');
  const replacement = await f.openPlayer({ ...first.source, quality: '720p' }, first);
  assert.equal(replacement.video.paused, true);
  assert.equal(replacement.video.playbackRate, 1.5);
  assert.equal(replacement.element.querySelector('.speed').value, '1.5');
  f.disconnect();
});

test('theater buttons reflect the shared mode across players, reloads and navigation', async () => {
  const f = fixture();
  const pressed = p => p.element.querySelector('[data-action=theater]').getAttribute('aria-pressed');
  try {
    const first = await f.open({ key: 'first' }), second = await f.open({ key: 'second' });
    await f.playerAction(first, 'theater');
    assert.equal(pressed(first), 'true');
    assert.equal(pressed(second), 'true');

    const replacement = await f.openPlayer(second.source, second);
    assert.equal(pressed(replacement), 'true');

    await f.showPage('settings');
    assert.equal(f.document.querySelector('#studio').classList.contains('theater'), false);
    assert.equal(pressed(first), 'false');
    assert.equal(pressed(replacement), 'false');
  } finally { f.disconnect(); }
});

test('reloading a player keeps its position in the channel list and grid', async () => {
  const f = fixture();
  try {
    f.target({ isVod: true });
    const first = await f.open({ key: 'first' }), second = await f.open({ key: 'second' });
    const replacement = await f.openPlayer(first.source, first);
    assert.deepEqual(Array.from(f.state.players, p => p.id), [replacement.id, second.id]);
    assert.deepEqual(f.document.querySelector('#players').children, [replacement.element, second.element]);
    assert.equal(f.state.selected, second.id);
    assert.equal(replacement.video.paused, true);
    f.selectPlayer(replacement.id);
    assert.equal(replacement.video.paused, false);
  } finally { f.disconnect(); }
});

test('stopping during a pre-reload save prevents the replacement request', { timeout: 5000 }, async () => {
  const started = Promise.withResolvers(), response = Promise.withResolvers();
  let saves = 0;
  const f = fixture({ onRequest: async url => {
    if (url === '/api/progress' && ++saves === 1) { started.resolve(); await response.promise; }
  } });
  f.target({ isVod: true });
  const first = await f.open();
  first.video.currentTime = 42;
  const reloading = f.openPlayer(first.source, first);
  await started.promise;
  const stopping = f.playerAction(first, 'stop');
  assert.equal(first.video.paused, true, 'stop releases media while the server save is pending');
  response.resolve();
  await Promise.all([reloading, stopping]);
  assert.equal(f.requests.filter(r => r.url === '/api/open').length, 1);
  assert.equal(first.reloading, false);
  assert.equal(first.stopped, true);
  assert.equal(f.requests.find(r => r.url === '/api/progress').input.position, 42);
  f.disconnect();
});
