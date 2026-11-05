(() => {
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
})();
