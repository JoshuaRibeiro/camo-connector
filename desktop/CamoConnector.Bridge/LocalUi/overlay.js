'use strict';

const key = location.hash.slice(1);
const byId = id => document.getElementById(id);
const legacyLayout = new URLSearchParams(location.search).get('layout');

const armouryLayouts = ['glow-accent', 'gradient-edge-sweep', 'corner-accents', 'subtle-wave', 'minimal-glow'];
const layouts = [...armouryLayouts, 'default', 'jabber', '11annah', 'field-manual', 'afterglow', 'scoreboard', 'blueprint', 'filmstrip'];
let previous;

function render(data) {
  const layout = data.overlayLayout ?? legacyLayout;
  for (const id of layouts) document.body.classList.toggle('layout-' + id, layout === id);
  byId('overlay-card').hidden = !data.weapon;
  byId('weapon').textContent = data.weapon ?? '';
  document.body.classList.toggle('armoury', armouryLayouts.includes(layout));
  const jabber = layout === 'jabber';
  byId('jabber-class').textContent = data.category ?? '';
  const level = byId('weapon-level');
  level.hidden = !data.weapon;
  const prefix = armouryLayouts.includes(layout) ? '\u2303 LVL ' : jabber ? '\u2197 LVL ' : 'LVL ';
  let value = '-';
  if (data.maxLevel) value = 'MAX';
  else if (data.level != null) value = data.level;
  else if (data.minimumLevel) {
    const confirmed = data.maximumCamoLevel > 0 && data.minimumLevel >= data.maximumCamoLevel;
    value = data.minimumLevel + (confirmed ? '' : '+');
  } else if (data.minimumUnlocks || data.weaponUnlocks != null) {
    const confirmed = data.weaponUnlocks != null || data.minimumUnlocks === data.maximumCamoUnlocks;
    value = (data.minimumUnlocks ?? data.weaponUnlocks) + (confirmed ? '' : '+');
  }
  level.textContent = prefix + value;
  const available = data.rows ?? [];
  const visible = available.length || !data.waiting ? available : [{
    ...data.waiting, objective: data.waiting.unlock
  }];
  if (armouryLayouts.includes(layout)) byId('armoury-count').textContent = visible.length;
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
    } else if (row.placeholder) {
      const tile = document.createElement('span');
      tile.className = 'camo-artwork preview-swatch';
      tile.setAttribute('aria-hidden', 'true');
      section.append(tile);
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
if (layouts.includes(preview)) {
  document.body.classList.add('preview');
  const themes = {
    "default": ["Default", "The OG", "the original camo connector layout", "Lime trim", "A slim green edge frames compact, left-aligned tiles."],
    "jabber": ["Jabber", "Jabber's Theme", "Jabber's personal touch", "Gold in motion", "Three soft gold highlights travel around the purple-trimmed panel."],
    "11annah": ["11annah", "11annah's theme", "Camo Connector's first custom theme", "Teal edge", "Large artwork sits beside each objective on a translucent card."],
    "afterglow": ["Afterglow", "Plum glass", "A plum gradient frames each inset panel.", "Round portraits", "Circular tiles sit on the right, opposite rose-coloured headings."],
    "scoreboard": ["Eclipse", "Violet trim", "A fine lavender line runs across the top edge.", "Level badge", "A rounded badge balances the weapon name above softly squared tiles."],
    "filmstrip": ["Ember", "Copper edge", "A warm copper rail anchors the charcoal panel.", "Framed tiles", "Large square swatches sit beside peach-coloured headings."],
    "blueprint": ["Tide", "Deep teal", "A translucent sea-green panel fades into navy.", "Mint accents", "Inset rows pair a mint rail with artwork on the right."],
    "field-manual": ["Velvet", "Smoked plum", "Rose headings sit against a soft plum gradient.", "Rose rings", "Fine rings frame the circular tiles beneath a slim divider."]
  };
  if (armouryLayouts.includes(preview)) {
    render({
      overlayLayout: preview, weapon: 'Cronen Osprey', category: 'Assault Rifle', level: 69,
      rows: [{ name: 'Harmonic Break', objective: 'Complete 33 Platinum Camo Challenges',
        artwork: '/local/artwork/harmonic-break.png', count: 29, goal: 33 }]
    });
  } else {
    const [weapon, first, intro, second, detail] = themes[preview];
    render({
      overlayLayout: preview, weapon, category: 'Layout', level: 34,
      rows: [
        { name: first, objective: intro, placeholder: true, count: 34, goal: 36 },
        { name: second, objective: detail, placeholder: true, count: 0, goal: 10 }
      ]
    });
  }
  const frame = window.frameElement;
  if (frame) {
    const card = byId('overlay-card');
    new ResizeObserver(() => {
      if (card.offsetHeight) frame.height = Math.ceil(card.getBoundingClientRect().height + (armouryLayouts.includes(preview) ? 16 : 0));
    }).observe(card);
  }
} else {
  void refresh();
}
