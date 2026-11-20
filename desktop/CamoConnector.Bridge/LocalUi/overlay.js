'use strict';

const key = location.hash.slice(1);
const byId = id => document.getElementById(id);

async function refresh() {
  try {
    const response = await fetch('/local/overlay-state', { headers: { Authorization: 'Bearer ' + key } });
    if (!response.ok) throw new Error('Overlay unavailable');
    const data = await response.json();
    byId('overlay-card').hidden = !data.weapon;
    byId('weapon').textContent = data.weapon ?? '';
    byId('camo').textContent = data.camo ?? 'All camos complete';
    byId('objective').textContent = data.objective ?? '';
    byId('meter').hidden = !data.goal;
    if (data.goal) {
      byId('progress').max = data.goal;
      byId('progress').value = data.count;
      byId('count').textContent = data.count + ' / ' + data.goal;
    }
  } catch {
    byId('overlay-card').hidden = true;
  } finally {
    setTimeout(refresh, 750);
  }
}
void refresh();
