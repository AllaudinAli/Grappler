// Grappler for Brave - toolbar popup.
//
// Grabs go through background.js, so they finish even if the popup closes
// while Grappler is still starting. The download list comes straight from
// Grappler over one native-messaging connection kept open for as long as
// the popup is, polled a little faster than once a second.

const HOST = 'com.grappler.host';
const DEFAULTS = { quality: -1, sendCookies: true };
const POLL_MS = 700;
const ACTIVE = new Set(['probing', 'queued', 'downloading', 'processing']);

const ICONS = {
  x: '<path d="m4.5 4.5 7 7m0-7-7 7"/>',
  retry: '<path d="M13 8a5 5 0 1 1-1.46-3.54M13.5 2.5v2.5H11"/>',
  play: '<path d="M5.5 3.8v8.4a.5.5 0 0 0 .77.42l6.4-4.2a.5.5 0 0 0 0-.84l-6.4-4.2a.5.5 0 0 0-.77.42z"/>',
  folder: '<path d="M2 4.5a1 1 0 0 1 1-1h3l1.5 1.5H13a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1z"/>',
  check: '<path d="m3.5 8.5 3 3 6-7"/>',
  alert: '<path d="M8 4v5M8 12v.01"/>',
  film: '<rect x="2" y="3.5" width="12" height="9" rx="1.5"/><path d="M6.75 6.25v3.5L9.75 8z"/>'
};

const $ = (id) => document.getElementById(id);
const icon = (name) => '<svg viewBox="0 0 16 16" aria-hidden="true">' + ICONS[name] + '</svg>';

let tab = null;
let port = null;
let pending = [];
let polling = false;
let pollTimer = null;
let lastItems = [];
const rows = new Map();

init();

