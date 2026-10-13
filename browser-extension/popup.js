// Grappler for Brave - toolbar popup.
//
// Two steps, like the app: "Fetch video" reads the page's details and the
// qualities it really offers; then you pick one and press Download.
//
// Fetches go through background.js, so they finish even if the popup
// closes while Grappler is still starting. The download list comes straight
// from Grappler over one native-messaging connection kept open for as long
// as the popup is, polled a little faster than once a second.

const HOST = 'com.grappler.host';
const DEFAULTS = { sendCookies: true };
const POLL_MS = 700;
const ACTIVE = new Set(['queued', 'downloading', 'processing']);
const FINISHED = new Set(['done', 'failed', 'cancelled']);

const ICONS = {
  x: '<path d="m4.5 4.5 7 7m0-7-7 7"/>',
  retry: '<path d="M13 8a5 5 0 1 1-1.46-3.54M13.5 2.5v2.5H11"/>',
  play: '<path d="M5.5 3.8v8.4a.5.5 0 0 0 .77.42l6.4-4.2a.5.5 0 0 0 0-.84l-6.4-4.2a.5.5 0 0 0-.77.42z"/>',
  folder: '<path d="M2 4.5a1 1 0 0 1 1-1h3l1.5 1.5H13a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1z"/>',
  check: '<path d="m3.5 8.5 3 3 6-7"/>',
  alert: '<path d="M8 4v5M8 12v.01"/>',
  film: '<rect x="2" y="3.5" width="12" height="9" rx="1.5"/><path d="M6.75 6.25v3.5L9.75 8z"/>',
  down: '<path d="M8 2.5v8M4.5 7 8 10.5 11.5 7M3 13.5h10"/>'
};
const ACTION_ICONS = {
  fetch: 'M7 12.5a5.5 5.5 0 1 1 0-11 5.5 5.5 0 0 1 0 11zM14.5 14.5l-3.6-3.6',
  download: 'M8 2.5v8M4.5 7 8 10.5 11.5 7M3 13.5h10'
};

const $ = (id) => document.getElementById(id);
const icon = (name) => '<svg viewBox="0 0 16 16" aria-hidden="true">' + ICONS[name] + '</svg>';

let tab = null;
let usable = false;
let port = null;
let pending = [];
let polling = false;
let pollTimer = null;
let items = [];
let cardId = null;        // the row for this tab, once fetched
let cardMode = 'fetch';   // what the main button does: 'fetch' or 'download'
let busy = false;         // a request from the main button is in flight
const rows = new Map();

init();

