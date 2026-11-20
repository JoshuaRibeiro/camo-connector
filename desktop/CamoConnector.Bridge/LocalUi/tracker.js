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

function renderOverview() {
  const content = byId('overview-content');
  const wasExpanded = content.querySelector('details')?.open ?? false;
  const weapon = catalog.weapons.find(item => item.id === state.focus);
  if (!weapon) {
    content.replaceChildren(element('p', 'Select a weapon to see its next unlocks.', 'subtitle'));
    return;
  }

  const overview = state.overview;
  const heading = [weapon.name];
  if (overview.minimumLevel) heading.push('Weapon level ' + (overview.level ?? overview.minimumLevel + '+'));
  const entries = [element('p', heading.join(' · '), 'subtitle')];
  const rules = new Map(weapon.camos.map(rule => [rule.id, rule]));
  for (const row of overview.rows) entries.push(camoRow(rules.get(row.id), weapon.id, row));
  if (overview.waiting) {
    const waiting = overview.waiting;
    const next = element('div', null, 'camo-row');
    const copy = element('div');
    copy.append(element('div', waiting.name, 'camo-name'));
    copy.append(element('p', waiting.unlock ?? 'Complete the prerequisite camos to unlock this challenge.', 'prerequisite'));
    if (waiting.count != null && waiting.goal != null)
      copy.append(element('p', waiting.count + '/' + waiting.goal, 'subtitle'));
    const description = element('div', null, 'camo-description');
    if (waiting.artwork) {
      const image = element('img', null, 'camo-artwork');
      image.src = waiting.artwork;
      image.alt = '';
      description.append(image);
    }
    description.append(copy);
    next.append(description);
    entries.push(next);
  } else if (!overview.rows.length) entries.push(element('p', overview.complete
    ? 'All weapon camos complete' : 'No camo challenges available', 'overview-empty'));

  const completed = weapon.camos.filter(rule => state.progress[weapon.id + ':' + rule.id]?.done);
  if (completed.length) {
    const history = element('details', null, 'overview-completed');
    history.open = wasExpanded;
    history.append(element('summary', 'Completed unlocks (' + completed.length + ')'));
    for (const rule of completed) history.append(camoRow(rule, weapon.id, {}));
    entries.push(history);
  }
  content.replaceChildren(...entries);
}

function render() {
  renderOverview();
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
  if (weapon) {
    const level = state.progress[weapon.id + ':weapon-level']?.count;
    entries.push(element('p', weapon.category + (level == null ? '' : ' · Weapon level ' + level), 'subtitle'));
  }
  for (const group of new Set(rules.map(rule => rule.type))) {
    const section = element('section', null, 'group');
    section.append(element('h3', group === 'Multiplayer' ? 'Multiplayer mastery' : group));
    for (const rule of rules.filter(item => item.type === group)) section.append(camoRow(rule, selected));
    entries.push(section);
  }
  detail.replaceChildren(...entries);
}

function camoRow(rule, weapon, overview) {
  const current = state.progress[weapon + ':' + rule.id] ?? { count: 0, done: false };
  const row = element('div', null, 'camo-row');
  const copy = element('div');
  copy.append(element('div', rule.name, 'camo-name'));
  if (rule.objective) copy.append(element('p', rule.objective));
  if (rule.unlock) copy.append(element('p', rule.unlockTotal == null ? rule.unlock :
    rule.unlock.replace(/\bmore\s+/i, ''), 'prerequisite'));
  const controls = element('div', null, 'controls');
  if (overview && !current.done && overview.count != null && overview.goal != null) {
    const meter = element('progress');
    meter.max = overview.goal;
    meter.value = overview.count;
    meter.setAttribute('aria-label', rule.name + ' progress');
    const progress = element('div', null, 'overview-meter');
    progress.append(meter, element('span', overview.count + ' / ' + overview.goal));
    copy.append(progress);
  }
  if (overview && rule.goal != null && !current.done) {
    for (const increment of [1, 5]) {
      const button = element('button', '+' + increment);
      button.setAttribute('aria-label', 'Add ' + increment + ' to ' + rule.name);
      button.addEventListener('click', () => void save('edit', { weapon, camo: rule.id, increment }));
      controls.append(button);
    }
  }
  if (!overview && rule.goal != null) {
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
  const done = element('button', current.done ? (overview ? 'Untick' : 'Done') : (overview ? 'Tick' : 'Mark done'));
  done.setAttribute('aria-label', (current.done ? 'Undo ' : 'Mark complete: ') + rule.name);
  done.setAttribute('aria-pressed', String(current.done));
  done.addEventListener('click', () => void save('edit', {
    weapon, camo: rule.id, count: current.done ? 0 : rule.goal ?? 0, done: !current.done
  }));
  controls.append(done);
  const description = element('div', null, 'camo-description');
  if (rule.artwork) {
    const image = element('img', null, 'camo-artwork');
    image.src = rule.artwork;
    image.alt = '';
    description.append(image);
  }
  description.append(copy);
  row.append(description, controls);
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
  finally { setTimeout(poll, 300); }
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