// ---------------------------------------------------------------- setup
async function init() {
  const prefs = await chrome.storage.local.get(DEFAULTS);
  $('quality').value = String(prefs.quality);
  $('cookies').checked = prefs.sendCookies;

  $('quality').addEventListener('change', () => {
    chrome.storage.local.set({ quality: Number($('quality').value) });
  });
  $('cookies').addEventListener('change', () => {
    chrome.storage.local.set({ sendCookies: $('cookies').checked });
  });
  $('grab').addEventListener('click', grab);
  $('open').addEventListener('click', () => {
    chrome.runtime.sendMessage({ type: 'show' });
    window.close();
  });
  $('clear').addEventListener('click', () => act('clear'));
  $('folder').addEventListener('click', () => act('folder'));

  [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const usable = tab && /^https?:\/\//i.test(tab.url || '');
  $('title').textContent = usable ? (tab.title || tab.url) : 'Nothing to grab on this page';
  $('site').textContent = usable ? new URL(tab.url).hostname.replace(/^www\./, '') : 'Open a video page first';
  if (usable && tab.favIconUrl && /^(https?|data):/i.test(tab.favIconUrl)) {
    $('favicon').src = tab.favIconUrl;
  }
  $('grab').disabled = !usable;

  connect();
}

function connect() {
  try {
    port = chrome.runtime.connectNative(HOST);
  } catch (e) {
    showSetup(e.message);
    return;
  }

  port.onMessage.addListener((msg) => {
    const resolve = pending.shift();
    if (resolve) { resolve(msg); }
  });
  port.onDisconnect.addListener(() => {
    const error = chrome.runtime.lastError ? chrome.runtime.lastError.message : '';
    port = null;
    clearInterval(pollTimer);
    pending.forEach((resolve) => resolve({ ok: false, error }));
    pending = [];
    if (/not found|forbidden/i.test(error)) {
      showSetup(error);
    } else {
      setConn('off', 'Disconnected');
    }
  });

  poll();
  pollTimer = setInterval(poll, POLL_MS);
}

function request(message) {
  return new Promise((resolve) => {
    if (!port) { resolve({ ok: false, error: 'Not connected to Grappler.' }); return; }
    pending.push(resolve);
    port.postMessage(message);
  });
}

async function poll() {
  if (polling || !port) { return; }
  polling = true;
  const reply = await request({ type: 'status' });
  polling = false;
  if (reply && reply.ok) { render(reply); }
}

function showSetup(detail) {
  $('main').hidden = true;
  $('setup').hidden = false;
  $('setupDetail').textContent = detail || '';
  setConn('off', 'Not set up');
}

function setConn(kind, text) {
  const el = $('conn');
  el.className = 'conn' + (kind ? ' ' + kind : '');
  el.textContent = text;
}

// ---------------------------------------------------------------- actions
async function grab() {
  const button = $('grab');
  button.disabled = true;
  $('grabLabel').textContent = 'Sending…';
  notice('');

  const reply = await chrome.runtime.sendMessage({
    type: 'grab',
    url: tab.url,
    quality: Number($('quality').value),
    sendCookies: $('cookies').checked
  });

  if (reply && reply.ok) {
    button.classList.add('done');
    $('grabLabel').textContent = 'Added';
    poll();
    setTimeout(() => {
      button.classList.remove('done');
      button.disabled = false;
      updateGrabButton();
    }, 1500);
  } else {
    notice((reply && reply.error) || 'Something went wrong.');
    button.disabled = false;
    updateGrabButton();
  }
}

async function act(action, id) {
  const message = { type: 'action', action };
  if (id !== undefined) { message.id = id; }
  const reply = await request(message);
  if (reply && !reply.ok && reply.error) { notice(reply.error); }
  poll();
}

function notice(text) {
  $('notice').textContent = text || '';
}

// ---------------------------------------------------------------- render
function render(status) {
  const items = status.items || [];
  lastItems = items;
  const active = items.filter((i) => ACTIVE.has(i.state));
  const finished = items.length - active.length;

  setConn(status.running ? 'live' : '', status.running ? 'Running' : 'Idle');
  $('summary').textContent = active.length ? active.length + ' active' : '';
  $('clear').hidden = finished === 0;
  $('folder').hidden = !status.running;

  $('empty').hidden = items.length > 0;
  if (!items.length) {
    $('emptyTitle').textContent = status.running ? 'No downloads yet' : 'Grappler is idle';
    $('emptyHint').textContent = status.running
      ? 'Open a video and press Grab.'
      : 'It starts by itself, in the tray, when you grab something.';
  }

  // Keep the toolbar badge honest: the background only watches while
  // something is running, and an old tick should not outlive a look here.
  if (active.length) {
    chrome.runtime.sendMessage({ type: 'watch' });
  } else {
    chrome.action.setBadgeText({ text: '' });
  }

  const list = $('list');
  const seen = new Set();
  items.forEach((item, index) => {
    seen.add(item.id);
    let row = rows.get(item.id);
    if (!row) {
      row = createRow(item);
      rows.set(item.id, row);
    }
    updateRow(row, item);
    if (list.children[index] !== row.el) { list.insertBefore(row.el, list.children[index] || null); }
  });
  for (const [id, row] of rows) {
    if (!seen.has(id)) { row.el.remove(); rows.delete(id); }
  }

  updateGrabButton();
}

// "Downloading" or "Grab again" when the open tab is already in the list.
function updateGrabButton() {
  const button = $('grab');
  if (!tab || button.classList.contains('done') || $('grabLabel').textContent === 'Sending…') { return; }
  const match = lastItems.find((i) => i.url === tab.url);
  if (match && ACTIVE.has(match.state)) {
    $('grabLabel').textContent = 'In queue';
  } else if (match && match.state === 'done') {
    $('grabLabel').textContent = 'Grab again';
  } else {
    $('grabLabel').textContent = 'Grab';
  }
}

function createRow(item) {
  const el = document.createElement('li');
  el.className = 'row';
  el.innerHTML =
    '<div class="thumb">' + icon('film') + '<span class="badge"></span></div>' +
    '<div class="body"><div class="row-title"></div><div class="row-meta"></div>' +
    '<div class="bar"><span></span></div></div>' +
    '<div class="actions"></div>';
  return {
    el,
    thumb: el.querySelector('.thumb'),
    badge: el.querySelector('.badge'),
    title: el.querySelector('.row-title'),
    meta: el.querySelector('.row-meta'),
    bar: el.querySelector('.bar'),
    fill: el.querySelector('.bar > span'),
    actions: el.querySelector('.actions'),
    state: null,
    thumbUrl: null
  };
}

function updateRow(row, item) {
  const isActive = ACTIVE.has(item.state);
  row.el.className = 'row ' + item.state;

  if (item.thumb && item.thumb !== row.thumbUrl) {
    row.thumbUrl = item.thumb;
    const old = row.thumb.querySelector('img');
    if (old) { old.remove(); }
    const img = document.createElement('img');
    img.alt = '';
    img.referrerPolicy = 'no-referrer';
    img.addEventListener('error', () => img.remove());
    img.src = item.thumb;
    row.thumb.insertBefore(img, row.badge);
  }

  const title = item.title || item.url;
  if (row.title.textContent !== title) { row.title.textContent = title; }
  row.title.title = title;

  // Progress bar: only while something is happening.
  row.bar.hidden = !isActive || item.state === 'queued';
  const indeterminate = item.state === 'probing' || item.state === 'processing';
  row.bar.classList.toggle('indeterminate', indeterminate);
  row.fill.style.width = item.state === 'downloading' ? Math.max(1, item.percent) + '%' : '0%';

  setMeta(row.meta, item);

  // Actions and the corner badge only change with the state, so they are
  // left alone in between - rebuilding them would eat hover and clicks.
  if (row.state !== item.state) {
    row.state = item.state;
    row.badge.innerHTML = item.state === 'done' ? icon('check') : item.state === 'failed' ? icon('alert') : '';
    row.badge.hidden = !(item.state === 'done' || item.state === 'failed');
    row.actions.textContent = '';
    actionsFor(item).forEach((a) => row.actions.appendChild(a));
  }
}

function setMeta(el, item) {
  el.textContent = '';
  el.title = '';
  const tidy = (s) => (s || '').replace(/\s+·\s+/g, ' · ').trim();

  switch (item.state) {
    case 'probing':
      el.textContent = 'Reading video info…';
      break;
    case 'queued':
      el.textContent = 'Waiting for a free slot';
      break;
    case 'downloading': {
      if (item.percent <= 0 && !item.speed) { el.textContent = 'Starting…'; break; }
      const pct = document.createElement('span');
      pct.className = 'pct';
      pct.textContent = Math.floor(item.percent) + '%';
      el.appendChild(pct);
      const rest = [item.speed, item.eta ? item.eta + ' left' : ''].filter(Boolean).join(' · ');
      if (rest) { el.appendChild(document.createTextNode(' · ' + rest)); }
      break;
    }
    case 'processing':
      el.textContent = (item.status || 'Finishing up') + '…';
      break;
    case 'done':
      el.textContent = ['Done', item.size, tidy(item.meta)].filter(Boolean).join(' · ');
      break;
    case 'failed':
      el.textContent = item.error || 'Failed';
      el.title = item.error || '';
      break;
    default:
      el.textContent = 'Cancelled';
  }
}

function actionsFor(item) {
  const make = (name, label, action, danger) => {
    const b = document.createElement('button');
    b.className = 'icon-btn' + (danger ? ' danger' : '');
    b.title = label;
    b.setAttribute('aria-label', label);
    b.innerHTML = icon(name);
    b.addEventListener('click', () => act(action, item.id));
    return b;
  };

  if (ACTIVE.has(item.state)) { return [make('x', 'Cancel', 'cancel', true)]; }
  if (item.state === 'done') {
    return [make('play', 'Play', 'play'), make('folder', 'Show in folder', 'folder'), make('x', 'Remove from list', 'remove')];
  }
  return [make('retry', 'Try again', 'retry'), make('x', 'Remove from list', 'remove')];
}
