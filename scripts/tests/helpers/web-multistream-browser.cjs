'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

module.exports = async ({ cdp, evaluate, wait, capture, viewport, output }) => {
  const measurements = {};
  const desktopViewport = { width: 1910, height: 965, deviceScaleFactor: 1, mobile: false };
  const open = async channel => {
    const count = await evaluate("document.querySelectorAll('#players video').length");
    await cdp('Input.dispatchKeyEvent', { type: 'keyDown', key: 'k', code: 'KeyK', windowsVirtualKeyCode: 75, modifiers: 2 });
    await cdp('Input.dispatchKeyEvent', { type: 'keyUp', key: 'k', code: 'KeyK', windowsVirtualKeyCode: 75, modifiers: 2 });
    await wait("document.querySelector('#open-dialog').open");
    await evaluate(`document.querySelector('#open-query').value=${JSON.stringify(channel)};document.querySelector('#open-form').requestSubmit()`);
    await wait(`document.querySelectorAll('#players video').length===${count + 1} && document.querySelectorAll('#players video')[${count}].currentTime>0.1`);
  };
  const layout = async name => {
    await evaluate("document.querySelector('button[data-page=settings]').click()");
    await wait("document.querySelector('#studio').dataset.page==='settings' && !document.querySelector('#studio').classList.contains('watching')");
    await evaluate(`document.querySelector('#chat-layout').value=${JSON.stringify(name)};document.querySelector('#chat-layout').dispatchEvent(new Event('change',{bubbles:true}));document.querySelector('#chat-opacity').value='0';document.querySelector('#chat-opacity').dispatchEvent(new Event('input',{bubbles:true}))`);
    await wait("document.querySelector('#save-status').textContent==='All changes saved to your account.'");
    await evaluate("(document.querySelector('.followed-link[aria-current=true]') || document.querySelector('[data-page=following]')).click()");
    await wait("!document.querySelector('#players video') || document.querySelector('#studio').classList.contains('watching')");
  };
  const toggle = async value => {
    await evaluate(`(()=>{const t=document.querySelector('.player:not([hidden]) .multistream-toggle');t.checked=${value};t.dispatchEvent(new Event('change'))})()`);
    await wait(`document.querySelector('#players').classList.contains('grid')===${value}`);
  };
  const metrics = async () => {
    await evaluate('scrollTo(0,0);new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');
    return evaluate(`(()=>{const rect=e=>{const r=e.getBoundingClientRect();return {x:r.x,y:r.y,w:r.width,h:r.height,right:r.right,bottom:r.bottom}};
      return {width:innerWidth,height:innerHeight,scrollWidth:document.documentElement.scrollWidth,header:rect(document.querySelector('.topbar')),
        grid:rect(document.querySelector('#players')),pixelSize:parseFloat(getComputedStyle(document.querySelector('#players')).getPropertyValue('--grid-pixel-size')),
        media:[...document.querySelectorAll('.video-chat')].map(rect),videos:[...document.querySelectorAll('video')].map(v=>({...rect(v),frameWidth:v.videoWidth,frameHeight:v.videoHeight,fit:getComputedStyle(v).objectFit})),
        info:[...document.querySelectorAll('.channel-information:not([hidden])')].map(rect),descriptions:[...document.querySelectorAll('.channel-information:not([hidden]) .channel-description')].map(rect)}})()`);
  };
  const assertTilesTouch = (m, label) => {
    const columns = m.width <= 650 ? 1 : 2;
    for (const [index, tile] of m.media.entries()) {
      if (index % columns) assert.equal(tile.x, m.media[index - 1].right, label + ': neighboring columns touch');
      if (index >= columns) assert.equal(tile.y, m.media[index - columns].bottom, label + ': neighboring rows touch');
    }
  };
  const assertGrid = async (count, label) => {
    const m = await metrics();
    measurements[label] = m;
    assert.equal(m.media.length, count);
    assert.equal(m.info.length, 1, label + ': only the selected channel information is visible');
    assert.ok(await evaluate("document.querySelector('.channel-information:not([hidden])').closest('.player').id==='player-'+document.querySelector('.is-selected').dataset.playerId"), label + ': channel information follows the selected tab');
    assert.ok(m.scrollWidth <= m.width, label + ': no horizontal overflow');
    assert.equal(m.info[0].w, m.grid.w, label + ': selected channel information spans the watch area');
    const lastMedia = Math.max(...m.media.map(r => r.bottom));
    assert.ok(m.info.every(r => r.y >= lastMedia - 1), label + ': all channel information follows all video rows');
    assert.ok(m.descriptions.every(r => r.y >= m.height - 1), label + ': descriptions require scrolling');
    assert.ok(m.videos.every(r => r.h >= 100 && r.w >= 100), label + ': videos retain usable dimensions');
    assertTilesTouch(m, label);
    if (count <= 2 && m.width > 650) {
      assert.ok(Math.abs(lastMedia - m.height) <= 1, label + ': videos use the available viewport');
    }
    return m;
  };

  const assertPicturesFillTiles = (m, label) => {
    for (const video of m.videos) {
      assert.equal(video.fit, 'contain', label + ': video preserves the whole frame');
      const ratio = video.frameWidth / video.frameHeight;
      assert.ok(Math.abs(video.w - video.h * ratio) <= m.pixelSize, label +
        `: picture fills its ${video.w} × ${video.h} video element at a source ratio of ${ratio}`);
    }
  };

  const readPixels = async clip => {
    const { data } = await cdp('Page.captureScreenshot', { format: 'png', clip: { ...clip, scale: 1 } });
    return evaluate(`(async () => {
      const image = new Image();
      image.src = 'data:image/png;base64,${data}';
      await image.decode();
      const canvas = document.createElement('canvas');
      canvas.width = image.width;
      canvas.height = image.height;
      const context = canvas.getContext('2d');
      context.drawImage(image, 0, 0);
      return [...context.getImageData(0, 0, image.width, image.height).data];
    })()`);
  };
  const assertRenderedSeams = async (m, label, docked = false) => {
    const columns = m.width <= 650 ? 1 : 2;
    const points = [];
    for (const [i, video] of m.videos.entries()) {
      if (i % columns && video.y + video.h / 2 < m.height - 3) points.push([video.x, video.y + video.h / 2]);
      if (!docked && i >= columns && video.y < m.height - 3) points.push([video.x + video.w * .84, video.y]);
    }
    assert.ok(points.length > 0, label + ': there are visible seams to sample');
    for (const [x, y] of points) {
      const pixels = await readPixels({ x: Math.floor(x) - 2, y: Math.floor(y) - 2, width: 5, height: 5 });
      // The color bars have no black bands in these samples, even under the controls.
      for (let i = 0; i < pixels.length; i += 4) {
        assert.ok(Math.max(pixels[i], pixels[i + 1], pixels[i + 2]) > 18, label + `: video pixels touch around (${x}, ${y})`);
      }
    }
  };

  await layout('overlay');
  await cdp('Emulation.setDeviceMetricsOverride', desktopViewport);
  await open('Northstar');
  await open('Pixelquest');
  await toggle(true);
  const videoFocus = await evaluate(`(() => {
    const video = document.querySelector('.player:last-child video');
    const indicator = getComputedStyle(video.parentElement, '::after');
    return { focused: video.matches(':focus-visible'), outline: getComputedStyle(video).outlineStyle,
      indicator: indicator.content !== 'none' && indicator.borderTopStyle === 'solid' && parseFloat(indicator.top) > 0 };
  })()`);
  assert.ok(videoFocus.focused, 'opening a stream with the keyboard gives its video visible focus');
  assert.equal(videoFocus.outline, 'none', 'the focused video draws no outline across a neighboring tile');
  assert.ok(videoFocus.indicator, 'keyboard focus has a visible indicator inside the tile');
  const two = await assertGrid(2, 'two streams at screenshot size');
  assert.ok(two.videos.every(r => r.fit === 'contain' && r.frameWidth / r.frameHeight === 16 / 9 && Math.min(r.h, r.w * r.frameHeight / r.frameWidth) > 965 * .36), 'actual 16:9 pictures exceed the old 36vh limit without cropping');
  await capture('two-streams');

  // Real wheel input must reach the existing channel descriptions and social/panel content.
  await cdp('Input.dispatchMouseEvent', { type: 'mouseWheel', x: 800, y: 700, deltaX: 0, deltaY: 800 });
  await wait("[...document.querySelectorAll('.channel-information:not([hidden]) .channel-description')].some(e=>{const r=e.getBoundingClientRect();return r.y<innerHeight && r.bottom>64})");
  await capture('channel-information-after-scroll');
  await evaluate("window.originalVideo=document.querySelector('video');window.originalInfo=document.querySelector('.channel-information');document.querySelector('.channel-information:not([hidden]) .channel-description').textContent+=' Long biography.'.repeat(500)");
  assert.deepEqual((await metrics()).videos, two.videos, 'long channel descriptions do not change media dimensions');
  await evaluate("document.querySelectorAll('.player')[0].querySelector('.video-surface').dispatchEvent(new PointerEvent('pointerdown',{bubbles:true}))");
  assert.ok(await evaluate("document.querySelector('video')===window.originalVideo && !window.originalVideo.paused && !window.originalVideo.muted"), 'selection retains playback and audio');
  await assertGrid(2, 'selecting the first stream');

  await layout('hidden');
  await assertGrid(2, 'hidden chat');
  await layout('docked');
  await assertGrid(2, 'docked chat');
  assert.ok(await evaluate("[...document.querySelectorAll('.player')].every(p=>{const c=p.querySelector('.chat').getBoundingClientRect(),v=p.querySelector('video').getBoundingClientRect(),m=p.querySelector('.video-chat').getBoundingClientRect();return c.height>0 && c.y>=v.bottom-1 && c.bottom<=m.bottom+1})"), 'docked chat stays inside its media tile');
  await layout('overlay');
  assert.ok(await evaluate("[...document.querySelectorAll('.player')].every(p=>{const c=p.querySelector('.chat').getBoundingClientRect(),s=p.querySelector('.video-surface').getBoundingClientRect();return c.x>=s.x && c.right<=s.right && c.y>=s.y && c.bottom<=s.bottom})"), 'overlay chat stays inside each video');

  const click = async (selector, count = 1) => {
    const query = `document.querySelector(${JSON.stringify(selector)})`;
    const point = await evaluate(`(() => {
      const r = ${query}.getBoundingClientRect();
      return {x:r.x+r.width/2,y:r.y+r.height/2};
    })()`);
    await cdp('Input.dispatchMouseEvent', { type: 'mouseMoved', ...point });
    await wait(`${query}.contains(document.elementFromPoint(${point.x},${point.y}))`);
    for (let clickCount = 1; clickCount <= count; clickCount++) {
      await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', ...point, button: 'left', clickCount });
      await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', ...point, button: 'left', clickCount });
    }
  };
  const gridFullscreen = "document.fullscreenElement === document.querySelector('#players')";
  const focusTile = async (tile, docked = false) => {
    await click(tile + ' video', 2);
    await wait(`document.fullscreenElement === document.querySelector('${tile}') && document.querySelector('#players').matches(':fullscreen')`);
    assert.ok(await evaluate(`(() => {
      const player = document.fullscreenElement;
      const bounds = player.querySelector('.video-chat').getBoundingClientRect();
      const video = player.querySelector('video');
      const frame = video.getBoundingClientRect();
      const chat = player.querySelector('.chat').getBoundingClientRect();
      return bounds.width === innerWidth && bounds.height === innerHeight &&
        (${docked} ? chat.left === frame.right && chat.right === bounds.right && chat.height === bounds.height
          : frame.width === bounds.width) && frame.height >= innerHeight * .9 &&
        document.elementFromPoint(frame.x + frame.width / 2, frame.y + frame.height / 2) === video;
    })()`), 'the clicked stream fills the fullscreen viewport above the other tiles');
  };

  await cdp('Page.bringToFront');
  await metrics();
  const point = await evaluate("(()=>{const r=document.querySelector('[data-action=fullscreen]').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()");
  await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', ...point, button: 'left', clickCount: 1 });
  await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', ...point, button: 'left', clickCount: 1 });
  await wait(gridFullscreen);
  assert.ok(await evaluate("[...document.querySelectorAll('.channel-information')].every(e=>getComputedStyle(e).display==='none') && [...document.querySelectorAll('.video-chat')].every(e=>{const r=e.getBoundingClientRect();return r.top>=0 && r.bottom<=innerHeight+1})"), 'fullscreen fits videos and omits channel details');
  assertTilesTouch(await metrics(), 'fullscreen');
  await capture('fullscreen');
  await cdp('Emulation.setDeviceMetricsOverride', { ...desktopViewport, deviceScaleFactor: 1.25 });
  const scaled = await metrics();
  const [tile] = scaled.media;
  const [video] = scaled.videos;
  const letterboxHeight = (tile.h - tile.w * video.frameHeight / video.frameWidth) / 2;
  assert.ok(letterboxHeight > 20, 'the fixture has black letterboxing on both sides of the fullscreen seam');
  const letterboxPixels = await readPixels({ x: Math.floor(tile.right) - 4, y: Math.round(tile.y + letterboxHeight / 2), width: 9, height: 1 });
  assert.ok(letterboxPixels.every((value, i) => i % 4 === 3 || value === 0), 'both sides of the fullscreen seam render solid black at 125% scaling');
  await cdp('Emulation.setDeviceMetricsOverride', desktopViewport);
  await metrics();
  await evaluate("window.gridVideos = [...document.querySelectorAll('#players video')]");
  await focusTile('.player:last-child');
  await wait("document.fullscreenElement.querySelector('[data-action=fullscreen]').ariaLabel === 'Back to fullscreen multiview'");
  await click('.player:last-child [data-action=fullscreen]');
  await wait(gridFullscreen);
  await focusTile('.player:first-child');
  await cdp('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await cdp('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await wait(gridFullscreen);
  assertTilesTouch(await metrics(), 'restored fullscreen');
  assert.ok(await evaluate(`(() => {
    const videos = [...document.querySelectorAll('#players video')];
    return videos.length === gridVideos.length && videos.every((video, i) => video === gridVideos[i] && !video.paused) &&
      !videos[0].muted && videos[1].muted;
  })()`), 'restoring the grid preserves both playing videos and the clicked stream selection');
  await click('.player:first-child [data-action=fullscreen]');
  await wait('!document.fullscreenElement');

  await layout('docked');
  await click('.player:first-child [data-action=fullscreen]');
  await wait(gridFullscreen);
  await focusTile('.player:last-child', true);
  await cdp('Input.dispatchKeyEvent', { type: 'keyDown', key: 'm', code: 'KeyM', windowsVirtualKeyCode: 77 });
  await cdp('Input.dispatchKeyEvent', { type: 'keyUp', key: 'm', code: 'KeyM', windowsVirtualKeyCode: 77 });
  await wait(`document.fullscreenElement === document.querySelector('.player:last-child') &&
    !document.querySelector('#players').matches('.grid, :fullscreen') &&
    !document.querySelector('.player:last-child .multistream-toggle').disabled`);
  await click('.player:last-child [data-action=fullscreen]');
  await wait('!document.fullscreenElement');
  await toggle(true);
  await layout('overlay');

  for (const count of [3, 4, 5]) {
    await open('stream' + count);
    const label = count + ' streams';
    assertPicturesFillTiles(await assertGrid(count, label), label);
  }
  await capture('five-streams');
  await assertRenderedSeams(await metrics(), 'five streams');
  for (const deviceScaleFactor of [1.25, 1.5, 1.75]) {
    await cdp('Emulation.setDeviceMetricsOverride', { ...desktopViewport, deviceScaleFactor });
    await assertRenderedSeams(await metrics(), 'five streams at scale ' + deviceScaleFactor);
  }
  await cdp('Emulation.setDeviceMetricsOverride', { ...desktopViewport, width: 1440, height: 999, deviceScaleFactor: 1.25 });
  await click('.player:first-child [data-action=fullscreen]');
  await wait(gridFullscreen);
  const fullscreenFive = await metrics();
  assertPicturesFillTiles(fullscreenFive, 'five streams in fullscreen');
  assertTilesTouch(fullscreenFive, 'five streams in fullscreen');
  assert.ok(Math.abs(fullscreenFive.media[0].y + fullscreenFive.media.at(-1).bottom - fullscreenFive.height) < fullscreenFive.pixelSize * 2, 'fullscreen centers the video rows vertically');
  await capture('five-streams-fullscreen');
  await assertRenderedSeams(fullscreenFive, 'five streams in fullscreen');
  await click('.player:first-child [data-action=fullscreen]');
  await wait('!document.fullscreenElement');

  await layout('docked');
  const dockedFive = await assertGrid(5, 'five streams with docked chat');
  assertPicturesFillTiles(dockedFive, 'five streams with docked chat');
  assert.ok(await evaluate("[...document.querySelectorAll('.player')].every(p=>{const c=p.querySelector('.chat').getBoundingClientRect(),v=p.querySelector('video').getBoundingClientRect();return c.height>0 && c.top===v.bottom && c.width===v.width})"), 'docked chats sit directly below their videos');
  await assertRenderedSeams(dockedFive, 'five streams with docked chat', true);
  await layout('hidden');
  await assertRenderedSeams(await assertGrid(5, 'five streams with hidden chat'), 'five streams with hidden chat');
  await layout('overlay');
  for (const width of [320, 390, 650, 651, 768, 1440]) {
    await viewport(width);
    const label = 'five streams at ' + width;
    const grid = await assertGrid(5, label);
    assertPicturesFillTiles(grid, label);
    if (width === 390 || width === 1440) await assertRenderedSeams(grid, label);
  }
  await capture('responsive-grid');
  for (let count = 6; count <= 16; count++) await open('stream' + count);
  assertPicturesFillTiles(await assertGrid(16, 'sixteen streams'), 'sixteen streams');

  await toggle(false);
  assert.ok(await evaluate("document.querySelectorAll('.player:not([hidden])').length===1 && [...document.querySelectorAll('.channel-information')].every(e=>e.parentElement.classList.contains('screen')) && document.querySelector('video')===window.originalVideo && document.querySelector('.channel-information')===window.originalInfo"), 'single view restores the same video and channel DOM');
  await toggle(true);
  for (let count = 16; count > 1; count--) {
    await evaluate("document.querySelector('.player:last-child [data-action=close]').click()");
    await wait(`document.querySelectorAll('#players video').length===${count - 1}`);
  }
  await assertGrid(1, 'closing down to one stream');
  await open('SquareOne');
  await open('SquareTwo');
  const mixed = await assertGrid(3, 'mixed widescreen and square streams');
  assert.equal(mixed.videos[0].frameWidth / mixed.videos[0].frameHeight, 16 / 9);
  assert.ok(mixed.videos.slice(1).every(v => v.frameWidth === v.frameHeight && v.frameWidth > 0));
  assert.ok(mixed.videos.every(v => v.fit === 'contain' && v.w === v.h), 'the decoded majority ratio sizes the grid and the widescreen image stays uncropped');
  await evaluate("document.querySelector('.player:first-child [data-action=close]').click()");
  await wait("document.querySelectorAll('#players video').length===2");
  await open('SquareThree');
  assertPicturesFillTiles(await assertGrid(3, 'three square streams'), 'three square streams');
  assert.deepEqual(await evaluate('window.visualErrors'), []);
  fs.writeFileSync(path.join(output, 'measurements.json'), JSON.stringify(measurements, null, 2));
  console.log('PASS multi-stream video seams at several display scales; full source frames; mixed aspect ratios; visible keyboard focus; descriptions below every video row; 1-16 streams; responsive layouts; docked/overlay/hidden chat; fullscreen and grid restoration; selection/audio; DOM preservation; close and single-view recovery.');
};
