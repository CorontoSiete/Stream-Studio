'use strict';

const assert = require('node:assert/strict');

module.exports = async ({ cdp, evaluate, wait, capture, viewport, address }) => {
  const layout = () => evaluate(`(() => {
    const sidebar = document.querySelector('#studio-sidebar').getBoundingClientRect();
    const workspace = document.querySelector('.workspace').getBoundingClientRect();
    const toggle = document.querySelector('#sidebar-toggle');
    return {
      sidebarWidth: sidebar.width,
      workspaceLeft: workspace.left,
      toggleLabel: toggle.getAttribute('aria-label'),
      expanded: toggle.getAttribute('aria-expanded'),
      channelsVisible: !!document.querySelector('#channels-toggle').getClientRects().length,
      overflow: document.documentElement.scrollWidth > innerWidth
    };
  })()`);
  const verifyHeaderLibraryNav = async () => {
    assert.deepEqual(await evaluate(`(() => {
      const header = document.querySelector('.topbar');
      const brandRight = header.querySelector('#brand').getBoundingClientRect().right;
      const headerRight = header.getBoundingClientRect().right;
      return [...header.querySelectorAll('.topbar-actions .library-nav button')].filter(button => {
        const rect = button.getBoundingClientRect();
        return button.getAttribute('aria-label') && rect.width >= 44 && rect.height >= 44 && rect.left >= brandRight && rect.right <= headerRight;
      }).map(button => button.dataset.page);
    })()`), ['recent', 'downloads', 'settings'], 'all library buttons fit on the right side of the header');
  };
  const clickToggle = async () => {
    const point = await evaluate(`(() => {
      const r = document.querySelector('#sidebar-toggle').getBoundingClientRect();
      return { x: r.x + r.width / 2, y: r.y + r.height / 2 };
    })()`);
    await cdp('Input.dispatchMouseEvent', { type: 'mouseMoved', ...point });
    await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', button: 'left', clickCount: 1, ...point });
    await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', button: 'left', clickCount: 1, ...point });
  };

  assert.deepEqual(await layout(), {
    sidebarWidth: 240, workspaceLeft: 240, toggleLabel: 'Collapse sidebar',
    expanded: 'true', channelsVisible: false, overflow: false
  });
  assert.equal(await evaluate("document.querySelector('#sidebar-toggle').getAttribute('aria-controls')"), 'studio-sidebar');
  await verifyHeaderLibraryNav();
  assert.equal(await evaluate("document.querySelector('#studio-sidebar .library-nav') === null"), true);
  await capture('sidebar-expanded');
  await cdp('Emulation.setDeviceMetricsOverride', { width: 1440, height: 600, deviceScaleFactor: 1, mobile: false });
  await evaluate("window.__sidebarResizeMarker = true");
  await cdp('Page.reload');
  await wait("document.readyState === 'complete' && !window.__sidebarResizeMarker");
  await wait("document.querySelectorAll('#followed-channels .followed-channel').length === 6");
  await capture('sidebar-scrollbar');
  const followedList = await evaluate(`(() => {
    const list = document.querySelector('#followed-channels');
    const heading = document.querySelector('#followed-heading').getBoundingClientRect();
    const rect = list.getBoundingClientRect();
    return {
      top: rect.top,
      headingTop: heading.top,
      headingBottom: heading.bottom,
      firstRowTop: list.firstElementChild.getBoundingClientRect().top,
      overflow: list.scrollHeight - list.clientHeight,
      gutter: list.offsetWidth - list.clientWidth,
      x: rect.left + rect.width / 2,
      y: rect.top + rect.height / 2
    };
  })()`);
  assert.ok(followedList.overflow > 0 && followedList.gutter > 0, 'the channel rows have their own scrollbar');
  assert.ok(followedList.top >= followedList.headingBottom && followedList.top === followedList.firstRowTop,
    'the scrollable list starts at the first row below the heading');
  await cdp('Input.dispatchMouseEvent', { type: 'mouseMoved', x: followedList.x, y: followedList.y });
  await cdp('Input.dispatchMouseEvent', { type: 'mouseWheel', x: followedList.x, y: followedList.y, deltaX: 0, deltaY: followedList.overflow });
  await wait("document.querySelector('#followed-channels').scrollTop > 0");
  assert.equal(await evaluate("document.querySelector('#followed-heading').getBoundingClientRect().top"), followedList.headingTop,
    'scrolling to later channels keeps the heading in place');
  await viewport(1440);
  await clickToggle();
  await wait("document.querySelector('#studio').classList.contains('sidebar-collapsed')");
  assert.deepEqual(await layout(), {
    sidebarWidth: 68, workspaceLeft: 68, toggleLabel: 'Expand sidebar',
    expanded: 'false', channelsVisible: true, overflow: false
  });
  assert.ok(await evaluate("[...document.querySelectorAll('.sidebar nav button[data-page]')].every(b => b.getClientRects().length && b.title && getComputedStyle(b.querySelector('span:nth-child(2)')).display === 'none')"), 'compact browsing navigation keeps visible, named icon buttons');
  assert.equal(await evaluate("getComputedStyle(document.querySelector('#studio-sidebar .followed-section')).display"), 'none');
  assert.equal(await evaluate("getComputedStyle(document.querySelector('#open-player-count')).display"), 'none', 'no empty stream badge is shown');
  assert.equal(await evaluate("localStorage.getItem('streamstudio.sidebarCollapsed')"), 'true');
  await verifyHeaderLibraryNav();
  await capture('sidebar-collapsed');

  await evaluate("document.querySelector('.sidebar [data-page=discover]').click()");
  await wait("document.querySelector('#studio').dataset.page==='discover' && document.querySelector('#cards').getAttribute('aria-busy')==='false'");
  assert.equal((await layout()).workspaceLeft, 68, 'collapse survives page navigation');
  await evaluate("document.querySelector('#channels-toggle').click()");
  await wait("document.querySelector('#channels-dialog').open && document.querySelectorAll('#channels-dialog .followed-channel').length===6");
  assert.equal(await evaluate("getComputedStyle(document.querySelector('#channels-dialog .followed-section')).display"), 'block', 'the full channel list remains available in the stream dialog');
  await evaluate("document.querySelector('#close-channels').click()");
  await wait("!document.querySelector('#channels-dialog').open");

  await viewport(901);
  assert.deepEqual((await layout()).sidebarWidth, 68);
  assert.equal((await layout()).overflow, false, 'the compact layout fits the smallest desktop width');
  await viewport(1440);
  await evaluate("window.__sidebarReloadMarker=true");
  await cdp('Page.navigate', { url: address });
  await wait("document.readyState==='complete' && !window.__sidebarReloadMarker && !document.querySelector('#studio').hidden && document.querySelector('#cards').getAttribute('aria-busy')==='false'");
  assert.deepEqual(await layout(), {
    sidebarWidth: 68, workspaceLeft: 68, toggleLabel: 'Expand sidebar',
    expanded: 'false', channelsVisible: true, overflow: false
  });

  await evaluate("document.querySelector('#channels-toggle').click()");
  await wait("document.querySelector('#channels-dialog').open");
  await evaluate("document.querySelector('#channels-dialog button.followed-channel').click()");
  await wait("document.querySelector('#studio').classList.contains('watching') && !document.querySelector('#channels-dialog').open && !!document.querySelector('.player:not([hidden]) video')");
  assert.ok(await evaluate("document.querySelector('.player:not([hidden]) video').getBoundingClientRect().width > 1000"), 'watch page uses the wider workspace');
  assert.ok((await layout()).channelsVisible, 'the stream switcher remains available while watching');
  await capture('sidebar-collapsed-watch');
  await evaluate("document.querySelector('.player:not([hidden]) [data-action=theater]').click()");
  await wait("document.querySelector('#studio').classList.contains('theater')");
  assert.equal(await evaluate("getComputedStyle(document.querySelector('#studio-sidebar')).display"), 'none');
  await evaluate("document.querySelector('.player:not([hidden]) [data-action=theater]').click()");
  await wait("!document.querySelector('#studio').classList.contains('theater')");
  assert.equal((await layout()).sidebarWidth, 68, 'leaving theater restores the chosen rail width');

  await viewport(800);
  assert.equal((await layout()).sidebarWidth, 68, 'tablet keeps its existing icon rail');
  assert.equal(await evaluate("document.querySelector('#sidebar-toggle').getClientRects().length"), 0);
  await viewport(375);
  const phone = await evaluate("(() => { const sidebar=document.querySelector('#studio-sidebar').getBoundingClientRect(); return { sidebarWidth: sidebar.width, sidebarBottom: sidebar.bottom, clientWidth: document.documentElement.clientWidth, viewportHeight: innerHeight, workspaceLeft: document.querySelector('.workspace').getBoundingClientRect().left, scrollWidth: document.documentElement.scrollWidth }; })()");
  assert.ok(phone.sidebarWidth === phone.clientWidth && phone.sidebarBottom === phone.viewportHeight && phone.workspaceLeft === 0 && phone.scrollWidth <= phone.clientWidth, 'phone keeps its bottom navigation and full-width workspace: ' + JSON.stringify(phone));
  await viewport(320);
  await evaluate("document.querySelector('.topbar [data-page=recent]').click()");
  await wait("document.querySelector('#studio').dataset.page==='recent' && document.querySelector('#cards').getAttribute('aria-busy')==='false'");
  assert.equal(await evaluate("document.querySelector('.library-nav [aria-current=page]').dataset.page"), 'recent');
  await verifyHeaderLibraryNav();
  await viewport(1440);
  await clickToggle();
  await wait("!document.querySelector('#studio').classList.contains('sidebar-collapsed')");
  assert.equal((await layout()).sidebarWidth, 240);
  assert.equal(await evaluate("document.querySelector('.topbar .library-nav [aria-current=page]').dataset.page"), 'recent');
  assert.equal(await evaluate("localStorage.getItem('streamstudio.sidebarCollapsed')"), 'false');
  await verifyHeaderLibraryNav();
  assert.deepEqual(await evaluate('window.visualErrors'), []);
  console.log('PASS sidebar: channel row scrollbar; real button input; compact navigation; stream access; page changes; reload persistence; watch/theater; tablet/phone layouts; expansion; zero JavaScript errors.');
};
