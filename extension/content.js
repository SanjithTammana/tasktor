(() => {
  // ---- focus: which kind of field has the keyboard (secure fields mean "record nothing") ----
  function secure(el) {
    const tokens = (el?.getAttribute?.('autocomplete') || '').toLowerCase().split(/\s+/);
    return el?.tagName === 'IFRAME' || el?.type?.toLowerCase() === 'password' || tokens.some(x => x.startsWith('cc-') || ['one-time-code', 'current-password', 'new-password'].includes(x)) || el?.hasAttribute?.('data-sensitive');
  }
  function focus() {
    const el = document.activeElement;
    const field = secure(el) ? 'secure' : el && (el.matches('input, textarea, [contenteditable="true"]')) ? 'text' : 'none';
    chrome.runtime.sendMessage({ type: 'focus', field }).catch(() => {});
  }
  chrome.runtime.onMessage.addListener(message => { if (message.type === 'refresh_focus') focus(); });
  document.addEventListener('focusin', focus, true);
  document.addEventListener('focusout', () => queueMicrotask(focus), true);
  document.addEventListener('visibilitychange', () => chrome.runtime.sendMessage({ type: 'visibility', visible: !document.hidden }).catch(() => {}));
  focus();

  // ---- page text and video (C18 / C20.2, T1): the top frame only, and only once the background says this tab may be read ----
  if (window.top !== window) return;
  const DWELL_MS = 2000;
  const EVERY_MS = 2000;
  const MEDIA_EVERY_MS = 10000;
  const SKIP = 'script, style, noscript, template, textarea, input, select, option, [contenteditable=""], [contenteditable="true"], [aria-hidden="true"], [hidden], [type="password"], [autocomplete^="cc-"], [autocomplete~="one-time-code"], [autocomplete~="current-password"], [autocomplete~="new-password"], [data-sensitive]';
  const BLOCK = 'p, li, h1, h2, h3, h4, h5, h6, td, th, pre, blockquote, dt, dd, figcaption, caption, summary, label, a, button, div, section, article, main';
  let allowed = false;
  let dirty = true;
  let last = null;
  let lastMedia = null;

  function visibleRect(el) {
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0 && r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth;
  }
  // Readable content (main / article / role=main) first, then text in the viewport; one line per text block.
  function read() {
    const root = document.querySelector('main, article, [role="main"]');
    const lines = [];
    const seen = new Set();
    for (const scope of root ? [root, document.body] : [document.body]) {
      if (!scope) continue;
      const walker = document.createTreeWalker(scope, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        const el = node.parentElement;
        if (!el || el.closest(SKIP)) continue;
        const block = el.closest(BLOCK) || el;
        if (seen.has(block)) continue;
        if (scope !== root && !visibleRect(block)) continue;
        seen.add(block);
        lines.push(block.innerText || node.textContent);
        if (lines.length > 1200) break;
      }
    }
    return lines;
  }
  function cap(blocks) {
    const out = [];
    let total = 0;
    for (const raw of blocks) {
      const line = String(raw ?? '').replace(/\s+/g, ' ').trim().slice(0, 500);
      if (!line) continue;
      if (out.length >= 400 || total + line.length > 8192) break;
      out.push(line);
      total += line.length;
    }
    return out;
  }
  function sendText() {
    if (!allowed || document.hidden || !dirty) return;
    dirty = false;
    const lines = cap(read());
    if (last && lines.length === last.length && lines.every((x, i) => x === last[i])) return;
    last = lines;
    chrome.runtime.sendMessage({ type: 'text', lines }).catch(() => {});
  }
  function media(event) {
    const v = event.target;
    if (!allowed || !(v instanceof HTMLVideoElement)) return;
    const playing = !v.paused && !v.ended;
    const now = Date.now();
    if (lastMedia && lastMedia.playing === playing && (!playing || now - lastMedia.at < MEDIA_EVERY_MS)) return;
    lastMedia = { playing, at: now };
    chrome.runtime.sendMessage({ type: 'media', playing, position_s: Number.isFinite(v.currentTime) ? Math.round(v.currentTime * 10) / 10 : null }).catch(() => {});
  }
  async function ask() {
    try { allowed = Boolean(await chrome.runtime.sendMessage({ type: 'may_read' })); } catch { allowed = false; }
    if (!allowed) { last = null; dirty = true; }
  }
  new MutationObserver(() => { dirty = true; }).observe(document.documentElement, { childList: true, characterData: true, subtree: true });
  for (const type of ['play', 'pause', 'ended', 'timeupdate']) document.addEventListener(type, media, true);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) dirty = true; });
  void ask().then(() => setTimeout(sendText, DWELL_MS));
  setInterval(sendText, EVERY_MS);
  setInterval(ask, 10000); // blocks can change (site excluded, capture paused): re-ask, and read nothing while not allowed
})();
