'use strict';

const byId = id => document.getElementById(id);
const key = location.hash.slice(1);
const statusLine = byId('status');
let catalog;
let overlayUrl;
let state;
let selected;
let editing = false;

async function request(path, body) {
  const options = { headers: { Authorization: 'Bearer ' + key } };
  if (body !== undefined) {
    options.method = 'POST';
    options.headers['Content-Type'] = 'application/json';
    options.body = JSON.stringify(body);
  }
  const response = await fetch('/local/' + path, options);
  if (!response.ok) throw new Error(path === 'import' && response.status === 404
    ? 'No compatible Multiplayer backup found.' : 'Local tracker request failed (' + response.status + ').');
  return response.json();
}

function element(tag, text, className) {
  const node = document.createElement(tag);
  if (text != null) node.textContent = text;
  if (className) node.className = className;
  return node;
}

function choose(id) {
  if (id === 'shared') {
    selected = id;
    render();
  } else {
    void save('edit', { weapon: id }, 'Tracking ' + catalog.weapons.find(weapon => weapon.id === id).name, id);
  }
}

function renderRoster() {
  if (!catalog) return;
  const roster = byId('roster');
  const query = byId('search').value.trim().toLowerCase();
  const entries = [];
  for (const category of new Set(catalog.weapons.map(weapon => weapon.category))) {
    const matches = catalog.weapons.filter(weapon => weapon.category === category && weapon.name.toLowerCase().includes(query));
    if (!matches.length) continue;
    entries.push(element('h2', category));
    for (const weapon of matches) entries.push(selection(weapon.id, weapon.name));
  }
  if (!query) entries.push(selection('shared', 'Prestige & mastery'));
  roster.replaceChildren(...entries);
}

function selection(id, label) {
  const button = element('button', label);
  if (id === selected) button.setAttribute('aria-current', 'true');
  button.addEventListener('click', () => choose(id));
  return button;
}

function render() {
  renderRoster();
  const detail = byId('detail');
  const weapon = catalog.weapons.find(weapon => weapon.id === selected);
  const shared = selected === 'shared';
  const rules = shared ? catalog.shared : weapon?.camos;
  if (!rules) {
    detail.replaceChildren(element('h2', 'Select a weapon'));
    return;
  }
  const title = element('h2', weapon?.name ?? 'Prestige & mastery');
  const entries = [title];
  if (weapon) entries.push(element('p', weapon.category, 'subtitle'));
  for (const group of new Set(rules.map(rule => rule.type))) {
    const section = element('section', null, 'group');
    section.append(element('h3', group === 'Multiplayer' ? 'Multiplayer mastery' : group));
    for (const rule of rules.filter(item => item.type === group)) section.append(camoRow(rule, selected));
    entries.push(section);
  }
  detail.replaceChildren(...entries);
}

function camoRow(rule, weapon) {
  const current = state.progress[weapon + ':' + rule.id] ?? { count: 0, done: false };
  const row = element('div', null, 'camo-row');
  const copy = element('div');
  copy.append(element('div', rule.name, 'camo-name'));
  if (rule.objective) copy.append(element('p', rule.objective));
  if (rule.unlock) copy.append(element('p', rule.unlockTotal == null ? rule.unlock :
    rule.unlock.replace(/\bmore\s+/i, ''), 'prerequisite'));
  const controls = element('div', null, 'controls');
  if (rule.goal != null) {
    const input = element('input');
    input.type = 'number';
    input.min = '0';
    input.max = String(rule.goal);
    input.value = String(current.count);
    input.setAttribute('aria-label', rule.name + ' progress');
    input.addEventListener('change', () => {
      if (input.value === '' || !input.checkValidity()) { input.value = String(current.count); return; }
      void save('edit', { weapon, camo: rule.id, count: input.valueAsNumber, done: input.valueAsNumber === rule.goal });
    });
    controls.append(input, element('span', '/ ' + rule.goal));
  }
  const done = element('button', current.done ? 'Done' : 'Mark done');
  done.setAttribute('aria-label', (current.done ? 'Undo ' : 'Mark complete: ') + rule.name);
  done.setAttribute('aria-pressed', String(current.done));
  done.addEventListener('click', () => void save('edit', {
    weapon, camo: rule.id, count: current.done ? 0 : rule.goal ?? 0, done: !current.done
  }));
  controls.append(done);
  row.append(copy, controls);
  return row;
}

async function save(path, body, message = '', focus) {
  if (editing || !catalog) return;
  editing = true;
  try {
    state = await request(path, body);
    selected = focus ?? selected ?? state.focus;
    statusLine.textContent = message;
    render();
  } catch (error) { statusLine.textContent = error.message; }
  finally { editing = false; }
}

async function poll() {
  try {
    if (editing || document.activeElement?.matches('#detail input')) return;
    if (!catalog) ({ catalog, overlayUrl } = await request('catalog'));
    const next = await request('state');
    if (editing || document.activeElement?.matches('#detail input')) return;
    if (JSON.stringify(next) === JSON.stringify(state)) return;
    if (!selected || next.focus !== state?.focus) selected = next.focus ?? selected;
    state = next;
    render();
  } catch (error) { statusLine.textContent = error.message; }
  finally { setTimeout(poll, 750); }
}

byId('search').addEventListener('input', renderRoster);
byId('import').addEventListener('click', () => void save('import', {}, 'Saved progress merged.'));
byId('overlay').addEventListener('click', async () => {
  if (!overlayUrl) return;
  try {
    await navigator.clipboard.writeText(overlayUrl);
    statusLine.textContent = 'OBS Browser Source URL copied.';
  } catch {
    const input = byId('overlay-url');
    input.value = overlayUrl;
    byId('overlay-dialog').showModal();
    input.select();
  }
});
void poll();