// ---------------------------------------------------------------- setup
async function init() {
  const prefs = await chrome.storage.local.get(DEFAULTS);
  $('cookies').checked = prefs.sendCookies;
  $('cookies').addEventListener('change', () => {
    chrome.storage.local.set({ sendCookies: $('cookies').checked });
  });

  $('action').addEventListener('click', onAction);
  $('open').addEventListener('click', () => {
    chrome.runtime.sendMessage({ type: 'show' });
    window.close();
  });
  $('clear').addEventListener('click', () => act('clear'));
  $('folder').addEventListener('click', () => act('folder'));

  [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  usable = !!tab && /^https?:\/\//i.test(tab.url || '');
  $('title').textContent = usable ? (tab.title || tab.url) : 'Nothing to fetch on this page';
  $('site').textContent = usable ? new URL(tab.url).hostname.replace(/^www\./, '') : 'Open a video page first';
  if (usable && tab.favIconUrl && /^(https?|data):/i.test(tab.favIconUrl)) {
    $('favicon').src = tab.favIconUrl;
  }
  renderCard();
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
async function onAction() {
  if (busy) { return; }
  notice('');

  if (cardMode === 'download') {
    const item = cardItem();
    if (!item) { return; }
    busy = true;
    await start(item.id, Number($('choice').value));
    busy = false;
    return;
  }

  busy = true;
  renderCard();
  const reply = await chrome.runtime.sendMessage({
    type: 'fetch',
    url: tab.url,
    sendCookies: $('cookies').checked
  });
  busy = false;

  if (reply && reply.ok) {
    cardId = reply.id;
    if (!port) { connect(); }   // Grappler has just started
    poll();
  } else {
    notice((reply && reply.error) || 'Something went wrong.');
  }
  renderCard();
}

async function start(id, choice) {
  const reply = await request({ type: 'action', action: 'start', id, choice });
  if (reply && !reply.ok && reply.error) { notice(reply.error); }
  poll();
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
  items = status.items || [];
  const active = items.filter((i) => ACTIVE.has(i.state));
  const ready = items.filter((i) => i.state === 'ready');
  const finished = items.filter((i) => FINISHED.has(i.state));

  setConn(status.running ? 'live' : '', status.running ? 'Running' : 'Idle');
  const summary = [];
  if (active.length) { summary.push(active.length + ' active'); }
  if (ready.length) { summary.push(ready.length + ' ready'); }
  $('summary').textContent = summary.join(' · ');
  $('clear').hidden = finished.length === 0;
  $('folder').hidden = !status.running;

  $('empty').hidden = items.length > 0;
  if (!items.length) {
    $('emptyTitle').textContent = status.running ? 'No downloads yet' : 'Grappler is idle';
    $('emptyHint').textContent = status.running
      ? 'Open a video and press Fetch video.'
      : 'It starts by itself, in the tray, when you fetch something.';
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

  renderCard();
}

// The row for this tab: the one fetched from here, or the newest with the
// same address (fetched earlier, or from the right-click menu).
function cardItem() {
  if (cardId !== null) {
    const byId = items.find((i) => i.id === cardId);
    if (byId) { return byId; }
  }
  return usable ? items.find((i) => i.url === tab.url) : undefined;
}

function renderCard() {
  const item = cardItem();
  const state = item ? item.state : null;
  const button = $('action');
  const label = $('actionLabel');
  const wrap = $('choiceWrap');
  const select = $('choice');

  $('cardBar').hidden = !(busy && cardMode === 'fetch') && state !== 'probing';
  wrap.hidden = state !== 'ready';

  if (state === 'ready') {
    // Options are built once per row, so a choice made here is not undone
    // by the next poll.
    if (select.dataset.id !== String(item.id)) {
      fillChoices(select, item);
      select.dataset.id = String(item.id);
    }
    setAction('download', 'Download', busy);
    return;
  }
  select.dataset.id = '';

  if (busy && cardMode === 'fetch') { setAction('fetch', 'Fetching…', true); return; }
  if (state === 'probing') { setAction('fetch', 'Fetching details…', true); return; }
  if (ACTIVE.has(state)) { setAction('download', 'Downloading…', true); return; }
  setAction('fetch', FINISHED.has(state) ? 'Fetch again' : 'Fetch video', !usable);

  function setAction(mode, text, disabled) {
    cardMode = mode;
    label.textContent = text;
    button.disabled = !!disabled;
    $('actionIcon').setAttribute('d', ACTION_ICONS[mode]);
  }
}

function fillChoices(select, item) {
  select.textContent = '';
  (item.choices || []).forEach((c, index) => {
    const option = document.createElement('option');
    option.value = String(index);
    option.textContent = c.detail ? c.label + ' · ' + c.detail : c.label;
    if (c.picker) { option.textContent = c.label + ' (opens Grappler)'; }
    select.appendChild(option);
  });
  select.value = String(Math.max(0, item.selected || 0));
}

function createRow(item) {
  const el = document.createElement('li');
  el.className = 'row';
  el.innerHTML =
    '<div class="thumb">' + icon('film') + '<span class="badge"></span></div>' +
    '<div class="body"><div class="row-title"></div><div class="row-meta"></div>' +
    '<label class="select small" hidden><select></select>' +
    '<svg class="chevron" viewBox="0 0 16 16" aria-hidden="true"><path d="m4 6 4 4 4-4"/></svg></label>' +
    '<div class="bar"><span></span></div></div>' +
    '<div class="actions"></div>';
  return {
    el,
    thumb: el.querySelector('.thumb'),
    badge: el.querySelector('.badge'),
    title: el.querySelector('.row-title'),
    meta: el.querySelector('.row-meta'),
    pick: el.querySelector('.select'),
    select: el.querySelector('select'),
    bar: el.querySelector('.bar'),
    fill: el.querySelector('.bar > span'),
    actions: el.querySelector('.actions'),
    state: null,
    thumbUrl: null
  };
}

function updateRow(row, item) {
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
  const moving = item.state === 'probing' || item.state === 'downloading' || item.state === 'processing';
  row.bar.hidden = !moving;
  row.bar.classList.toggle('indeterminate', item.state !== 'downloading');
  row.fill.style.width = item.state === 'downloading' ? Math.max(1, item.percent) + '%' : '0%';

  const ready = item.state === 'ready';
  row.pick.hidden = !ready;
  row.meta.hidden = ready;
  if (!ready) { setMeta(row.meta, item); }

  // The picker, actions and corner badge only change with the state, so
  // they are left alone in between - rebuilding them would eat hover,
  // clicks and the choice being made.
  if (row.state !== item.state) {
    row.state = item.state;
    if (ready) { fillChoices(row.select, item); }
    row.badge.innerHTML = item.state === 'done' ? icon('check') : item.state === 'failed' ? icon('alert') : '';
    row.badge.hidden = !(item.state === 'done' || item.state === 'failed');
    row.actions.textContent = '';
    actionsFor(item, row).forEach((a) => row.actions.appendChild(a));
  }
}

function setMeta(el, item) {
  el.textContent = '';
  el.title = '';
  const tidy = (s) => (s || '').replace(/\s+·\s+/g, ' · ').trim();

  switch (item.state) {
    case 'probing':
      el.textContent = 'Fetching details…';
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

function actionsFor(item, row) {
  const make = (name, label, onClick, cls) => {
    const b = document.createElement('button');
    b.className = 'icon-btn' + (cls ? ' ' + cls : '');
    b.title = label;
    b.setAttribute('aria-label', label);
    b.innerHTML = icon(name);
    b.addEventListener('click', onClick);
    return b;
  };
  const run = (action) => () => act(action, item.id);

  if (item.state === 'ready') {
    return [
      make('down', 'Download', () => start(item.id, Number(row.select.value)), 'accent'),
      make('x', 'Remove from list', run('remove'), 'danger')
    ];
  }
  if (item.state === 'probing' || ACTIVE.has(item.state)) { return [make('x', 'Cancel', run('cancel'), 'danger')]; }
  if (item.state === 'done') {
    return [make('play', 'Play', run('play')), make('folder', 'Show in folder', run('folder')), make('x', 'Remove from list', run('remove'))];
  }
  return [make('retry', 'Try again', run('retry')), make('x', 'Remove from list', run('remove'))];
}
