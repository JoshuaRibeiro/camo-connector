'use strict';

const key = location.hash.slice(1);
const byId = id => document.getElementById(id);
const legacyLayout = new URLSearchParams(location.search).get('layout');

const armouryLayouts = ['glow-accent', 'gradient-edge-sweep', 'corner-accents', 'subtle-wave', 'minimal-glow'];
const layouts = [...armouryLayouts, 'default', 'jabber', '11annah', 'field-manual', 'afterglow', 'scoreboard', 'blueprint', 'filmstrip'];
let previous;

// Each pose shares its side walls and rounded corners. Only the two long edges flex.
function sizeWaveFrame() {
  if (!document.body.classList.contains('layout-subtle-wave')) return;
  // The SVG fills the card's padding box. Draw in local units, before OBS custom CSS zoom.
  const { clientWidth: width, clientHeight: height } = byId('overlay-card');
  if (width < 24 || height < 24) return;
  const r = Math.min(14, height * .085);
  const left = 2, right = width - 2, top = 3, bottom = height - 3;
  const start = left + r, end = right - r;
  const leftWall = `Q ${left} ${bottom} ${left} ${bottom-r} V ${top+r} Q ${left} ${top} ${start} ${top}`;

  function edge(from, to, baseline, phase) {
    const steps = 8, step = (to - from) / steps;
    const heights = Array.from({length: steps + 1}, (_, n) => {
      const t = n / steps;
      return baseline + 2.65 * Math.sin(Math.PI * t) ** 2 * Math.sin(3 * Math.PI * t + phase);
    });
    function tangent(n) {
      return n === 0 || n === steps ? 0 : (heights[n + 1] - heights[n - 1]) / 2;
    }
    let path = '';
    for (let n = 1; n <= steps; n++) {
      const x = from + n * step;
      path += ` C ${x - 2 * step / 3} ${heights[n - 1] + tangent(n - 1) / 3}`
        + ` ${x - step / 3} ${heights[n] - tangent(n) / 3} ${x} ${heights[n]}`;
    }
    return path;
  }
  function outline(phase) {
    return `M ${start} ${top}` + edge(start, end, top, phase)
      + ` Q ${right} ${top} ${right} ${top+r} V ${bottom-r} Q ${right} ${bottom} ${end} ${bottom}`
      + edge(end, start, bottom, phase + 1) + ' ' + leftWall + ' Z';
  }
  const path = byId('wave-outline');
  path.setAttribute('d', outline(0));
  path.replaceChildren();
  if (waveMotion.matches) return;
  const motion = document.createElementNS('http://www.w3.org/2000/svg', 'animate');
  motion.setAttribute('attributeName', 'd');
  motion.setAttribute('dur', '6s');
  motion.setAttribute('repeatCount', 'indefinite');
  motion.setAttribute('values', Array.from({length: 13}, (_, n) => outline(n*Math.PI/6)).join(';'));
  path.append(motion);
}
const waveMotion = matchMedia('(prefers-reduced-motion: reduce)');
waveMotion.addEventListener('change', sizeWaveFrame);
new ResizeObserver(sizeWaveFrame).observe(byId('overlay-card'));

function render(data) {
  const layout = data.overlayLayout ?? legacyLayout;
  const enteringWave = layout === 'subtle-wave' && !document.body.classList.contains('layout-subtle-wave');
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
  if (enteringWave) sizeWaveFrame();
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
    "field-manual": ["Velvet", "Smoked plum", "Rose headings sit against a soft plum gradient.", "Rose rings", "Fine rings frame the circular tiles beneath a slim divider."],
    "glow-accent": ["Glow Accent", "Cyan pulse", "Light swells and fades along the left edge.", "Cool glass", "Mint headings and progress bars sit on translucent charcoal."],
    "gradient-edge-sweep": ["Gradient Edge Sweep", "Travelling light", "Peach and pink flares sweep clockwise along a fine rose rim.", "Sunset gradient", "The progress fill blends from apricot to pink."],
    "corner-accents": ["Corner Accents", "Corner loop", "The top-left and bottom-right brackets pulse in turn.", "Warm highlights", "Peach headings and progress bars echo the corners."],
    "subtle-wave": ["Subtle Wave", "Twin waves", "Teal waves drift along the top and bottom in opposite directions.", "Seafoam trim", "A fine teal outline echoes the colour of the progress bars."],
    "minimal-glow": ["Minimal Glow", "Breathing light", "A violet halo slowly brightens and fades around the card.", "Lavender details", "A fine border frames soft purple headings and progress bars."]
  };
  const [weapon, first, intro, second, detail] = themes[preview];
  render({
    overlayLayout: preview, weapon, category: 'Layout', level: 34,
    rows: [
      { name: first, objective: intro, placeholder: true, count: 34, goal: 36 },
      { name: second, objective: detail, placeholder: true, count: 0, goal: 10 }
    ]
  });
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
