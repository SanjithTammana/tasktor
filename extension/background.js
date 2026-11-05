import { hostname, allowed, validBlocks } from './logic.js';

let port;
let blocks = null;
let updateRequired = false;
let retry;
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
    extension_version: chrome.runtime.getManifest().version });
}
function send(message) { connect(); port?.postMessage({ ...message, at: at() }); }
async function report(tab) {
  if (!allowed(tab, blocks)) { if (tab?.id !== undefined) reported.delete(tab.id); return; }
  reported.set(tab.id, hostname(tab.url));
  fields.set(tab.id, new Map([[-1, 'secure']]));
  send({ type: 'tab', tab: tab.id, window: tab.windowId, host: hostname(tab.url), active: tab.active, audible: Boolean(tab.audible) });
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
  if (message.type === 'state') { respond({ blocks, updateRequired }); return; }
  if (message.type === 'block_request' && message.tab && !message.tab.incognito) {
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
connect();
