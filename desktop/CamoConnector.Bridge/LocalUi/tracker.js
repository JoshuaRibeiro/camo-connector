'use strict';

const byId = id => document.getElementById(id);
const key = location.hash.slice(1);
const statusLine = byId('status');
const overlayButtons = document.querySelectorAll('[data-overlay]');
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
  if (!response.ok) throw new Error('Local tracker request failed (' + response.status + ').');
  return response.json();
}

function element(tag, text, className) {
  const node = document.createElement(tag);
  if (text != null) node.textContent = text;
  if (className) node.className = className;
  return node;
}

function progressMeter(label, count, total) {
  const meter = element('progress');
  meter.max = total || 1;
  meter.value = count;
  meter.setAttribute('aria-label', label);
  return meter;
}

function completedCount(weapon, rules) {
  return rules.filter(rule => state.progress[weapon + ':' + rule.id]?.done).length;
}

function renderCollection() {
  const total = catalog.shared.length + catalog.weapons.reduce((sum, weapon) => sum + weapon.camos.length, 0);
  const done = completedCount('shared', catalog.shared) + catalog.weapons.reduce((sum, weapon) =>
    sum + completedCount(weapon.id, weapon.camos), 0);
  byId('overall-count').textContent = done + ' / ' + total + ' camos unlocked';
  const overall = byId('overall-meter');
  overall.max = total || 1;
  overall.value = done;

  const tiers = new Map();
  for (const weapon of catalog.weapons) {
    for (const rule of weapon.camos.filter(rule => rule.type === 'Completionist')) {
      if (!tiers.has(rule.id)) tiers.set(rule.id, { rule, done: 0, total: 0 });
      const tier = tiers.get(rule.id);
      tier.total++;
      tier.done += completedCount(weapon.id, [rule]);
    }
  }
  const cards = [...tiers.values()].map(({ rule, done, total }) => {
    const card = element('article', null, 'mastery-tile');
    if (rule.artwork) {
      const image = element('img');
      image.src = rule.artwork;
      image.alt = '';
      card.append(image);
    }
    const copy = element('div', null, 'tile-copy');
    const caption = element('div', null, 'tile-caption');
    caption.append(element('h3', rule.name), element('span', done + '/' + total));
    copy.append(caption, progressMeter(rule.name + ' completion', done, total));
    card.append(copy);
    return card;
  });
  byId('mastery-tiles').replaceChildren(...cards);
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
    for (const weapon of matches) entries.push(selection(weapon.id, weapon.name, weapon.camos));
  }
  if (!query) entries.push(selection('shared', 'Prestige & mastery'));
  roster.replaceChildren(...entries);
}

function selection(id, label, rules = catalog.shared) {
  const button = element('button');
  button.append(element('span', label), element('span', completedCount(id, rules) + '/' + rules.length, 'weapon-count'));
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
  const heading = element('div', null, 'detail-heading');
  heading.append(element('h3', weapon.name));
  if (overview.level != null || overview.minimumLevel)
    heading.append(element('span', 'Weapon level ' + (overview.level ?? overview.minimumLevel + '+'), 'subtitle'));
  const entries = [heading];
  const rules = new Map(weapon.camos.map(rule => [rule.id, rule]));
  for (const row of overview.rows) entries.push(camoRow(rules.get(row.id), weapon.id, row));
  if (overview.waiting) {
    const waiting = overview.waiting;
    const next = element('div', null, 'camo-row');
    const copy = element('div');
    copy.append(element('div', waiting.name, 'camo-name'));
    copy.append(element('p', waiting.unlock ?? 'Complete the prerequisite camos to unlock this challenge.', 'prerequisite'));
    if (waiting.count != null && waiting.goal != null) {
      const progress = element('div', null, 'overview-meter');
      progress.append(progressMeter(waiting.name + ' prerequisite progress', waiting.count, waiting.goal),
        element('span', waiting.count + '/' + waiting.goal));
      copy.append(progress);
    }
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
  for (const button of overlayButtons) {
    const active = button.dataset.overlay === (state.overlayLayout ?? 'default');
    button.setAttribute('aria-pressed', String(active));
  }
  renderCollection();
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
  const detailHeading = element('div', null, 'detail-heading');
  const headingMeta = element('div', null, 'weapon-heading-meta');
  const level = weapon && (state.focus === weapon.id
    ? state.overview.level ?? (state.overview.minimumLevel ? state.overview.minimumLevel + '+' : null)
    : state.progress[weapon.id + ':weapon-level']?.count);
  if (weapon && level != null) headingMeta.append(element('span', 'Weapon level ' + level, 'subtitle'));
  headingMeta.append(element('span', completedCount(selected, rules) + ' / ' + rules.length, 'weapon-count'));
  detailHeading.append(title, headingMeta);
  const entries = [detailHeading];
  if (weapon) {
    entries.push(element('p', weapon.category, 'subtitle'));
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
  row.classList.toggle('camo-complete', current.done);
  const copy = element('div');
  copy.append(element('div', rule.name, 'camo-name'));
  if (rule.objective) copy.append(element('p', rule.objective));
  if (rule.unlock) copy.append(element('p', rule.unlockTotal == null ? rule.unlock :
    rule.unlock.replace(/\bmore\s+/i, ''), 'prerequisite'));
  const controls = element('div', null, 'controls');
  if (overview && !current.done && overview.count != null && overview.goal != null) {
    const meter = progressMeter(rule.name + ' progress', overview.count, overview.goal);
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
    if (!current.done && current.count > 0) {
      const progress = element('div', null, 'overview-meter');
      progress.append(progressMeter(rule.name + ' progress', current.count, rule.goal));
      copy.append(progress);
    }
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
function showSection(page) {
  const overlays = page === 'overlays';
  byId('tracker-content').hidden = overlays;
  byId('overlay-settings').hidden = !overlays;
}
showSection(new URLSearchParams(location.search).get('page'));
window.chrome?.webview?.addEventListener('message', event => {
  if (event.data === 'tracker' || event.data === 'overlays') showSection(event.data);
});
for (const button of overlayButtons) button.addEventListener('click', () => {
  void save('overlay-layout', { layout: button.dataset.overlay }, 'OBS overlay layout updated.');
});
byId('copy-overlay-url').addEventListener('click', async () => {
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
