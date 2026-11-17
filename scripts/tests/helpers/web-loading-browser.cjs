'use strict';
const assert = require('node:assert/strict');
module.exports = async ({ evaluate, wait, capture, viewport, fixture }) => {
  const settled = "document.querySelector('#cards').getAttribute('aria-busy')==='false'";
  const click = selector => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
  const until = async predicate => {
    for (let index = 0; index < 100 && !predicate(); index++) await new Promise(resolve => setTimeout(resolve, 20));
    assert.ok(predicate(), 'the expected provider request or cancellation must arrive');
  };
  await evaluate("window.originalCard=document.querySelector('.card');document.querySelector('#live-filter').click()");
  const before = fixture.requests.filter(action => action === 'following').length;
  await click('[data-page=following]');
  assert.equal(fixture.requests.filter(action => action === 'following').length, before);
  assert.equal(await evaluate("document.querySelector('#live-filter').checked"), true);
  assert.equal(await evaluate("document.querySelectorAll('.card').length"), 4);
  await evaluate("window.originalCard=document.querySelector('.card')");
  await click('[data-page=following]');
  assert.ok(await evaluate("document.querySelector('.card')===window.originalCard"), 'same-page navigation keeps existing controls');
  await click('#refresh');
  await until(() => fixture.requests.filter(action => action === 'following').length === before + 1);
  await wait(settled);
  assert.ok(await evaluate("document.querySelector('.card')===window.originalCard"), 'unchanged refresh reuses existing cards');

  for (const width of [1440, 390]) {
    await viewport(width);
    fixture.hold.add('categories');
    await click('[data-page=discover]');
    await until(() => fixture.held.some(item => item.action === 'categories' && !item.response.destroyed));
    await wait("document.querySelector('#cards').getAttribute('aria-busy')==='true'");
    const cancellations = fixture.canceled;
    await click('[data-page=downloads]');
    await wait(settled + " && document.querySelector('.card h3')?.textContent==='Ready to watch'");
    await until(() => fixture.canceled > cancellations);
    assert.equal(await evaluate("document.querySelector('#notice').hidden"), true, 'abandoned requests produce no error notice');
    assert.equal(await evaluate("document.documentElement.scrollWidth<=document.documentElement.clientWidth"), true);
    await capture('loading-navigation-' + width);
    fixture.hold.delete('categories');
  }
  fixture.hold.add('categories');
  await click('[data-page=discover]');
  await until(() => fixture.held.some(item => !item.response.destroyed));
  const cancellations = fixture.canceled;
  await click('[data-page=settings]');
  await wait("!document.querySelector('#preferences').hidden && " + settled);
  await until(() => fixture.canceled > cancellations);
  fixture.hold.delete('categories');
  const categories = Array.from({ length: 125 }, (_, index) => ({ id: 'render-' + index, name: 'Category ' + index,
    viewerCount: index * 100, thumbnailUrl: 'https://artwork.fixture/code' }));
  let reverse = false;
  fixture.categories = input => input.cursor ? { items: categories.slice(100) }
    : { items: reverse ? categories.slice(0, 100).reverse() : categories.slice(0, 100), nextCursor: 'next' };
  await click('[data-page=discover]');
  await wait(settled + " && document.querySelectorAll('.card').length===100");
  await evaluate(`window.renderCardsBefore=[...document.querySelectorAll('.card')];
    window.renderFocus=window.renderCardsBefore[3].querySelector('.card-main');window.renderFocus.focus({preventScroll:true});
    window.cardMutations={added:0,removed:0};
    window.cardObserver=new MutationObserver(records=>{for(const record of records){window.cardMutations.added+=record.addedNodes.length;window.cardMutations.removed+=record.removedNodes.length;}});
    window.cardObserver.observe(document.querySelector('#cards'),{childList:true});`);
  await click('#more');
  await wait(settled + " && document.querySelectorAll('.card').length===125");
  assert.deepEqual(await evaluate('window.cardMutations'), { added: 25, removed: 0 }, 'pagination creates only the new cards');
  assert.ok(await evaluate("window.renderCardsBefore.every((card,index)=>document.querySelectorAll('.card')[index]===card)"));
  assert.ok(await evaluate('document.activeElement===window.renderFocus'), 'pagination preserves focus');
  await evaluate('window.cardObserver.disconnect()');
  reverse = true;
  await click('#refresh');
  await wait(settled + " && document.querySelectorAll('.card').length===100 && document.querySelector('.card').dataset.key==='render-99'");
  assert.ok(await evaluate("window.renderCardsBefore.every((card,index)=>document.querySelectorAll('.card')[99-index]===card)"), 'sorting reuses the existing cards');
  assert.ok(await evaluate('document.activeElement===window.renderFocus'), 'sorting restores focus after moving its card');
  assert.equal(await evaluate('window.visualErrors.length'), 0);
  console.log('PASS real-browser loading: canceled slow directories, desktop/phone navigation, unchanged refresh, 100 retained cards plus 25 new cards with zero removals, reordered cards retain focus, settings cancellation, zero JavaScript errors.');
};
