export function hostname(url) {
  try {
    const parsed = new URL(url);
    return parsed.protocol === 'http:' || parsed.protocol === 'https:' ? parsed.hostname.toLowerCase() : null;
  } catch { return null; }
}

export function allowed(tab, blocks) {
  return Boolean(tab && !tab.incognito && blocks && !blocks.paused &&
    !blocks.paused_tabs.includes(tab.id) &&
    !blocks.excluded_hosts.includes(hostname(tab.url)));
}

export function isSecure(element) {
  const type = (element.type || '').toLowerCase();
  const autocomplete = (element.autocomplete || '').toLowerCase().split(/\s+/);
  return type === 'password' || autocomplete.some(x => x.startsWith('cc-') ||
    ['one-time-code', 'current-password', 'new-password'].includes(x)) ||
    element.hasAttribute('data-sensitive');
}

export function validBlocks(state) {
  return state && typeof state.paused === 'boolean' && Array.isArray(state.excluded_hosts) &&
    state.excluded_hosts.every(h => typeof h === 'string' && h === h.toLowerCase() && /^[a-z0-9.-]+$/.test(h)) &&
    Array.isArray(state.paused_tabs) && state.paused_tabs.every(Number.isInteger);
}

// C D-C22 T1 limits, shared with the native host's validation (BrowserText.MaxLines / MaxLine / MaxChars).
export const MAX_LINES = 400;
export const MAX_LINE = 500;
export const MAX_CHARS = 8192;
export const MAX_TITLE = 300;
export const MAX_URL = 2048;
export const MEDIA_EVERY_MS = 10_000;

// The full address for T1: http(s) only, credentials stripped, at most MAX_URL characters; null otherwise.
export function safeUrl(url) {
  try {
    const parsed = new URL(url);
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null;
    parsed.username = '';
    parsed.password = '';
    const text = parsed.toString();
    return text.length <= MAX_URL ? text : null;
  } catch { return null; }
}

export function safeTitle(title) {
  const text = (title || '').replace(/\s+/g, ' ').trim();
  return text.length === 0 ? null : text.slice(0, MAX_TITLE);
}

// Raw text blocks → the lines sent: whitespace collapsed, empty ones dropped, each cut to MAX_LINE, at most MAX_LINES
// lines and MAX_CHARS characters in all (the readable part comes first, so it survives the cut).
export function textLines(blocks) {
  const lines = [];
  let total = 0;
  for (const raw of blocks || []) {
    const line = String(raw ?? '').replace(/\s+/g, ' ').trim().slice(0, MAX_LINE);
    if (!line) continue;
    if (lines.length >= MAX_LINES || total + line.length > MAX_CHARS) break;
    lines.push(line);
    total += line.length;
  }
  return lines;
}

export function sameLines(a, b) {
  return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((x, i) => x === b[i]);
}

// Video: report a change of playing at once, and while playing at most every MEDIA_EVERY_MS.
export function mediaDue(last, now, playing) {
  return !last || last.playing !== playing || (playing && now - last.at >= MEDIA_EVERY_MS);
}

// Never read: form values and anything a secure field marks (password, card, one-time code), scripts and hidden text.
export const SKIP_SELECTOR = 'script, style, noscript, template, textarea, input, select, option, [contenteditable=""], [contenteditable="true"], [aria-hidden="true"], [hidden], [type="password"], [autocomplete^="cc-"], [autocomplete~="one-time-code"], [autocomplete~="current-password"], [autocomplete~="new-password"], [data-sensitive]';
