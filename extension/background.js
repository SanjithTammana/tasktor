import { hostname, allowed as allowedFor, validBlocks, safeUrl, safeTitle, textLines, privateAllowed } from './logic.js';

let port;
let blocks = null;
let updateRequired = false;
let retry;
let privateOk = false; // private windows: the profile's opt-in (popup) and the browser's own permission
const allowed = (tab, state) => allowedFor(tab, state, privateOk);
const reported = new Map();
const fields = new Map();
const at = () => new Date().toISOString();
function connect() {
  if (port) return;
  port = chrome.runtime.connectNative('com.tasktor.nmh');
  port.onDisconnect.addListener(() => { port = null; blocks = null; clearTimeout(retry); retry = setTimeout(connect, 5000); });
  retry = setTimeout(() => { if (!blocks && port) port.disconnect(); }, 5000);
  port.onMessage.addListener(message => {
    if (message.type === 'welcome') {
      clearTimeout(retry);
      updateRequired = message.contract !== 1;
      blocks = !updateRequired && validBlocks(message.blocks) ? message.blocks : null;
      if (blocks) void chrome.tabs.query({}).then(tabs => tabs.forEach(tab => { void report(tab); }));
    }
    if (message.type === 'blocks') {
      blocks = validBlocks(message.state) ? message.state : null;
      if (blocks) void chrome.tabs.query({}).then(tabs => tabs.forEach(tab => { void report(tab); }));
    }
  });
  port.postMessage({ type: 'hello', at: at(), contract: 1, client: 'nmh',
    browser: navigator.userAgent.includes('Edg/') ? 'edge' : 'chrome',
    extension_version: chrome.runtime.getManifest().version, incognito: privateOk });
}
function send(message) { connect(); port?.postMessage({ ...message, at: at() }); }
async function report(tab) {
  if (!allowed(tab, blocks)) { if (tab?.id !== undefined) reported.delete(tab.id); return; }
  reported.set(tab.id, hostname(tab.url));
  fields.set(tab.id, new Map([[-1, 'secure']]));
  // T1 (C D-C22): the full address (no credentials) and the title, only for tabs capture may read.
  send({ type: 'tab', tab: tab.id, window: tab.windowId, host: hostname(tab.url), active: tab.active, audible: Boolean(tab.audible),
    url: hostname(tab.url) ? safeUrl(tab.url) : null, title: safeTitle(tab.title), incognito: Boolean(tab.incognito) });
  send({ type: 'focus', tab: tab.id, field: 'secure' });
  chrome.tabs.sendMessage(tab.id, { type: 'refresh_focus' }).catch(() => {});
}
chrome.tabs.onUpdated.addListener((_id, _change, tab) => { void report(tab); });
chrome.tabs.onActivated.addListener(async info => { await report(await chrome.tabs.get(info.tabId)); });
chrome.windows.onFocusChanged.addListener(async id => {
  if (id === chrome.windows.WINDOW_ID_NONE) return;
  const tabs = await chrome.tabs.query({ windowId: id, active: true });
  for (const tab of tabs) await report(tab);
});
chrome.tabs.onRemoved.addListener((id, info) => {
  const host = reported.get(id);
  if (reported.has(id) && blocks && !blocks.paused && !blocks.paused_tabs.includes(id) && !blocks.excluded_hosts.includes(host)) send({ type: 'tab_closed', tab: id });
  reported.delete(id);
  fields.delete(id);
});
chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (sender.tab) {
    if (message.type === 'may_read') { respond(sender.frameId === 0 && allowed(sender.tab, blocks)); return; }
    if (sender.frameId === 0 && allowed(sender.tab, blocks) && message.type === 'text' && Array.isArray(message.lines)) {
      const lines = textLines(message.lines); // the tested caps are applied here, whatever the page sent
      if (lines.length > 0) send({ type: 'text', tab: sender.tab.id, lines });
      return;
    }
    if (sender.frameId === 0 && allowed(sender.tab, blocks) && message.type === 'media' && typeof message.playing === 'boolean') {
      const pos = Number.isFinite(message.position_s) && message.position_s >= 0 ? message.position_s : null;
      send({ type: 'media', tab: sender.tab.id, playing: message.playing, position_s: pos });
      return;
    }
    if (allowed(sender.tab, blocks) && ['focus', 'visibility'].includes(message.type)) {
      if (message.type === 'focus' && ['none', 'text', 'secure'].includes(message.field)) {
        const frameFields = fields.get(sender.tab.id) || new Map();
        if (sender.frameId === 0) frameFields.delete(-1);
        frameFields.set(sender.frameId, message.field);
        fields.set(sender.tab.id, frameFields);
        const values = [...frameFields.values()];
        send({ type: 'focus', tab: sender.tab.id, field: values.includes('secure') ? 'secure' : values.includes('text') ? 'text' : 'none' });
      } else if (message.type === 'visibility' && sender.frameId === 0 && typeof message.visible === 'boolean') {
        send({ type: 'visibility', tab: sender.tab.id, visible: message.visible });
      }
    }
    return;
  }
  if (message.type === 'state') { respond({ blocks, updateRequired, privateOk }); return; }
  if (message.type === 'block_request' && message.tab && (!message.tab.incognito || privateOk)) {
    const tab = message.tab;
    const host = hostname(tab.url);
    if (!blocks || (message.kind === 'site' && !host) || !['site', 'tab'].includes(message.kind)) return;
    if (message.kind === 'site') blocks.excluded_hosts.push(host);
    else blocks.paused_tabs.push(tab.id);
    reported.delete(tab.id);
    send({ type: 'block_request', request_id: crypto.randomUUID(), op: 'add', kind: message.kind,
      ...(message.kind === 'site' ? { host: hostname(tab.url) } : { tab: tab.id }) });
  }
});
// The opt-in is read before the first hello, and a change starts a new session (a new hello carries it; capture forgets
// the profile's private tabs on a hello without it).
async function loadPrivate() {
  const { private_opt_in: optIn } = await chrome.storage.local.get('private_opt_in').catch(() => ({}));
  const browserAllows = await chrome.extension.isAllowedIncognitoAccess().catch(() => false);
  return privateAllowed(optIn, browserAllows);
}
chrome.storage.onChanged.addListener(async (changes, area) => {
  if (area !== 'local' || !('private_opt_in' in changes)) return;
  const next = await loadPrivate();
  if (next === privateOk) return;
  privateOk = next;
  if (!privateOk) for (const [id] of reported) { const tab = await chrome.tabs.get(id).catch(() => null); if (!tab || tab.incognito) reported.delete(id); }
  restart();
});
// Our own disconnect() fires no onDisconnect, so the session state is reset here.
function restart() { if (port) { port.disconnect(); port = null; blocks = null; clearTimeout(retry); } connect(); }
loadPrivate().then(ok => { if (ok !== privateOk) { privateOk = ok; restart(); } else connect(); }); // a tab event may already have said hello
