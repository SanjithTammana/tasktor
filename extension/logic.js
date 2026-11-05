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
