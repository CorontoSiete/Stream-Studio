'use strict';
const assert = require('node:assert/strict');

module.exports = async ({ cdp, evaluate, wait, capture, viewport, playbackRequests }) => {
  const startPlayback = async () => {
    await wait("document.querySelector('#players video')?.videoWidth>0 && !document.querySelector('.video-seek').disabled");
    // Brave enforces an autoplay gesture even with the Chromium test flag.
    await cdp('Runtime.evaluate', { expression: "document.querySelector('#players video').play()", userGesture: true, awaitPromise: true });
    await wait("document.querySelector('#players video').currentTime>0.1");
  };
  await evaluate("document.querySelector('#open-dialog').showModal();document.querySelector('#open-query').value='https://www.twitch.tv/videos/12345';document.querySelector('#open-form').requestSubmit()");
  await startPlayback();
  await evaluate("window.mainVideo=document.querySelector('#players video');mainVideo.pause();mainVideo.currentTime=2");
  await wait('!mainVideo.seeking');
  const mainPosition = await evaluate('mainVideo.currentTime');
  const moveHover = async fraction => {
    await evaluate("scrollTo(0,0);document.querySelector('.video-surface').classList.remove('controls-idle')");
    const point = await evaluate(`(()=>{const r=document.querySelector('.video-seek').getBoundingClientRect();return {x:r.left+7+${fraction}*(r.width-14),y:r.top+r.height/2}})()`);
    await cdp('Input.dispatchMouseEvent', { type: 'mouseMoved', ...point });
  };
  const hover = async fraction => {
    await moveHover(fraction);
    await wait("!document.querySelector('.seek-preview').hidden && !document.querySelector('.seek-preview-frame').hidden");
    return evaluate(`(()=>{
      const card=document.querySelector('.seek-preview'),canvas=document.querySelector('.seek-preview-frame'),surface=document.querySelector('.video-surface');
      const r=card.getBoundingClientRect(),s=surface.getBoundingClientRect(),pixel=canvas.getContext('2d').getImageData(0,0,canvas.width,canvas.height).data;
      return {label:document.querySelector('.seek-preview-time').textContent,time:Number(canvas.dataset.time),
        colored:pixel.some((v,i)=>i%4!==3&&v>20),rgb:Array.from(pixel.slice(0,3)),left:r.left,right:r.right,top:r.top,bottom:r.bottom,surface:{left:s.left,right:s.right,top:s.top,bottom:s.bottom},
        playback:mainVideo.currentTime,paused:mainVideo.paused,readout:document.querySelector('.video-time').textContent,max:Number(document.querySelector('.video-seek').max)};
    })()`);
  };
  // Hold back the canvas copy after the real decoder becomes ready. This makes
  // the first-frame race deterministic without replacing media, events or HLS.
  await evaluate(`(() => {
    const original = CanvasRenderingContext2D.prototype.drawImage;
    window.previewCaptureGate = { blocked: true, attempts: 0, original };
    CanvasRenderingContext2D.prototype.drawImage = function(source, ...args) {
      if (this.canvas.matches('.seek-preview-frame') && previewCaptureGate.blocked) { previewCaptureGate.attempts++; return; }
      return original.call(this, source, ...args);
    };
  })()`);
  for (const fraction of [0.59, 0.1]) {
    await evaluate('previewCaptureGate.blocked=true;previewCaptureGate.attempts=0');
    await moveHover(fraction);
    await wait('previewCaptureGate.attempts>0');
    await evaluate('new Promise(resolve=>setTimeout(resolve,200))');
    assert.equal(await evaluate("document.querySelector('.seek-preview-frame').hidden"), true, 'empty first capture stays in loading state');
    assert.equal(await evaluate("document.querySelector('.seek-preview-status').textContent"), 'Loading preview…');
    await evaluate('previewCaptureGate.blocked=false');
    await wait("!document.querySelector('.seek-preview-frame').hidden");
    const result = await hover(fraction), component = fraction === 0.59 ? 1 : 0;
    assert.ok(result.rgb[component] > 100 && result.rgb[1 - component] < 30, 'capture recovers at the hovered scene without a new pointer event');
    assert.equal(result.playback, mainPosition);
    assert.equal(result.paused, true);
  }
  await evaluate('CanvasRenderingContext2D.prototype.drawImage=previewCaptureGate.original;delete window.previewCaptureGate');
  for (const width of [1440, 390, 320]) {
    await viewport(width);
    // Include nearby seeks within one low-frame-rate scene and two targets
    // clamped to the final frame, as well as large jumps between scenes.
    for (const fraction of [0, 0.5, 0.59, 0.592, 0.595, 0.999, 1]) {
      const result = await hover(fraction);
      assert.ok(result.colored, 'preview contains real decoded pixels');
      if (fraction === 0.59) assert.ok(result.rgb[1] > 100 && result.rgb[1] > result.rgb[0] * 2, '35-second preview decodes the green scene, beyond red segment preroll');
      assert.ok(Math.abs(result.time - fraction * result.max) < 0.15, 'preview is for the hovered timestamp');
      assert.equal(result.playback, mainPosition, 'hover leaves playback untouched');
      assert.equal(result.paused, true, 'hover leaves pause state untouched');
      assert.ok(result.left >= result.surface.left - 1 && result.right <= result.surface.right + 1, 'card stays within horizontal bounds');
      assert.ok(result.top >= result.surface.top - 1 && result.bottom <= result.surface.bottom, 'card stays within video bounds');
      assert.match(result.readout, /^0:00:02 \/ 0:0\d:\d\d$/);
      await capture(`seek-preview-${width}-${fraction}`);
    }
  }
  await cdp('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 0, y: 0 });
  await wait("document.querySelector('.seek-preview').hidden");
  // Keyboard navigation exposes the same timestamp and frame without a mouse.
  await evaluate("document.querySelector('.video-seek').focus()");
  await wait("!document.querySelector('.seek-preview-frame').hidden");
  await cdp('Input.dispatchKeyEvent', { type: 'keyDown', key: 'ArrowRight', code: 'ArrowRight', windowsVirtualKeyCode: 39 });
  await cdp('Input.dispatchKeyEvent', { type: 'keyUp', key: 'ArrowRight', code: 'ArrowRight', windowsVirtualKeyCode: 39 });
  await wait('mainVideo.currentTime>2');
  await cdp('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await cdp('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  assert.equal(await evaluate("document.querySelector('.seek-preview').hidden"), true);
  await evaluate("document.querySelector('[data-action=close]').click()");
  await wait("!document.querySelector('#players video')");
  await viewport(1440);
  await evaluate("document.querySelector('#open-dialog').showModal();document.querySelector('#open-query').value='https://www.twitch.tv/videos/mp4';document.querySelector('#open-form').requestSubmit()");
  await startPlayback();
  await evaluate("mainVideo=document.querySelector('#players video');mainVideo.pause();mainVideo.currentTime=2");
  await wait('!mainVideo.seeking');
  for (const fraction of [0.25, 0.75]) {
    const result = await hover(fraction);
    assert.ok(result.colored, 'MP4 preview decodes a frame');
    assert.equal(result.playback, mainPosition, 'MP4 hover leaves playback untouched');
    assert.ok(Math.abs(result.time - fraction * result.max) < 0.15);
    await capture('seek-preview-mp4-' + fraction);
  }
  await evaluate("Object.defineProperty(mainVideo,'duration',{configurable:true,value:17*3600+59*60+59});Object.defineProperty(mainVideo,'currentTime',{configurable:true,value:12*3600+34*60+56});mainVideo.dispatchEvent(new Event('durationchange'))");
  await viewport(320);
  const longReadout = await evaluate(`(()=>{
    const p=document.querySelector('.player'),surface=p.querySelector('.video-surface').getBoundingClientRect();
    return {text:p.querySelector('.video-time').textContent,width:surface.width,bar:p.querySelector('.player-controls').getBoundingClientRect().height,
      fits:[...p.querySelectorAll('.control-group > *')].filter(e=>e.getBoundingClientRect().width>0).every(e=>{const r=e.getBoundingClientRect();return r.left>=surface.left&&r.right<=surface.right&&r.top>=surface.top&&r.bottom<=surface.bottom})};
  })()`);
  assert.equal(longReadout.text, '12:34:56 / 17:59:59');
  assert.ok(longReadout.fits, 'long time readout and buttons stay within a phone video');
  await capture('seek-preview-long-vod-320');
  await evaluate("document.querySelector('[data-action=close]').click()");
  await wait("!document.querySelector('#players video')");
  // The live manifest only has its last two segments. Early red and later scenes
  // history frames must come from the separate archive, without opening it.
  await viewport(1440);
  for (const platform of ['Twitch', 'Kick']) {
    await evaluate(`document.querySelector('#open-dialog').showModal();document.querySelector('#open-query').value=${JSON.stringify('https://' + (platform === 'Twitch' ? 'www.twitch.tv' : 'kick.com') + '/streamer')};document.querySelector('#open-form').requestSubmit()`);
    await startPlayback();
    await wait("document.querySelector('#players video')?.videoWidth>0 && Number(document.querySelector('.video-seek').max)===60");
    assert.equal(await evaluate("document.querySelector('.player-provider').textContent"), platform);
    await evaluate("mainVideo=document.querySelector('#players video');mainVideo.pause();window.livePreviewPosition=mainVideo.currentTime");
    const before = playbackRequests();
    for (const [fraction, color] of [[0.1, 'red'], [0.59, 'green'], [1, 'blue']]) {
      const result = await hover(fraction);
      assert.ok(result.colored, platform + ' full-history preview decodes pixels');
      const component = { red: 0, green: 1, blue: 2 }[color];
      assert.ok(result.rgb[component] > 100 && result.rgb.every((value, index) => index === component || value < result.rgb[component] / 2),
        `archive at ${fraction}: expected ${color}, decoded ${JSON.stringify(result.rgb)}`);
      assert.equal(result.playback, await evaluate('livePreviewPosition'), 'archive hover leaves live position untouched');
      assert.equal(result.paused, true); assert.equal(result.label, fraction === 1 ? '0:01:00' : fraction === 0.1 ? '0:00:06' : '0:00:35');
      await capture('seek-preview-live-' + platform.toLowerCase() + '-' + fraction);
    }
    assert.deepEqual(playbackRequests(), before, 'hover creates no player, watch-progress, or started request');
    await evaluate("document.querySelector('[data-action=close]').click()");
    await wait("!document.querySelector('#players video')");
  }
  assert.deepEqual(await evaluate('window.visualErrors'), []);
  console.log('PASS decoded HLS, MP4, and Twitch/Kick full live-history hover frames; distinct live/archive timelines and scene colors; start/middle/end; desktop and phone bounds; unchanged playback and request counts; keyboard seeking and dismissal.');
};
