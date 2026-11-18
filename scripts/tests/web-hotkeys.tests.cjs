'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/web-player-fixture.cjs');

test('shortcut recording and restored bindings preserve letters whose lowercase expands', () => {
  const f = fixture();
  try {
    const binding = f.normalizeHotkey({ key: 'İ' });
    assert.ok(binding, 'the Turkish dotted capital I is one valid shortcut character');
    assert.equal(binding.key, 'İ');
    const restored = f.loadHotkeys({ mute: binding }).mute;
    const pressed = f.hotkeyFromEvent({ key: 'İ' });
    assert.deepEqual(restored, pressed, 'a saved shortcut must match the same keyboard input');
  } finally { f.disconnect(); }
});

test('shortcut normalization retains case folding, named keys and invalid-character checks', () => {
  const f = fixture();
  try {
    for (const [input, expected] of [['A', 'a'], ['É', 'é'], ['𐐀', '𐐨'], ['🎮', '🎮'], ['F1', 'F1'], ['F24', 'F24']])
      assert.equal(f.normalizeHotkey({ key: input }).key, expected);
    for (const key of ['', 'ab', 'F01', 'F25', ' ', '\u200d', '\ue000', '\ud800'])
      assert.equal(f.normalizeHotkey({ key }), null, JSON.stringify(key));
    assert.equal(f.loadHotkeys({ mute: { key: 'F' } }).mute, null, 'reserved keys still apply after case folding');
  } finally { f.disconnect(); }
});

test('desktop gestures map to browser keys, and explicit account bindings take precedence', () => {
  const f = fixture();
  try {
    const bindings = f.loadHotkeys({ back: { key: 'm' }, togglePause: null }, {
      back: 'Ctrl+Mouse5', nextTab: 'Win+Shift+D1', previousTab: 'Alt+OemOpenBrackets'
    });
    assert.equal(bindings.back.key, 'm');
    assert.equal(bindings.grid, null, 'explicit override wins over an inherited desktop shortcut');
    assert.equal(bindings.togglePause, null, 'cleared shortcuts stay disabled');
    assert.equal(bindings.nextTab.code, 'Digit1');
    assert.equal(bindings.nextTab.key, '!');
    assert.equal(bindings.nextTab.meta, true);
    assert.equal(bindings.previousTab.key, '[');
    assert.equal(bindings.previousTab.alt, true);
    assert.equal(bindings.mute.key, 'm');
    assert.equal(bindings.mute.shift, true);
    for (const key of ['4', 'ArrowLeft'])
      assert.equal(bindings.skipBackward.key, f.hotkeyFromEvent({ key, code: 'Numpad4' }).key);
    assert.equal(f.loadHotkeys().togglePause.key, f.hotkeyFromEvent({ key: ' ', code: 'Space' }).key);
  } finally { f.disconnect(); }
});
