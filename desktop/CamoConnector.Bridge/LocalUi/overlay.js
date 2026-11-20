'use strict';

const key = location.hash.slice(1);
const byId = id => document.getElementById(id);

let previous;

function render(data) {
  byId('overlay-card').hidden = !data.weapon;
  byId('weapon').textContent = data.weapon ?? '';
  const level = byId('weapon-level');
  level.hidden = !data.minimumLevel;
  level.textContent = data.level != null ? 'Weapon level ' + data.level :
    'Weapon level ' + data.minimumLevel + '+';

  const rows = (data.rows ?? []).map(row => {
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
      progress.max = row.goal;
      progress.value = row.count;
      progress.setAttribute('aria-label', row.name);
      const count = document.createElement('span');
      count.textContent = row.count + ' / ' + row.goal;
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
    setTimeout(refresh, 750);
  }
}
void refresh();
