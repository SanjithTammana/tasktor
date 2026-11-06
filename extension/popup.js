const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
const state = await chrome.runtime.sendMessage({ type: 'state' });
const privateOk = Boolean(state?.privateOk);
const off = tab?.incognito && !privateOk;
document.querySelector('#status').textContent = off ? 'Not capturing private windows' : state?.updateRequired ? 'Update Tasktor' : state?.blocks ? (state.blocks.paused ? 'Capture paused' : 'Capture connected') : 'Tasktor unavailable';
for (const kind of ['site', 'tab']) {
  const button = document.querySelector(`#${kind}`);
  button.disabled = !tab || off || !state?.blocks;
  button.addEventListener('click', () => { chrome.runtime.sendMessage({ type: 'block_request', kind, tab }); window.close(); });
}

// Private windows (C §5.5): off unless turned on here, per profile, and only where the browser lets the extension run.
const box = document.querySelector('#private');
const browserAllows = await chrome.extension.isAllowedIncognitoAccess().catch(() => false);
const { private_opt_in: optIn } = await chrome.storage.local.get('private_opt_in').catch(() => ({}));
box.checked = optIn === true && browserAllows;
box.disabled = !browserAllows;
document.querySelector('#private-hint').hidden = browserAllows;
box.addEventListener('change', () => { void chrome.storage.local.set({ private_opt_in: box.checked }); });
