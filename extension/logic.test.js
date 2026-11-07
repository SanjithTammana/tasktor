import test from 'node:test';
import assert from 'node:assert/strict';
import { hostname, allowed, isSecure, privateAllowed } from './logic.js';

test('hostname never includes path, query, credentials or port', () => {
  assert.equal(hostname('https://user:pw@Example.COM:443/a?secret=1#x'), 'example.com');
  assert.equal(hostname('file:///tmp/example'), null);
});
test('capture blocks and private tabs fail closed', () => {
  const tab = { id: 3, url: 'https://example.com/a', incognito: false };
  const blocks = { paused: false, paused_tabs: [], excluded_hosts: [] };
  assert.equal(allowed(tab, null), false);
  assert.equal(allowed({ ...tab, incognito: true }, blocks), false);
  assert.equal(allowed(tab, { ...blocks, paused: true }), false);
  assert.equal(allowed(tab, { ...blocks, paused_tabs: [3] }), false);
  assert.equal(allowed(tab, { ...blocks, excluded_hosts: ['example.com'] }), false);
  assert.equal(allowed(tab, blocks), true);
});
test('secure field markers', () => {
  const field = (type, autocomplete, marked = false) => ({ type, autocomplete, hasAttribute: () => marked });
  assert.equal(isSecure(field('password', '')), true);
  assert.equal(isSecure(field('text', 'section-a cc-number')), true);
  assert.equal(isSecure(field('text', 'one-time-code')), true);
  assert.equal(isSecure(field('text', '', true)), true);
  assert.equal(isSecure(field('text', 'name')), false);
});

import { safeUrl, safeTitle, textLines, sameLines, mediaDue, MAX_LINE, MAX_LINES, MAX_CHARS, MEDIA_EVERY_MS, SKIP_SELECTOR } from './logic.js';

test('the full address keeps path and query but never credentials, and only for http(s)', () => {
  assert.equal(safeUrl('https://user:pw@Example.com/a/b?q=1#top'), 'https://example.com/a/b?q=1#top');
  assert.equal(safeUrl('file:///c:/secret.txt'), null);
  assert.equal(safeUrl('chrome://settings'), null);
  assert.equal(safeUrl('https://example.com/' + 'x'.repeat(3000)), null);
  assert.equal(safeTitle('  Inbox\n (3) '), 'Inbox (3)');
  assert.equal(safeTitle(''), null);
});
test('page text is normalised and capped, readable part first', () => {
  assert.deepEqual(textLines(['  Heading ', '', 'a\n\n b', null]), ['Heading', 'a b']);
  assert.equal(textLines(['x'.repeat(MAX_LINE + 50)])[0].length, MAX_LINE);
  assert.equal(textLines(Array(MAX_LINES + 10).fill('y')).length, MAX_LINES);
  const big = textLines(Array(40).fill('z'.repeat(400)));
  assert.ok(big.reduce((n, l) => n + l.length, 0) <= MAX_CHARS);
  assert.ok(sameLines(['a', 'b'], ['a', 'b']));
  assert.ok(!sameLines(['a'], ['a', 'b']));
});
test('video changes report at once, playing reports at most every 10 s', () => {
  assert.ok(mediaDue(null, 0, true));
  assert.ok(!mediaDue({ playing: true, at: 0 }, MEDIA_EVERY_MS - 1, true));
  assert.ok(mediaDue({ playing: true, at: 0 }, MEDIA_EVERY_MS, true));
  assert.ok(mediaDue({ playing: true, at: 0 }, 1, false));
  assert.ok(!mediaDue({ playing: false, at: 0 }, 60_000, false));
});
test('the never-read list covers form values and secure fields', () => {
  for (const s of ['textarea', 'input', '[type="password"]', '[autocomplete^="cc-"]', 'one-time-code', 'current-password', 'new-password', 'contenteditable']) {
    assert.ok(SKIP_SELECTOR.includes(s), s);
  }
});
test('the content script carries the same never-read list (it cannot import modules)', async () => {
  const { readFile } = await import('node:fs/promises');
  const { SKIP_SELECTOR } = await import('./logic.js');
  const src = await readFile(new URL('./content.js', import.meta.url), 'utf8');
  assert.ok(src.includes(`const SKIP = '${SKIP_SELECTOR}';`));
});
test('private windows need the profile opt-in and the browser permission, and blocks still apply', () => {
  const tab = { id: 3, url: 'https://example.com/a', incognito: true };
  const blocks = { paused: false, paused_tabs: [], excluded_hosts: [] };
  assert.equal(allowed(tab, blocks), false);
  assert.equal(allowed(tab, blocks, privateAllowed(true, false)), false);
  assert.equal(allowed(tab, blocks, privateAllowed('true', true)), false);
  assert.equal(allowed(tab, blocks, privateAllowed(true, true)), true);
  assert.equal(allowed(tab, { ...blocks, excluded_hosts: ['example.com'] }, true), false);
});
