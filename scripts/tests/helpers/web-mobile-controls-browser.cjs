'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

module.exports = async ({ cdp, evaluate, wait, capture, output }) => {
  const measurements = [];
  const open = async input => {
    const count = await evaluate("document.querySelectorAll('#players video').length");
    await evaluate("document.querySelector('#open-dialog').showModal()");
    await evaluate(`document.querySelector('#open-query').value=${JSON.stringify(input)};document.querySelector('#open-form').requestSubmit()`);
    await wait(`document.querySelectorAll('#players video').length===${count + 1} && document.querySelectorAll('#players video')[${count}].currentTime>0.1`);
  };
  const device = async (width, height = 844) => {
    await cdp('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: true });
    await cdp('Emulation.setTouchEmulationEnabled', { enabled: true });
  };
  const measure = async (label, playerCount = Infinity) => {
    // Reveal through the player so the idle timer restarts for hit testing.
    await evaluate("scrollTo(0,0);document.querySelectorAll('.video-surface').forEach(e=>e.dispatchEvent(new PointerEvent('pointermove',{pointerType:'touch'})));new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
    const m = await evaluate(`(()=>{
      const rect=e=>{const r=e.getBoundingClientRect();return {left:r.left,top:r.top,right:r.right,bottom:r.bottom,width:r.width,height:r.height}};
      return {width:innerWidth,height:innerHeight,scrollWidth:document.documentElement.scrollWidth,coarse:matchMedia('(pointer:coarse)').matches,
        players:[...document.querySelectorAll('.player:not([hidden])')].map(p=>{
          const bar=p.querySelector('.player-controls'),f=p.querySelector('[data-action=fullscreen]'),r=f.getBoundingClientRect();
          const targets=[[r.x+r.width/2,r.y+r.height/2],[r.left+2,r.top+2],[r.right-2,r.bottom-2]]
            .map(([x,y])=>document.elementFromPoint(x,y));
          const hit=targets.every(target=>f.contains(target));
          return {surface:rect(p.querySelector('.video-surface')),bar:rect(bar),decoded:p.querySelector('video').videoWidth>0,
            fullscreen:{...rect(f),visible:!f.hidden,hit,obstructions:targets.filter(target=>!f.contains(target)).map(target=>target?.outerHTML.slice(0,180)),
              idle:p.querySelector('.video-surface').classList.contains('controls-idle')},
            controls:[...bar.querySelectorAll('.control-group > *')].filter(e=>e.getBoundingClientRect().width>0).map(e=>({name:e.dataset.action||e.className,...rect(e)}))};
        })};
    })()`);
    m.label = label;
    measurements.push(m);
    fs.writeFileSync(path.join(output, 'measurements.json'), JSON.stringify(measurements, null, 2));
    assert.ok(m.coarse, label + ': real touch emulation is enabled');
    assert.ok(m.scrollWidth <= m.width, label + ': no page overflow');
    for (const p of m.players.slice(0, playerCount)) {
      assert.ok(p.decoded, label + ': real video frames decoded');
      assert.ok(p.fullscreen.visible && p.fullscreen.hit, label + ': the whole fullscreen button receives input: ' + JSON.stringify(p.fullscreen));
      for (const r of p.controls) {
        assert.ok(r.left >= p.surface.left - 1 && r.right <= p.surface.right + 1 && r.top >= p.surface.top - 1 && r.bottom <= p.surface.bottom + 1,
          label + ': ' + r.name + ' fits inside the video: ' + JSON.stringify({control:r,surface:p.surface}));
      }
      for (let i = 0; i < p.controls.length; i++) for (let j = i + 1; j < p.controls.length; j++) {
        const a = p.controls[i], b = p.controls[j];
        assert.ok(a.right <= b.left + 1 || b.right <= a.left + 1 || a.bottom <= b.top + 1 || b.bottom <= a.top + 1,
          label + ': ' + a.name + ' and ' + b.name + ' do not overlap');
      }
    }
    return m;
  };
  const tap = async point => {
    await cdp('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [point] });
    await cdp('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  };
  const tapButton = async selector => {
    const point = await evaluate(`(() => {
      const button = document.querySelector(${JSON.stringify(selector)});
      if (!button) return null;
      const r = button.getBoundingClientRect();
      const x = r.left + r.width / 2, y = r.top + r.height / 2;
      return r.width >= 44 && r.height >= 44 && button.contains(document.elementFromPoint(x, y)) ? { x, y } : null;
    })()`);
    assert.ok(point, selector + ' is a visible and unobstructed touch target');
    await tap(point);
  };
  const tapFullscreen = async (playerCount = Infinity) => {
    const m = await measure('before fullscreen touch', playerCount);
    const r = m.players[0].fullscreen;
    await tap({ x: r.left + r.width / 2, y: r.top + r.height / 2 });
  };
  const videoPoint = () => evaluate(`(() => {
    const video = document.querySelector('.player:not([hidden]) video');
    const r = video.getBoundingClientRect();
    return { x: r.left + r.width * .3, y: r.top + r.height * .35 };
  })()`);
  const doubleTapVideo = async () => {
    const point = await videoPoint();
    await tap(point);
    await tap(point);
  };
  for (const source of ['live', 'vod', 'long-vod']) {
    await open(source === 'live' ? 'Northstar' : 'https://www.twitch.tv/videos/12345');
    if (source === 'live') {
      await device(390);
      await wait("document.querySelector('.player .chat + .channel-information')");
      assert.ok(await evaluate(`(() => {
        const video = document.querySelector('.player video').getBoundingClientRect();
        const chat = document.querySelector('.player .chat').getBoundingClientRect();
        const info = document.querySelector('.player .channel-information').getBoundingClientRect();
        return chat.height > 0 && Math.abs(chat.top - video.bottom) < 2 && info.top >= chat.bottom;
      })()`), 'docked chat starts directly below the video and above channel details on a phone');
      await capture('watch-chat-phone');
      await device(1440);
      await wait("document.querySelector('.player .screen > .channel-information')");
    }
    if (source === 'long-vod') {
      // The six-second decoded fixture cannot supply an hours-long timeline. Feed
      // realistic metadata through the production duration/time event handlers.
      await evaluate(`(()=>{const v=document.querySelector('.player:not([hidden]) video');
        Object.defineProperty(v,'duration',{configurable:true,value:17*3600+59*60+59});
        Object.defineProperty(v,'currentTime',{configurable:true,value:12*3600+34*60+56});
        v.dispatchEvent(new Event('durationchange'));v.dispatchEvent(new Event('timeupdate'))})()`);
      assert.equal(await evaluate("document.querySelector('.player:not([hidden]) .video-time').textContent"), '12:34:56 / 17:59:59');
    }
    // 808/809 and 848/849 straddle video-container breakpoints with docked chat.
    for (const width of [320, 360, 375, 390, 414, 440, 650, 651, 760, 768, 800, 808, 809, 812, 820, 844, 848, 849, 900, 980, 981, 1020, 1021, 1440]) {
      await device(width);
      await measure(source + ' at ' + width);
      if ([320, 812].includes(width)) await capture(source + '-' + width);
    }
    for (const [width, height] of [[500, 300], [568, 320], [640, 360], [650, 360]]) {
      await device(width, height);
      await measure(source + ' at ' + width + 'x' + height + ' above bottom navigation');
    }
    // Use trusted touch input on the actual button, including the original failure
    // width, then rotate while fullscreen and exit using the same control.
    if (source === 'live') {
      await device(568, 320);
      await capture('live-568-landscape');
    } else await device(812, 375);
    await tapFullscreen();
    await wait("!!document.fullscreenElement");
    await measure(source + ' landscape fullscreen');
    await device(320, 568);
    await measure(source + ' portrait fullscreen');
    await capture(source + '-portrait-fullscreen');
    await tapFullscreen();
    await wait("!document.fullscreenElement");
    await evaluate("document.querySelector('.player:not([hidden]) [data-action=close]').click()");
    await wait("document.querySelectorAll('#players video').length===0");
  }

  const activeStreamVisible = "!!document.querySelector('#active-stream')?.getClientRects().length";
  const followingVisible = "document.querySelector('#studio').dataset.page==='following' && !!document.querySelector('#library').getClientRects().length";
  await device(320, 568);
  assert.equal(await evaluate(activeStreamVisible), false, 'Following has no return button without an open stream');
  await device(1440);
  await open('Northstar');
  await open('Pixelquest');
  await device(320, 568);
  await tapButton('#channels-toggle');
  await wait("document.querySelector('#channels-dialog').open");
  await tapButton('#channels-dialog .is-open .followed-link');
  await wait("!document.querySelector('#channels-dialog').open");
  await evaluate("window.lastWatchedVideo=document.querySelector('.player:not([hidden]) video')");
  assert.ok(await evaluate("lastWatchedVideo === document.querySelector('#players video') && lastWatchedVideo.videoWidth > 0"),
    'the older open player is selected and decoding video');
  for (const [width, height] of [[320, 568], [390, 844], [812, 375]]) {
    await device(width, height);
    await tapButton('.sidebar [data-page=following]');
    await wait(`${followingVisible} && document.querySelector('#cards').getAttribute('aria-busy')==='false'`);
    await capture('following-active-stream-' + width);
    if (width === 320) {
      await evaluate('scrollTo(0, document.documentElement.scrollHeight)');
      await wait('scrollY > 0');
    }
    await tapButton('#active-stream');
    await wait("document.querySelector('#studio').classList.contains('watching')");
    assert.ok(await evaluate("document.querySelector('.player:not([hidden]) video') === lastWatchedVideo && document.activeElement === lastWatchedVideo"),
      'the return button restores focus to the same player instance');
    assert.ok(await evaluate(`(() => {
      const top = lastWatchedVideo.getBoundingClientRect().top;
      return top >= document.querySelector('.topbar').getBoundingClientRect().bottom - 1 && top < innerHeight;
    })()`), 'the restored video is visible below the header');
    assert.equal(await evaluate(activeStreamVisible), false, 'the return button is hidden while watching');
  }
  await evaluate('delete window.lastWatchedVideo');
  await device(1440);
  await evaluate("const t=document.querySelector('.player:not([hidden]) .multistream-toggle');t.checked=true;t.dispatchEvent(new Event('change'))");
  await wait("document.querySelector('#players').classList.contains('grid')");
  for (const width of [390, 812, 1180, 1202, 1440]) {
    await device(width);
    const m = await measure('two-stream grid at ' + width);
    assert.equal(m.players.length, 2);
  }
  await device(568, 320);
  await measure('first grid tile at 568x320 above bottom navigation', 1);
  await device(812, 375);
  await tapFullscreen();
  await wait("document.fullscreenElement?.id==='players'");
  await measure('two-stream grid fullscreen');
  await capture('grid-fullscreen');
  await doubleTapVideo();
  await wait("document.fullscreenElement === document.querySelector('#players .player')");
  await doubleTapVideo();
  await wait("document.fullscreenElement?.id==='players'");
  await tapFullscreen();
  await wait("!document.fullscreenElement");

  await evaluate("document.querySelector('.player:not([hidden]) .multistream-toggle').click();document.querySelectorAll('.player [data-action=close]').forEach(button=>button.click())");
  await wait("!document.querySelector('#players video')");
  await device(390);
  assert.ok(await evaluate(followingVisible), 'closing the last player returns to Following');
  assert.equal(await evaluate(activeStreamVisible), false, 'closing the last player clears the return button');
  console.log('PASS Following returns to the same open player at phone and landscape sizes, including after scrolling.');
  await open('Northstar');
  await evaluate(`window.addEventListener('dblclick', event => {
    if (event.target.matches('video')) event.stopImmediatePropagation();
  }, true)`);
  // Use trusted touches even when the browser does not deliver a video dblclick.
  const point = await videoPoint(), fartherPoint = { ...point, x: point.x + 60 };
  await tap(point);
  assert.equal(await evaluate('document.fullscreenElement'), null, 'one touch leaves playback inline');
  await tap(fartherPoint);
  assert.equal(await evaluate('document.fullscreenElement'), null, 'touches far apart are not a double tap');
  await tap(fartherPoint);
  await wait("document.fullscreenElement?.classList.contains('player')");
  await doubleTapVideo();
  await wait('!document.fullscreenElement');

  await evaluate("const layout=document.querySelector('#chat-layout');layout.value='overlay';layout.dispatchEvent(new Event('change',{bubbles:true}))");
  await wait("document.querySelector('.player').classList.contains('chat-overlay')");
  await device(568, 320);
  await measure('default overlay chat at 568x320 above bottom navigation');
  assert.ok(await evaluate(`(() => {
    const chat = document.querySelector('.chat');
    const bounds = chat.getBoundingClientRect();
    return ['.chat-move', '.chat-resize', '.chat-form input', '.chat-form button'].every(selector => {
      const control = chat.querySelector(selector);
      const r = control.getBoundingClientRect();
      return r.bottom <= bounds.bottom && control.contains(document.elementFromPoint(r.x+r.width/2,r.y+r.height/2));
    });
  })()`), 'the compact overlay retains its chat targets');
  await capture('overlay-568-landscape');
  await evaluate("document.querySelector('.player [data-action=close]').click()");
  await wait("!document.querySelector('#players video')");

  // Chromium supplies decoded video and real touch events. Model the WebKit video
  // API separately so the app cannot fall back to Chromium's page fullscreen.
  await evaluate(`(() => {
    Object.defineProperty(document, 'fullscreenEnabled', { value: false });
    Element.prototype.requestFullscreen = undefined;
    let fullscreenVideo = null;
    window.videoFullscreenAvailable = false;
    window.videoFullscreenEntries = 0;
    Object.defineProperties(HTMLVideoElement.prototype, {
      webkitSupportsFullscreen: { get() { return videoFullscreenAvailable && this.readyState >= this.HAVE_METADATA; } },
      webkitDisplayingFullscreen: { get() { return fullscreenVideo === this; } },
      webkitEnterFullscreen: { value() {
        if (!this.webkitSupportsFullscreen || !navigator.userActivation.isActive) throw new DOMException('Fullscreen is unavailable', 'InvalidStateError');
        window.videoFullscreenEntries++;
        fullscreenVideo = this;
        this.dispatchEvent(new Event('webkitbeginfullscreen'));
      } },
      webkitExitFullscreen: { value() {
        fullscreenVideo = null;
        this.dispatchEvent(new Event('webkitendfullscreen'));
      } }
    });
    const layout = document.querySelector('#chat-layout');
    layout.value = 'docked';
    layout.dispatchEvent(new Event('change', { bubbles: true }));
  })()`);
  await device(390);
  await open('Northstar');
  const button = "document.querySelector('.player [data-action=fullscreen]')";
  const video = "document.querySelector('.player video')";
  assert.ok(await evaluate(`${button}.hidden`), 'fullscreen is hidden until video fullscreen is available');
  await doubleTapVideo();
  assert.ok(await evaluate("document.querySelector('#notice').hidden && videoFullscreenEntries === 0"), 'unsupported fullscreen leaves playback inline without an error');
  await evaluate(`window.videoFullscreenAvailable = true; ${video}.dispatchEvent(new Event('loadedmetadata'))`);
  await measure('video-only fullscreen at 390');
  assert.equal(await evaluate(`${button}.ariaLabel`), 'Fullscreen video');
  assert.ok(await evaluate("!document.querySelector('[data-action=fullscreen-menu]').hidden"), 'Stream options also offer video fullscreen');
  await tapFullscreen();
  await wait(`${video}.webkitDisplayingFullscreen && videoFullscreenEntries === 1`);
  await evaluate(`${video}.webkitExitFullscreen()`);
  await wait(`${button}.ariaLabel === 'Fullscreen video'`);
  await doubleTapVideo();
  await wait(`${video}.webkitDisplayingFullscreen && videoFullscreenEntries === 2`);
  await evaluate(`${video}.webkitExitFullscreen()`);
  await measure('inline again after video-only fullscreen');
  assert.ok(await evaluate("document.querySelector('#notice').hidden"), 'video fullscreen does not show an error');
  assert.deepEqual(await evaluate('window.visualErrors'), []);
  console.log('PASS mobile control bounds, short landscape, long timelines, grid and overlay layouts, fullscreen buttons and rotation, double taps, video-only capability fallback, and unsupported fullscreen.');
};
