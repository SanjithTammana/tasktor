const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
const state = await chrome.runtime.sendMessage({ type: 'state' });
document.querySelector('#status').textContent = tab?.incognito ? 'Unavailable in private windows' : state?.updateRequired ? 'Update Tasktor' : state?.blocks ? (state.blocks.paused ? 'Capture paused' : 'Capture connected') : 'Tasktor unavailable';
for (const kind of ['site', 'tab']) {
  const button = document.querySelector(`#${kind}`);
  button.disabled = !tab || tab.incognito || !state?.blocks;
  button.addEventListener('click', () => { chrome.runtime.sendMessage({ type: 'block_request', kind, tab }); window.close(); });
}
