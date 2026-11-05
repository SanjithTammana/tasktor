import test from 'node:test';
import assert from 'node:assert/strict';
import { hostname, allowed, isSecure } from './logic.js';

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
