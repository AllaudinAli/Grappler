// Grappler for Brave - background service worker.
//
// Grabs from the popup, the right-click menu and the keyboard shortcut all
// go through here: a popup dies the moment it loses focus, and Grappler can
// take a few seconds to start, so the request must not live in the popup.
//
// While anything is downloading this also keeps one connection to Grappler
// open and mirrors progress on the toolbar badge. An open native-messaging
// connection keeps the service worker alive, so the badge stays current
// with the popup closed.

const HOST = 'com.grappler.host';
const DEFAULTS = { quality: -1, sendCookies: true };
const ACTIVE = new Set(['probing', 'queued', 'downloading', 'processing']);
const COLORS = { accent: '#2F81F7', success: '#3FB950', danger: '#F85149' };

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: 'page', title: 'Grab this page with Grappler', contexts: ['page', 'frame'] });
    chrome.contextMenus.create({ id: 'link', title: 'Grab link with Grappler', contexts: ['link'] });
    chrome.contextMenus.create({ id: 'video', title: 'Grab this video with Grappler', contexts: ['video', 'audio'] });
  });
});

chrome.contextMenus.onClicked.addListener((info) => {
  // A <video> element's src is usually a blob: or a single stream - the
  // page itself is what yt-dlp knows how to read.
  const url = info.menuItemId === 'link' ? info.linkUrl : (info.frameUrl || info.pageUrl);
  grab(url, {});
});

chrome.commands.onCommand.addListener((command, tab) => {
  if (command === 'grab-current-tab' && tab) { grab(tab.url, {}); }
});

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  switch (msg.type) {
    case 'grab':
      grab(msg.url, msg).then(sendResponse);
      return true;
    case 'show':
      callHost({ type: 'show' }).then(sendResponse);
      return true;
    case 'watch':
      watch();
      return false;
    default:
      return false;
  }
});

// ---------------------------------------------------------------- grab
async function grab(url, overrides) {
  let reply;
  if (!/^https?:\/\//i.test(url || '')) {
    reply = { ok: false, error: 'Open a video page first - this tab has nothing to grab.' };
  } else {
    const prefs = await chrome.storage.local.get(DEFAULTS);
    const quality = overrides.quality !== undefined ? overrides.quality : prefs.quality;
    const sendCookies = overrides.sendCookies !== undefined ? overrides.sendCookies : prefs.sendCookies;

    const message = { type: 'grab', url, quality };
    if (sendCookies) {
      const cookies = await cookiesFor(url);
      if (cookies) { message.cookies = cookies; }
    }
    reply = await callHost(message);
  }

  if (reply.ok) {
    watch();
  } else {
    flashBadge('!', COLORS.danger, 'Grappler: ' + reply.error);
  }
  return reply;
}

// The site's cookies in the cookies.txt format yt-dlp reads with --cookies.
// Read through Brave's own API, so nothing has to decrypt the browser's
// cookie database - which is what fails with "Failed to decrypt with DPAPI".
async function cookiesFor(url) {
  let cookies;
  try { cookies = await chrome.cookies.getAll({ url }); } catch (e) { return null; }
  if (!cookies || cookies.length === 0) { return null; }

  const lines = ['# Netscape HTTP Cookie File'];
  for (const c of cookies) {
    const domain = c.hostOnly || c.domain.startsWith('.') ? c.domain : '.' + c.domain;
    lines.push([
      (c.httpOnly ? '#HttpOnly_' : '') + domain,
      c.hostOnly ? 'FALSE' : 'TRUE',
      c.path,
      c.secure ? 'TRUE' : 'FALSE',
      c.session ? 0 : Math.floor(c.expirationDate),
      c.name,
      c.value
    ].join('\t'));
  }
  return lines.join('\n') + '\n';
}

function callHost(message) {
  return new Promise((resolve) => {
    chrome.runtime.sendNativeMessage(HOST, message, (reply) => {
      if (chrome.runtime.lastError) {
        resolve({ ok: false, error: explain(chrome.runtime.lastError.message) });
      } else {
        resolve(reply || { ok: false, error: 'Grappler did not answer.' });
      }
    });
  });
}

function explain(text) {
  if (/not found|forbidden/i.test(text || '')) {
    return 'Brave cannot find Grappler yet. Run Install-Brave-Extension.bat in the Grappler folder, then restart Brave.';
  }
  if (/exited/i.test(text || '')) {
    return 'Grappler.exe could not be started. Has the Grappler folder moved? Run Install-Brave-Extension.bat again.';
  }
  return text;
}

// ---------------------------------------------------------------- badge
let watchPort = null;

function watch() {
  if (watchPort) { return; }
  try { watchPort = chrome.runtime.connectNative(HOST); } catch (e) { return; }

  const port = watchPort;
  const watched = new Set();   // ids seen active, to report how they ended
  let waiting = false;

  const ask = () => {
    if (waiting || watchPort !== port) { return; }
    waiting = true;
    port.postMessage({ type: 'status' });
  };
  const timer = setInterval(ask, 1500);
  const stop = () => {
    clearInterval(timer);
    if (watchPort === port) { watchPort = null; }
    try { port.disconnect(); } catch (e) { /* already gone */ }
  };

  port.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    clearInterval(timer);
    if (watchPort === port) { watchPort = null; }
  });

  port.onMessage.addListener((reply) => {
    waiting = false;
    const items = (reply && reply.items) || [];
    const active = items.filter((i) => ACTIVE.has(i.state));
    active.forEach((i) => watched.add(i.id));

    if (active.length) {
      const only = active.length === 1 ? active[0] : null;
      const text = only && only.state === 'downloading' && only.percent > 0
        ? Math.floor(only.percent) + '%'
        : String(active.length);
      setBadge(text, COLORS.accent, 'Grappler: ' + active.length + ' downloading');
      return;
    }

    stop();
    const ended = items.filter((i) => watched.has(i.id));
    if (!ended.length) { setBadge('', COLORS.accent, 'Grab with Grappler'); return; }
    const failed = ended.some((i) => i.state === 'failed');
    flashBadge(failed ? '!' : '✓', failed ? COLORS.danger : COLORS.success,
      failed ? 'Grappler: a download failed' : 'Grappler: downloads finished');
  });

  ask();
}

function setBadge(text, color, title) {
  chrome.action.setBadgeBackgroundColor({ color }).catch(() => {});
  if (chrome.action.setBadgeTextColor) { chrome.action.setBadgeTextColor({ color: '#FFFFFF' }).catch(() => {}); }
  chrome.action.setBadgeText({ text }).catch(() => {});
  chrome.action.setTitle({ title }).catch(() => {});
}

// Shown for a few seconds, unless a new download takes the badge first.
function flashBadge(text, color, title) {
  setBadge(text, color, title);
  setTimeout(() => {
    if (!watchPort) { setBadge('', COLORS.accent, 'Grab with Grappler'); }
  }, 6000);
}
