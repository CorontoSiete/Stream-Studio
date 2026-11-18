'use strict';
const assert = require('node:assert/strict');

module.exports = async ({ cdp, evaluate, wait }) => {
  const point = (selector, fraction = 0.5) => evaluate(`(() => {
    const r = document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();
    return { x: Math.round(r.left + r.width * ${fraction}), y: Math.round(r.top + r.height / 2) };
  })()`);
  const mouse = (type, position, buttons = 0) => cdp('Input.dispatchMouseEvent', {
    type, ...position, buttons, ...(type === 'mouseMoved' ? {} : { button: 'left', clickCount: 1 })
  });
  const click = async position => {
    await mouse('mouseMoved', position);
    await mouse('mousePressed', position, 1);
    await mouse('mouseReleased', position);
  };
  const key = async (key, windowsVirtualKeyCode) => {
    for (const type of ['keyDown', 'keyUp']) {
      await cdp('Input.dispatchKeyEvent', { type, key, code: key, windowsVirtualKeyCode });
    }
  };
  const video = "document.querySelector('.video-surface video')";
  const seek = "document.querySelector('.video-seek')";
  const previewHidden = "document.querySelector('.seek-preview').hidden";
  const style = "getComputedStyle(document.querySelector('.player-controls'))";
  const visible = `${style}.opacity === '1' && ${style}.pointerEvents === 'auto'`;
  const hidden = `${style}.opacity === '0' && ${style}.pointerEvents === 'none'`;

  await evaluate("document.querySelector('#open-dialog').showModal();document.querySelector('#open-query').value='Northstar'");
  await click(await point('#open-form button[type=submit]'));
  await wait(`${video}?.videoWidth > 0 && ${video}.currentTime > 0.1 && !${seek}.disabled && !document.querySelector('#open-dialog').open`);
  await evaluate('scrollTo(0,0);new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');
  const inside = await point('.video-surface');
  const outside = { x: 10, y: inside.y };
  assert.equal(await evaluate(`document.activeElement === ${video}`), true, 'the normal open flow focuses the video');
  await mouse('mouseMoved', inside);
  await wait(visible);
  await wait(hidden);
  assert.equal(await evaluate(`document.activeElement === ${video} && !${video}.paused`), true, 'idle controls hide while focused video keeps playing');

  await mouse('mouseMoved', { x: inside.x + 5, y: inside.y });
  await wait(visible);
  await mouse('mouseMoved', outside);
  assert.equal(await evaluate(`${style}.pointerEvents`), 'none', 'leaving the video disables the controls immediately');
  await wait(hidden);

  await mouse('mouseMoved', inside);
  await wait(visible);
  const scrub = await point('.video-seek', 0.25);
  await mouse('mouseMoved', scrub);
  await wait(`!${previewHidden}`);
  await evaluate('new Promise(resolve=>setTimeout(resolve,2700))');
  assert.equal(await evaluate(`${visible} && !${previewHidden}`), true, 'a stationary seek hover keeps the preview available');
  await mouse('mouseMoved', outside);
  await wait(`${hidden} && ${previewHidden}`);

  await mouse('mouseMoved', inside);
  await wait(visible);
  await mouse('mouseMoved', scrub);
  const beforeSeek = await evaluate(`${video}.currentTime`);
  await mouse('mousePressed', scrub, 1);
  await wait(`${video}.currentTime < ${beforeSeek - 5}`);
  await mouse('mouseMoved', outside, 1);
  const dragValue = await evaluate(`Number(${seek}.value)`);
  const dragTime = await evaluate(`${video}.currentTime`);
  await wait(`${video}.currentTime > ${dragTime + 1}`);
  assert.equal(await evaluate(`Number(${seek}.value)`), dragValue, 'holding the seek gesture keeps its displayed position steady');
  assert.equal(await evaluate(visible), true, 'controls stay available during a drag outside the video');
  await mouse('mouseReleased', outside);
  await mouse('mouseMoved', { x: outside.x + 1, y: outside.y });
  await wait(`${hidden} && ${previewHidden} && Math.abs(Number(${seek}.value) - ${video}.currentTime) < 0.5`);
  assert.equal(await evaluate(`document.activeElement === ${seek}`), true, 'mouse focus stays on the seekbar without pinning the controls');

  await click(inside);
  await mouse('mouseMoved', outside);
  await wait(hidden);
  await key('Tab', 9);
  await wait(`document.activeElement === ${seek} && ${visible} && !${previewHidden}`);
  await key('Home', 36);
  await wait(`!${video}.seeking && ${video}.currentTime < 1`);

  await mouse('mouseMoved', inside);
  const settings = await point('.playback-settings summary');
  await click(settings);
  await mouse('mouseMoved', outside);
  await wait(`document.querySelector('.playback-settings').open && ${visible}`);
  await click(settings);
  await mouse('mouseMoved', outside);
  await wait(hidden);

  await mouse('mouseMoved', inside);
  await wait(visible);
  const play = await point('.player-controls [data-action=play]');
  await click(play);
  await wait(`${video}.paused`);
  await mouse('mouseMoved', outside);
  assert.equal(await evaluate(visible), true, 'paused playback keeps its controls available');
  await click(play);
  await wait(`!${video}.paused`);
  await mouse('mouseMoved', outside);
  await wait(hidden);

  await cdp('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
  await cdp('Emulation.setTouchEmulationEnabled', { enabled: true });
  await evaluate(`${video}.pause(); scrollTo(0,0)`);
  await wait(visible);
  const touch = await point('.video-seek');
  await cdp('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [touch] });
  await cdp('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await wait(`!${video}.seeking && Math.abs(${video}.currentTime - Number(${seek}.max) / 2) < 1 && ${previewHidden}`);
  assert.deepEqual(await evaluate('window.visualErrors'), []);
  console.log('PASS playback controls hide after idle or mouse exit, recover after scrubbing, and support seek previews, keyboard and touch seeking, settings and pause.');
};
