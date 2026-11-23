'use strict';

const key = location.hash.slice(1);
const byId = id => document.getElementById(id);
const legacyLayout = new URLSearchParams(location.search).get('layout');

const edge = document.querySelector('.jabber-edge');
new ResizeObserver(([entry]) => {
  const { width, height } = entry.contentRect;
  if (!width || !height) return;
  const radius = Math.min(8, width / 2, height / 2);
  byId('jabber-route').setAttribute('d',
    `M0 0 H${width - radius} Q${width} 0 ${width} ${radius} V${height - radius} Q${width} ${height} ${width - radius} ${height} H0`);
}).observe(edge);

let previous;

function render(data) {
  document.body.classList.toggle('layout-11annah', (data.overlayLayout ?? legacyLayout) === '11annah');
  byId('overlay-card').hidden = !data.weapon;
  byId('weapon').textContent = data.weapon ?? '';
  const jabber = (data.overlayLayout ?? legacyLayout) === 'jabber';
  document.body.classList.toggle('layout-jabber', jabber);
  byId('jabber-class').textContent = data.category ?? '';
  const level = byId('weapon-level');
  level.hidden = !data.weapon;
  level.textContent = data.maxLevel ? 'Weapon level MAX' : data.maximumCamoLevel > 0 && data.minimumLevel >= data.maximumCamoLevel
    ? 'Weapon level ' + (data.level ?? data.minimumLevel) :
    data.level != null ? 'Weapon level ' + data.level :
    data.minimumLevel ? 'Weapon level ' + data.minimumLevel + '+' :
    data.minimumUnlocks ? 'Weapon unlocks ' + data.minimumUnlocks +
      (data.weaponUnlocks != null || data.minimumUnlocks === data.maximumCamoUnlocks ? '' : '+') : 'Weapon level —';

  if (jabber) level.textContent = level.textContent.replace('Weapon level ', '↗ LVL ');
  const available = data.rows ?? [];
  const visible = available.length || !data.waiting ? available : [{
    ...data.waiting, objective: data.waiting.unlock
  }];
  const rows = visible.map(row => {
    const section = document.createElement('section');
    section.className = 'overlay-unlock';
    const title = document.createElement('h2');
    title.textContent = row.name;
    const objective = document.createElement('p');
    objective.textContent = row.objective ?? '';
    if (row.artwork) {
      const image = document.createElement('img');
      image.className = 'camo-artwork';
      image.src = row.artwork;
      image.alt = '';
      section.append(image);
    }
    const copy = document.createElement('div');
    copy.append(title);
    if (row.objective) copy.append(objective);
    section.append(copy);
    if (row.goal && row.count != null) {
      const meter = document.createElement('div');
      meter.className = 'overlay-meter';
      const progress = document.createElement('progress');
      updateProgress(progress, data.weapon + ':' + row.name + ':' + row.objective, row.count, row.goal);
      progress.setAttribute('aria-label', row.name);
      const count = document.createElement('span');
      count.textContent = row.count + '/' + row.goal;
      meter.append(progress, count);
      copy.append(meter);
    }
    return section;
  });
  if (data.weapon && !rows.length) {
    const status = document.createElement('p');
    status.textContent = data.complete ? 'All weapon camos complete' : 'No camo challenges available';
    rows.push(status);
  }
  byId('unlock-rows').replaceChildren(...rows);
}

async function refresh() {
  try {
    const response = await fetch('/local/overlay-state', { headers: { Authorization: 'Bearer ' + key } });
    if (!response.ok) throw new Error('Overlay unavailable');
    const payload = await response.text();
    if (payload !== previous) {
      render(JSON.parse(payload));
      previous = payload;
    }
  } catch {
    previous = undefined;
    byId('overlay-card').hidden = true;
  } finally {
    setTimeout(refresh, 300);
  }
}
const preview = new URLSearchParams(location.search).get('preview');
if (['default', 'jabber', '11annah'].includes(preview)) {
  render({
    overlayLayout: preview, weapon: 'JOKR', category: 'Launcher', level: 34,
    rows: [
      { name: 'Military Camo 7', objective: 'Reach Weapon Level 36', artwork: '/local/artwork/military-camo-7.png', count: 34, goal: 36 },
      { name: 'Tectonic', objective: 'Get 10 Longshot Kills against Players', artwork: '/local/artwork/tectonic.png', count: 0, goal: 10 }
    ]
  });
  const frame = window.frameElement;
  if (frame) {
    const card = byId('overlay-card');
    new ResizeObserver(() => {
      if (card.offsetHeight) frame.height = Math.ceil(card.getBoundingClientRect().height + 24);
    }).observe(card);
  }
} else {
  void refresh();
}
