'use strict';
const assert = require('node:assert/strict');

module.exports = async ({ cdp, evaluate, wait, settingsFixture, getSavedSettings }) => {
  async function waitForHeldSave() {
    for (let n = 0; n < 120 && !settingsFixture.held.length; n++) await new Promise(resolve => setTimeout(resolve, 50));
    assert.equal(settingsFixture.held.length, 1, 'first account save is in flight');
  }

  await evaluate("document.querySelector('[data-page=settings]').click()");
  await wait("!document.querySelector('#preferences').hidden");
  assert.equal(await evaluate("document.querySelector('#save-status').textContent"), 'All changes saved to your account.');

  const savesBefore = settingsFixture.saves;
  await evaluate("document.querySelector('#chat-size').value='24';document.querySelector('#chat-size').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#keep-inactive').checked=true;document.querySelector('#keep-inactive').dispatchEvent(new Event('change',{bubbles:true}))");
  await wait("document.querySelector('#save-status').textContent==='All changes saved to your account.'");
  assert.equal(getSavedSettings().size, 24);
  assert.equal(getSavedSettings().keepInactive, true);
  assert.equal(settingsFixture.saves, savesBefore + 1, 'rapid account preference edits are coalesced');
  assert.equal(await evaluate("localStorage.getItem('stream-studio-preferences')"), null);

  // A slider can dispatch change after a pause has already saved its input value.
  await evaluate("document.querySelector('#chat-size').dispatchEvent(new Event('change',{bubbles:true}))");
  await evaluate("document.querySelector('#default-quality').value='720p';document.querySelector('#default-quality').dispatchEvent(new Event('change',{bubbles:true}));document.querySelector('#low-latency').checked=false;document.querySelector('#low-latency').dispatchEvent(new Event('change',{bubbles:true}))");
  await wait("document.querySelector('#save-status').textContent==='All changes saved to your account.'");
  assert.equal(getSavedSettings().quality, '720p');
  assert.equal(getSavedSettings().lowLatency, false);
  assert.deepEqual(settingsFixture.lastPatch, { quality: '720p', lowLatency: false });

  settingsFixture.holdNext = true;
  await evaluate("document.querySelector('#default-quality').value='best';document.querySelector('#default-quality').dispatchEvent(new Event('change',{bubbles:true}))");
  await waitForHeldSave();
  await evaluate("document.querySelector('#default-quality').value='720p';document.querySelector('#default-quality').dispatchEvent(new Event('change',{bubbles:true}));document.querySelector('#low-latency').checked=true;document.querySelector('#low-latency').dispatchEvent(new Event('change',{bubbles:true}))");
  settingsFixture.held.shift().send();
  await wait("document.querySelector('#save-status').textContent==='All changes saved to your account.'");
  assert.equal(getSavedSettings().quality, '720p', 'latest edit wins after an in-flight save');
  assert.equal(getSavedSettings().lowLatency, true);

  const beforeRevertedEdits = settingsFixture.saves;
  settingsFixture.holdNext = true;
  await evaluate("document.querySelector('#chat-opacity').value='85';document.querySelector('#chat-opacity').dispatchEvent(new Event('input',{bubbles:true}))");
  await waitForHeldSave();
  await evaluate("const opacity = document.querySelector('#chat-opacity'); opacity.value='90';opacity.dispatchEvent(new Event('input',{bubbles:true}));opacity.value='85';opacity.dispatchEvent(new Event('input',{bubbles:true}))");
  settingsFixture.held.shift().send();
  await wait("document.querySelector('#save-status').textContent==='All changes saved to your account.'");
  assert.equal(settingsFixture.saves, beforeRevertedEdits + 1, 'the in-flight save already matches the final edit');

  settingsFixture.failSave = true;
  await evaluate("document.querySelector('#low-latency').checked=false;document.querySelector('#low-latency').dispatchEvent(new Event('change',{bubbles:true}));document.querySelector('[data-page=following]').click()");
  await wait("document.querySelector('#save-status').textContent.includes('Couldn’t save your preferences')");
  assert.equal(await evaluate("document.querySelector('#studio').dataset.page"), 'following', 'leaving Preferences does not cancel the save');
  await evaluate("document.querySelector('[data-page=settings]').click()");
  assert.equal(await evaluate("document.querySelector('#retry-settings').hidden"), false);
  await evaluate("document.querySelector('#chat-size').value='25';document.querySelector('#chat-size').dispatchEvent(new Event('input',{bubbles:true}))");
  settingsFixture.failSave = false;
  await evaluate("document.querySelector('#retry-settings').click()");
  await wait("document.querySelector('#save-status').textContent==='All changes saved to your account.'");
  assert.equal(getSavedSettings().size, 25);
  assert.equal(getSavedSettings().lowLatency, false);
  assert.equal(await evaluate("document.querySelector('#retry-settings').hidden"), true);

  await evaluate("window.originalSetItem=Storage.prototype.setItem;Storage.prototype.setItem=function(){throw new DOMException('Blocked','SecurityError')};document.querySelector('#chat-size').value='26';document.querySelector('#chat-size').dispatchEvent(new Event('input',{bubbles:true}))");
  await wait("document.querySelector('#save-status').textContent==='All changes saved to your account.'");
  assert.equal(getSavedSettings().size, 26, 'browser storage cannot prevent account persistence');
  await evaluate("Storage.prototype.setItem=window.originalSetItem");

  await cdp('Page.reload');
  await wait("!document.querySelector('#studio').hidden && document.querySelector('#cards').getAttribute('aria-busy')==='false'");
  await evaluate("document.querySelector('[data-page=settings]').click()");
  await wait("!document.querySelector('#preferences').hidden");
  assert.equal(await evaluate("Number(document.querySelector('#chat-size').value)"), 26);
  assert.equal(await evaluate("document.querySelector('#keep-inactive').checked"), true);
  assert.deepEqual(await evaluate('window.visualErrors'), []);
  console.log('PASS account preferences coalesce, merge in-flight edits, report failures, retry and restore across reload without browser storage.');
};
