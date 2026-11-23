'use strict';

const key = location.hash.slice(1);
const byId = id => document.getElementById(id);
const legacyLayout = new URLSearchParams(location.search).get('layout');

const armouryLayouts = ['glow-accent', 'gradient-edge-sweep', 'corner-accents', 'subtle-wave', 'minimal-glow'];
const layouts = [...armouryLayouts, 'default', 'jabber', '11annah', 'field-manual', 'afterglow', 'scoreboard', 'blueprint', 'filmstrip'];
let previous;
let lastWaveSize;

// Left-to-right samples of the reference rim at 24 equal intervals across its 307 px span.
// Each pair is [centre offset, line width] in CSS pixels. Extra samples join the ends offscreen.
const waveSteps = 24;
const topWave = [
  [-2, .9], [-2.3, 1.4], [-1.9, 1.6], [-1, 1.3], [-.5, 1.9], [-.4, 2.1],
  [-.5, 1.8], [-1.4, 1.8], [-1.9, 1.4], [-1.8, 1.6], [-1, 1.8], [.3, 2],
  [1.5, 2.1], [2.3, 1.6], [2.8, 1.6], [2.4, 1.7], [1.5, 1.9], [.2, 1.9],
  [-1.1, 2.1], [-2.3, 2.2], [-2.6, 2], [-2.4, 1.7], [-2, 1.3], [-1, .9],
  [0, 1], [.3, 1.2], [-.4, 1.2], [-1.3, 1]
];
const bottomWave = [
  [2.1, 1], [1.9, 1.3], [1, 1.1], [.3, 1.7], [.1, 2.3], [0, 1.9],
  [-.6, 1.6], [-1.6, 1.4], [-2.2, 1.4], [-2.4, 1.7], [-2, 1.3], [-1.1, 1.8],
  [.2, 2.3], [1.2, 2], [1.8, 2], [1.6, 2.1], [.7, 2.2], [-.6, 2],
  [-1.7, 1.5], [-2.3, 1.6], [-2, 1.1], [-.6, 1.3], [1.2, 1.8], [2, 1.6], [2, 1]
];

// Both contours share the same wave. Their separation is the visible width of the rim.
function sizeWaveFrame() {
  if (!document.body.classList.contains('layout-subtle-wave')) return;
  // The SVG fills the card's padding box. Draw in local units, before OBS custom CSS zoom.
  const { clientWidth: width, clientHeight: height } = byId('overlay-card');
  if (width < 24 || height < 24) return;
  const reducedMotion = waveMotion.matches;
  if (lastWaveSize?.width === width && lastWaveSize.height === height && lastWaveSize.reducedMotion === reducedMotion) return;
  lastWaveSize = {width, height, reducedMotion};
  const r = Math.min(14, height * .085, (width - 4) / 4);
  const left = 2, right = width - 2, top = 3, bottom = height - 3;
  const start = left + r, end = right - r;
  const scale = Math.min((end - start) / 307, height / 118, 1.5);

  function contour(profile, baseline, turn, side) {
    return Array.from({length: waveSteps + 1}, (_, n) => {
      // Advancing both profiles in screen coordinates carries their crests to the left.
      const position = n + turn * profile.length;
      const index = Math.floor(position);
      const fraction = position - index;
      const [y0, w0] = profile[index % profile.length];
      const [y1, w1] = profile[(index + 1) % profile.length];
      const centre = baseline + (y0 + fraction * (y1 - y0)) * scale;
      return centre + side * (w0 + fraction * (w1 - w0)) / 2;
    });
  }
  function edge(from, to, heights) {
    const steps = heights.length - 1, step = (to - from) / steps;
    function tangent(n) {
      return n === 0 || n === steps ? 0 : (heights[n + 1] - heights[n - 1]) / 2;
    }
    let path = '';
    for (let n = 1; n <= steps; n++) {
      const x = from + n * step;
      path += ` C ${(x - 2 * step / 3).toFixed(2)} ${(heights[n - 1] + tangent(n - 1) / 3).toFixed(2)}`
        + ` ${(x - step / 3).toFixed(2)} ${(heights[n] - tangent(n) / 3).toFixed(2)} ${x.toFixed(2)} ${heights[n].toFixed(2)}`;
    }
    return path;
  }
  function outline(turn, inner) {
    const side = inner ? 1 : -1;
    const upper = contour(topWave, top, turn, side);
    const lower = contour(bottomWave, bottom, turn, -side);
    const x1 = left + (inner ? 3.2 : 0), x2 = right - (inner ? 1 : 0);
    const radius = r - (inner ? 1 : 0);
    const topLeft = upper[0], topRight = upper[waveSteps];
    const bottomLeft = lower[0], bottomRight = lower[waveSteps];
    return `M ${start} ${topLeft}` + edge(start, end, upper)
      + ` Q ${x2} ${topRight} ${x2} ${topRight+radius} V ${bottomRight-radius} Q ${x2} ${bottomRight} ${end} ${bottomRight}`
      + edge(end, start, lower.reverse())
      + ` Q ${x1} ${bottomLeft} ${x1} ${bottomLeft-radius} V ${topLeft+radius} Q ${x1} ${topLeft} ${start} ${topLeft} Z`;
  }
  for (const id of ['wave-outline', 'wave-inner']) {
    const path = byId(id);
    const inner = id === 'wave-inner';
    path.setAttribute('d', outline(0, inner));
    path.replaceChildren();
    if (reducedMotion) continue;
    const motion = document.createElementNS('http://www.w3.org/2000/svg', 'animate');
    motion.setAttribute('attributeName', 'd');
    motion.setAttribute('dur', '14s');
    motion.setAttribute('begin', 'indefinite');
    motion.setAttribute('repeatCount', 'indefinite');
    motion.setAttribute('values', Array.from({length: 29}, (_, n) => outline(n / 28, inner)).join(';'));
    path.append(motion);
    motion.beginElement();
  }
}
const waveMotion = matchMedia('(prefers-reduced-motion: reduce)');
waveMotion.addEventListener('change', sizeWaveFrame);
new ResizeObserver(sizeWaveFrame).observe(byId('overlay-card'));

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
  if (layout === 'subtle-wave') sizeWaveFrame();
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
    "subtle-wave": ["Subtle Wave", "Twin waves", "Two shallow teal waves drift left with different crest spacing.", "Seafoam glow", "Bright crests taper to a fine line around the dark glass."],
    "minimal-glow": ["Minimal Glow", "Breathing light", "A violet halo swells and fades along the lavender rim.", "Smoked glass", "Soft purple headings and bars sit on a dark glass panel."]
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
    new ResizeObserver(() => {
      const height = Math.ceil(document.body.getBoundingClientRect().height);
      if (height && frame.height !== String(height)) frame.height = height;
    }).observe(document.body, {box:'border-box'});
  }
} else {
  void refresh();
}
