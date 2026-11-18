(() => {
  const extension = globalThis.browser ?? globalThis.chrome;
  const storageKey = 'sunburnCursor';
  const banner = document.createElement('div');
  banner.setAttribute('role', 'status');
  Object.assign(banner.style, {
    position: 'fixed', bottom: '12px', right: '12px', zIndex: '2147483647',
    padding: '8px 12px', background: '#181a1e', color: '#fff',
    border: '1px solid #575c66', borderRadius: '6px', font: '13px system-ui',
    pointerEvents: 'none'
  });
  document.body.append(banner);
  let matchNotice = '';
  const show = message => { banner.textContent = 'Camo Connector · ' + (matchNotice ? matchNotice + ' | ' : '') + message; };
  const label = element => (element.innerText ?? element.textContent).trim().replace(/\s+/g, ' ');
  const visible = element => element.getClientRects().length > 0 &&
    getComputedStyle(element).visibility !== 'hidden';
  const buttons = root => [...root.querySelectorAll('button')].filter(visible);
  const weaponKey = value => value.toLowerCase().replace(/[^a-z0-9]/g, '')
    .replace(/^(?:crate|karit)p68$/, 'kraitp68');
  const grinding = element => /NOW\s+GRINDING/i.test(label(element));
  const weaponHeadings = () => [...document.querySelectorAll('h1,h2,h3,h4,button,span,div,strong,b,a,p')]
    .filter(element => visible(element) &&
      ![...element.children].some(child => label(child) === label(element)));
  const aliases = new Map([
    ['mosinnagan', 'mosinnagant'], ['signal5o', 'signal50'],
    ['oris', 'oris86'], ['oris8', 'oris86'], ['0ris86', 'oris86'],
    ['0ris', 'oris86'], ['oris8g', 'oris86'], ['vulcan', 'kg7vulcan']
  ]);

  function grindingCard(key) {
    const cards = new Set();
    for (const heading of weaponHeadings()) {
      if (weaponKey(label(heading)) !== key) continue;
      for (let card = heading.parentElement; card && card !== document.body; card = card.parentElement) {
        if (buttons(card).some(button => label(button) === 'Grind this')) break;
        if (grinding(card)) {
          cards.add(card);
          break;
        }
      }
    }
    return cards.size === 1 ? [...cards][0] : null;
  }

  function focusedWeapon(key) {
    return grindingCard(key) !== null;
  }

  function focusWeapon(key) {
    const controls = buttons(document);
    const targets = new Set();
    for (const title of weaponHeadings().filter(element => weaponKey(label(element)) === key)) {
      for (let card = title.parentElement; card && card !== document.body; card = card.parentElement) {
        if (grinding(card)) break;
        const grind = buttons(card).filter(button => label(button) === 'Grind this' && !button.disabled);
        const cardNames = buttons(card).filter(button => /^(all camos|prestige)$/i.test(label(button)));
        if (grind.length === 1 && (title.closest('button') || cardNames.length > 0)) {
          targets.add(grind[0]);
        }
        if (grind.length) break;
      }
    }
    if (targets.size === 1) {
      [...targets][0].click();
      return true;
    }
    // The armoury can hide the card behind the current category or search filter.
    // Only switch categories once for this weapon and leave searches untouched.
    const all = controls.filter(button => /^All weapons(?:\s+\d+)?$/i.test(label(button)));
    if (!checkedAllWeapons && all.length === 1) {
      checkedAllWeapons = true;
      all[0].click();
      return true;
    }
    return false;
  }

  function militaryControl(weapon, tier, root = document) {
    const targets = [];
    const sectionName = /^Military[\s\u00b7]*(?:Earn weapon unlocks using|Level up)\s+(?:the\s+)?/i;
    for (const button of root.querySelectorAll('button[title="Military ' + tier + '"]')) {
      if (!visible(button)) continue;
      const row = button.parentElement;
      const header = row.previousElementSibling;
      if (!header || header.children.length !== 2) continue;
      const [name, counter] = header.children;
      const text = name.textContent.trim().replace(/\s+/g, ' ');
      if (!sectionName.test(text) || weaponKey(text.replace(sectionName, '')) !== weapon) continue;
      const count = counter.textContent.trim().match(/^([0-8])\s*\/\s*8$/);
      if (!count) continue;
      targets.push({ button, count: Number(count[1]) });
    }
    if (targets.length <= 1) return targets[0] ?? null;
    // The same weapon can appear in the overlay and in the armoury list.
    const grindingTargets = targets.filter(target => {
      for (let card = target.button.parentElement; card && card !== document.body; card = card.parentElement) {
        if (targets.some(other => other !== target && card.contains(other.button))) break;
        if (grinding(card)) return true;
      }
      return false;
    });
    return grindingTargets.length === 1 ? grindingTargets[0] : null;
  }

  async function syncMilitary(session, tiers, weaponName = 'Sang 9mm') {
    if (!tiers?.length || !multiplayerSelected()) return false;
    const weapon = weaponKey(weaponName);
    const key = weapon === 'sang9mm' ? 'sangMilitaryCursor' : 'militaryCursor-' + weapon;
    const saved = (await extension.storage.local.get(key))[key];
    const state = saved?.session === session ? saved : { session, cursor: 0 };
    const remaining = tiers.slice(state.cursor);
    if (!remaining.length) return false;
    const tier = state.pendingTier ?? Math.max(...remaining);
    if (!focusedWeapon(weapon)) {
      if (Date.now() - lastFocusAttempt >= 3500) {
        lastFocusAttempt = Date.now();
        if (focusWeapon(weapon)) return true;
      }
      return false;
    }
    const card = grindingCard(weapon);
    if (!card) return false;
    const control = militaryControl(weapon, tier, card);
    const cursor = state.pendingCursor ?? (state.pending ? state.cursor : tiers.length);
    const done = { session, cursor };
    if (!control && militaryIncomplete(weapon)) return false;
    if (!control || control.count >= tier) {
      await extension.storage.local.set({ [key]: control ? done : { session, cursor: tiers.length } });
      if (!control) show(weaponName + ' | Military row already complete');
      return false;
    }
    if (state.pending) return false;
    await extension.storage.local.set({ [key]: { ...state, pending: true, pendingTier: tier, pendingCursor: cursor } });
    const current = militaryControl(weapon, tier, card);
    if (!multiplayerSelected() || !focusedWeapon(weapon) || current?.button !== control.button ||
        current.count !== control.count ||
        control.button.getAttribute('aria-label') !== 'Tick up to Military ' + tier ||
        !control.button.isConnected || control.button.disabled ||
        control.button.closest('[aria-disabled="true"],button:disabled')) {
      await extension.storage.local.set({ [key]: state });
      return true;
    }
    control.button.click();
    for (let attempt = 0; attempt < 8; attempt++) {
      await pause(150);
      if (!multiplayerSelected() || !focusedWeapon(weapon)) continue;
      const observed = militaryControl(weapon, tier, grindingCard(weapon) || card);
      if (observed?.count >= tier || (!observed && !militaryIncomplete(weapon))) {
        await extension.storage.local.set({ [key]: observed ? done : { session, cursor: tiers.length } });
        show(weaponName + ' | Military ' + tier + '/8 synced');
        return true;
      }
    }
    show(weaponName + ' Military ' + tier + ' clicked | waiting to verify');
    return false;
  }

  function militaryIncomplete(weapon) {
    const card = grindingCard(weapon);
    const count = card && militaryControl(weapon, 8, card)?.count;
    return count != null && count < 8;
  }

  function pastCompletionStage(weapon, name) {
    const card = grindingCard(weapon);
    if (!card) return false;
    const stages = ['gold', 'platinum', 'harmonicbreak', 'modemastery', 'prestige'];
    const requestedStage = stages.indexOf(camoKey(name));
    return [...document.querySelectorAll('h2,h3,h4,span,strong,b,p,div,label')].some(title => {
      if (!visible(title)) return false;
      const stage = stages.indexOf(camoKey(label(title).split(/[\u00b7\u2022:]/, 1)[0]));
      if (stage <= requestedStage) return false;
      let hasStageRow = false;
      for (let row = title; row && row !== document.body; row = row.parentElement) {
        if (buttons(row).some(button => label(button) === 'Grind this')) return false;
        if (row === card || row.contains(card)) return hasStageRow;
        if (grinding(row)) return false;
        const text = label(row);
        const meters = text.match(/\b\d+\s*\/\s*\d+\b/g);
        if (meters?.length > 1) return false;
        if (meters?.length === 1 || /\blocked\b/i.test(text)) hasStageRow = true;
      }
      return false;
    });
  }

  function grindingCompletion(weapon, name) {
    if (!multiplayerSelected() || !focusedWeapon(weapon)) return null;
    const headings = weaponHeadings().filter(title => weaponKey(label(title)) === weapon);
    const namedButtons = [...document.querySelectorAll('section[aria-label="Now grinding"]')]
      .filter(card => headings.some(heading => card.contains(heading)))
      .flatMap(card => buttons(card).filter(button => !button.disabled &&
        button.getAttribute('aria-disabled') !== 'true' &&
        button.getAttribute('aria-label')?.trim().toLowerCase() === 'mark ' + name.toLowerCase() + ' done')
        .map(button => ({ button, card })));
    if (namedButtons.length === 1) return { ...namedButtons[0], done: false };
    if (namedButtons.length > 1) return null;
    const candidates = new Map();
    const isTick = button => {
      const text = [label(button), button.title, button.getAttribute('aria-label')].filter(Boolean).join(' ');
      return !button.disabled && button.getAttribute('aria-disabled') !== 'true' &&
        !/untick|undo|locked/i.test(text) && /[\u2713\u2714]|\b(?:tick|mark (?:as )?(?:done|complete)|complete (?:camo|challenge))\b/i.test(text);
    };
    const rows = document.querySelectorAll('div,section,li,article');
    for (const row of rows) {
      if (!visible(row) || grinding(row)) continue;
      let card = row.parentElement;
      while (card && card !== document.body && !grinding(card)) card = card.parentElement;
      if (!card || card === document.body || !headings.some(title => card.contains(title))) continue;
      const identity = camoRowIdentity(row, name);
      if (!identity) continue;
      const counters = [...label(row).matchAll(/(?<![+\d])(\d+)\s*\/\s*(\d+)/g)];
      if (counters.length !== 1) continue;
      const [, count, total] = counters[0];
      const ticks = buttons(row).filter(isTick);
      const done = +total > 0 && +count === +total && ticks.length === 0;
      if (!done && (ticks.length !== 1 || +count >= +total)) continue;
      const button = ticks[0];
      const key = button || row;
      if ((candidates.get(key)?.identity || 0) < identity) {
        candidates.set(key, { identity, button, done, card });
      }
    }
    return uniqueCamoControl(candidates);
  }

  const completionFocusAttempts = new Map();

  async function syncGrindingCompletion(weapon, name, key, state, done, goal) {
    const now = Date.now();
    if (!focusedWeapon(weapon)) {
      if (now - lastFocusAttempt < 3500 || now - (completionFocusAttempts.get(weapon) || 0) < 15000)
        return false;
      lastFocusAttempt = now;
      completionFocusAttempts.set(weapon, now);
      return focusWeapon(weapon);
    }
    if (militaryIncomplete(weapon)) return false;
    const readControl = () => {
      const control = grindingCompletion(weapon, name);
      if (control || !Number.isInteger(goal) || goal < 1) return control;
      const row = findTarget(weapon, name, goal);
      if (!row || row.count < goal - 1 || row.count > goal) return null;
      return { button: row.count === goal - 1 ? row.tick || row.button : null, done: row.count === goal, card: row.card };
    };
    const control = readControl();
    if (!control && !pastCompletionStage(weapon, name)) {
      completionFocusAttempts.set(weapon, now);
      return false;
    }
    if (!control || control.done) {
      await extension.storage.local.set({ [key]: done });
      Object.assign(state, done);
      return false;
    }
    if (!control.button?.isConnected) return false;
    const interrupted = key === 'trackedProgress' || key === 'sunburnCompletion'
      ? { ...state, pendingId: done.handled.at(-1) }
      : { ...state, pending: true };
    await extension.storage.local.set({ [key]: interrupted });
    const current = readControl();
    if (militaryIncomplete(weapon) || current?.button !== control.button || !control.button.isConnected) {
      await extension.storage.local.set({ [key]: state });
      return false;
    }
    control.button.click();
    for (let attempt = 0; attempt < 8; attempt++) {
      await pause(150);
      if (!multiplayerSelected() || !focusedWeapon(weapon)) continue;
      const observed = readControl();
      if (observed?.done || (!observed && pastCompletionStage(weapon, name)) ||
          (!observed && !control.button.isConnected && control.card?.isConnected &&
          !camoRowIdentity(control.card, name))) {
        await extension.storage.local.set({ [key]: done });
        show(name + ' completed');
        return true;
      }
    }
    show(name + ' completion paused after an unverified click');
    return true;
  }
  async function syncChallenges(session, challenges, key = 'sangChallengeCursor') {
    if (!challenges?.length || !multiplayerSelected()) return false;
    const saved = (await extension.storage.local.get(key))[key];
    const state = saved?.session === session ? saved : { session, cursor: 0 };
    if (state.pending) {
      show('Paused after an unverified challenge tick · check the tracker before restarting the bridge');
      return true;
    }
    for (const entry of challenges.slice(state.cursor || 0)) {
      if (state.handled?.includes(entry)) continue;
      const fromSelection = entry.startsWith('selected-');
      if (fromSelection && !focusedWeapon('sang9mm')) continue;
      const challenge = fromSelection ? entry.slice('selected-'.length) : entry;
      const name = ({
        'menu-autumn': 'autumn', 'menu-snakeskin': 'snakeskin', 'menu-space-time': 'space time',
        'ads-25': 'autumn', 'streak-3x5': 'snakeskin'
      })[challenge];
      if (name && await syncGrindingCompletion('sang9mm', name, key, state,
        { ...state, handled: [...(state.handled || []), entry] })) return true;
    }
    return false;
  }

  function multiplayerSelected() {
    const modeButtons = ['Multiplayer', 'DMZ', 'Warzone'].map(mode =>
      buttons(document).filter(button => label(button) === mode));
    if (modeButtons.some(matches => matches.length !== 1)) return false;
    const [mp, dmz, warzone] = modeButtons.map(matches => matches[0]);
    if (['aria-current', 'aria-selected', 'aria-pressed', 'data-state'].some(attribute =>
      /^(true|page|active|on)$/.test(mp.getAttribute(attribute) || ''))) return true;
    // The active mode has the tracker's gold fill.
    const fills = [mp, dmz, warzone].map(button => getComputedStyle(button).backgroundColor);
    const rgb = fills[0].match(/^rgb\((\d+),\s*(\d+),\s*(\d+)\)$/);
    return !!rgb && +rgb[1] >= 200 && +rgb[2] >= 110 && +rgb[2] < +rgb[1] &&
      +rgb[3] <= 110 && fills[0] !== fills[1] && fills[0] !== fills[2];
  }

  function findTarget(weaponKeyName = 'signal50', camo = 'Sunburn', goal = 40) {
    if (!multiplayerSelected()) return null;
    const exact = text => [...document.querySelectorAll('h1,h2,h3,h4,span,div,p,strong,b')]
      .filter(element => visible(element) && label(element).toLowerCase() === text &&
        ![...element.children].some(child => label(child).toLowerCase() === text));
    // The focused card must contain the weapon, its grinding label and Sunburn.
    const targets = new Map();
    const titles = weaponHeadings().filter(heading => weaponKey(label(heading)) === weaponKeyName);
    for (const name of exact(camo.toLowerCase())) {
      let row = name.parentElement;
      while (row && row !== document.body && !/NOW\s+GRINDING/i.test(label(row))) {
        const text = label(row);
        const counts = [...text.matchAll(/(\d+)\s*\/\s*(\d+)\b/g)];
        if (counts.length === 1) {
          const rowButtons = buttons(row);
          const controls = rowButtons.filter(button => label(button) === '+1' && !button.disabled);
          const ticks = rowButtons.filter(button => !button.disabled && button.getAttribute('aria-disabled') !== 'true' &&
            /^[\u2713\u2714]\uFE0F?$/.test(label(button)));
          const count = Number(counts[0][1]);
          if (Number(counts[0][2]) === goal && (controls.length === 1 || count === goal)) {
            for (let card = row.parentElement; card && card !== document.body; card = card.parentElement) {
              if (!/NOW\s+GRINDING/i.test(label(card))) continue;
              if (titles.some(weapon => card.contains(weapon))) {
                targets.set(controls[0] || row, { button: controls[0], count, tick: ticks.length === 1 ? ticks[0] : null, card });
              }
              break;
            }
            break;
          }
        }
        row = row.parentElement;
      }
    }
    return targets.size === 1 ? [...targets.values()][0] : null;
  }

  const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
  const camoKey = value => (value || '').toLowerCase().replace(/[^a-z0-9]/g, '');
  function camoRowIdentity(row, name) {
    const expected = camoKey(name);
    if (!expected) return 0;
    const title = text => camoKey(text.split(/[·•:]|\s+[-–—]\s+/, 1)[0]);
    const nodes = [row, ...row.querySelectorAll('h2,h3,h4,h5,span,strong,b,p,div,label')];
    if (nodes.some(node => {
      // Details rows prefix the camo title with its category.
      const text = label(node).replace(/^Special\s*[\u00b7\u2022:]\s*/i, '');
      return camoKey(text) === expected || title(text) === expected;
    })) return 2;
    for (const image of row.querySelectorAll('img,[role="img"]')) {
      if (['alt', 'title', 'aria-label', 'data-camo-name'].some(attribute =>
        camoKey(image.getAttribute(attribute)) === expected)) return 1;
      const src = image.getAttribute('src');
      if (!src) continue;
      try {
        const filename = decodeURIComponent(new URL(src, document.baseURI).pathname.split('/').pop());
        if (camoKey(filename.replace(/\.[a-z0-9]+$/i, '')) === expected) return 1;
      } catch {}
    }
    return 0;
  }

  function uniqueCamoControl(candidates) {
    const named = [...candidates.values()].filter(candidate => candidate.identity === 2);
    if (named.length) return named.length === 1 ? named[0] : null;
    return candidates.size === 1 ? [...candidates.values()][0] : null;
  }

  function trackedControl(snapshot, doneName) {
    const key = weaponKey(snapshot.weapon);
    if (!multiplayerSelected() || !focusedWeapon(key)) return null;
    const names = weaponHeadings().filter(heading => weaponKey(label(heading)) === key);
    if (!snapshot.camo) return null;
    const candidates = new Map();
    for (const plus of buttons(document).filter(button => label(button).replace(/\s+/g, '') === '+1')) {
      for (let row = plus.parentElement; row && row !== document.body && !grinding(row); row = row.parentElement) {
        const text = label(row);
        const identity = camoRowIdentity(row, snapshot.camo);
        if (!identity) continue;
        const counts = [...text.matchAll(/(?<![+\d])(\d+)\s*\/\s*(\d+)/g)];
        if (counts.length !== 1 || Number(counts[0][2]) !== snapshot.goal) continue;
        let card = row.parentElement;
        while (card && card !== document.body && !grinding(card)) card = card.parentElement;
        if (!card || card === document.body || !names.some(name => card.contains(name))) continue;
        const controls = buttons(row);
        if (controls.length > 3 || controls.filter(button => label(button) === '+1').length !== 1) continue;
        if ((candidates.get(plus)?.identity || 0) < identity) {
          candidates.set(plus, { identity, button: plus, count: Number(counts[0][1]) });
        }
        if (identity === 2) break;
      }
    }
    if (candidates.size) return uniqueCamoControl(candidates);
    if (candidates.size || !(doneName || snapshot.confirmed && snapshot.camo) ||
        snapshot.value !== snapshot.goal) return null;
    const completedName = doneName || snapshot.camo;
    for (const row of document.querySelectorAll('div,li,article')) {
      if (!visible(row) || grinding(row)) continue;
      const text = label(row);
      const counters = [...text.matchAll(/(\d+)\s*\/\s*(\d+)/g)];
      if (!camoRowIdentity(row, completedName) || !/\bdone(?![a-z])/i.test(text) ||
          counters.length !== 1 || Number(counters[0][1]) !== Number(counters[0][2])) continue;
      let card = row.parentElement;
      while (card && card !== document.body && !grinding(card)) card = card.parentElement;
      if (card && card !== document.body && names.some(name => card.contains(name))) {
        return { count: Number(counters[0][1]), completed: true };
      }
    }
    return null;
  }

  let openedDetails = null;
  const activatedCountFields = new WeakSet();
  function weaponDetails(weapon) {
    const modals = new Map();
    for (const title of weaponHeadings().filter(node => weaponKey(label(node)) === weapon)) {
      for (let panel = title.parentElement; panel && panel !== document.body && !grinding(panel); panel = panel.parentElement) {
        if (![...panel.querySelectorAll('h2,h3,h4,div,span,p,strong')].some(node =>
          label(node).toLowerCase() === 'challenges')) continue;
        const close = buttons(panel).filter(button =>
          /^[×✕✖]$/.test(label(button)) || /^close(?: weapon details)?$/i.test(button.getAttribute('aria-label') || button.title || ''));
        if (close.length === 1) modals.set(panel, close[0]);
        break;
      }
    }
    if (modals.size !== 1) return null;
    const [panel, close] = [...modals][0];
    return { panel, close };
  }

  function openWeaponDetails(weapon) {
    const openers = new Set();
    for (const title of weaponHeadings().filter(node => weaponKey(label(node)) === weapon)) {
      const opener = title.closest('button');
      if (!opener || opener.disabled || weaponKey(label(opener)) !== weapon) continue;
      for (let card = opener.parentElement; card && card !== document.body; card = card.parentElement) {
        if (buttons(card).some(button => label(button) === 'Grind this')) break;
        if (grinding(card)) { openers.add(opener); break; }
      }
    }
    if (openers.size !== 1) return false;
    [...openers][0].click();
    openedDetails = weapon;
    return true;
  }

  function detailCounter(details, snapshot) {
    if (!snapshot.camo) return null;
    const matches = new Map();
    for (const minus of buttons(details.panel).filter(button => /^[-−–]$/.test(label(button)))) {
      for (let row = minus.parentElement; row && row !== details.panel; row = row.parentElement) {
        const identity = camoRowIdentity(row, snapshot.camo);
        if (!identity) continue;
        const controls = buttons(row);
        if (controls.filter(button => /^[-−–]$/.test(label(button))).length !== 1) break;
        const plus = controls.filter(button => label(button) === '+');
        if (plus.length !== 1) continue;
        const inputs = [...row.querySelectorAll('input')].filter(input =>
          ['number', 'text'].includes(input.type) && /^\d+$/.test(input.value));
        const numbers = [...row.querySelectorAll('span,div,button,output')].filter(node =>
          /^\d+$/.test(label(node)) && ![...node.children].some(child => label(child) === label(node)));
        const actionable = numbers.filter(node => node.tagName === 'BUTTON' || node.tagName === 'OUTPUT');
        const numberText = actionable.length ? actionable : numbers;
        const meter = inputs.length === 1 ? inputs[0].value :
          inputs.length === 0 && numberText.length === 1 ? label(numberText[0]) : null;
        if (meter === null) continue;
        const maximum = Number(inputs[0]?.getAttribute('max')) || null;
        const checkboxes = [...row.querySelectorAll('input[type="checkbox"],[role="checkbox"]')].filter(visible);
        const completed = (maximum !== null && Number(meter) >= maximum) ||
          checkboxes.some(box =>
            box.checked || box.getAttribute('aria-checked') === 'true');
        const input = inputs.length === 1 && !inputs[0].disabled && !inputs[0].readOnly ? inputs[0] : null;
        const editor = !input && numberText.length === 1 && numberText[0].tagName === 'BUTTON' &&
          !numberText[0].disabled ? numberText[0] : null;
        const prior = matches.get(minus);
        if (!prior || identity > prior.identity) {
          matches.set(minus, { identity, input, editor, button: plus[0], count: Number(meter), completed, maximum,
            checkbox: checkboxes.length === 1 && checkboxes[0].getAttribute('aria-disabled') !== 'true'
              ? checkboxes[0] : null });
        }
        if (identity === 2) break;
      }
    }
    return uniqueCamoControl(matches);
  }

  function isSunburn(row) {
    return weaponKey(row.weapon) === 'signal50' && camoKey(row.camo) === 'sunburn';
  }

  async function reconcileDetails(snapshot, storage, state, done) {
    const weapon = weaponKey(snapshot.weapon);
    const details = weaponDetails(weapon);
    if (!details) {
      if (focusedWeapon(weapon)) openWeaponDetails(weapon);
      show('Open ' + snapshot.weapon + ' details to sync ' + snapshot.value + '/' + snapshot.goal);
      return true;
    }
    const target = detailCounter(details, snapshot);
    if (!target) {
      show(snapshot.weapon + ' · no unique tracker details row for ' + snapshot.camo);
      return true;
    }
    if (target.count >= snapshot.value || target.completed) {
      await extension.storage.local.set({ [storage]: done });
      if (openedDetails === weapon) {
        details.close.click();
        openedDetails = null;
      }
      show(target.count > snapshot.value ? 'Higher tracker count preserved' :
        target.completed && target.count !== snapshot.value ? 'Completed camo preserved' :
        snapshot.weapon + ' · synced ' + snapshot.value + '/' + snapshot.goal);
      return true;
    }
    if (target.maximum !== null && snapshot.value > target.maximum) {
      show(snapshot.camo + ' · game count exceeds the tracker field limit');
      return true;
    }
    if (!target.input) {
      if (target.editor?.isConnected && !activatedCountFields.has(target.editor)) {
        activatedCountFields.add(target.editor);
        target.editor.click();
        return show('Opening count field for ' + snapshot.camo);
      }
      return show('No editable count field for ' + snapshot.camo);
    }
    const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
    if (!setValue) return show('Could not access the editable count field');
    await extension.storage.local.set({ [storage]: { ...state, pendingId: snapshot.id } });
    const currentDetails = weaponDetails(weapon);
    const current = currentDetails && detailCounter(currentDetails, snapshot);
    if (!multiplayerSelected() || !current || current.completed ||
        current.count !== target.count || current.maximum !== target.maximum || current.input !== target.input ||
        !current.input.isConnected || current.input.disabled || current.input.readOnly) {
      await extension.storage.local.set({ [storage]: state });
      return true;
    }
    current.input.focus({ preventScroll: true });
    setValue.call(current.input, String(snapshot.value));
    current.input.dispatchEvent(new Event('input', { bubbles: true }));
    current.input.dispatchEvent(new Event('change', { bubbles: true }));
    current.input.blur();
    for (let attempt = 0; attempt < 8; attempt++) {
      await pause(150);
      const summary = snapshot.weapon === 'Signal 50' && snapshot.camo === 'Sunburn' ?
        findTarget() : trackedControl(snapshot, snapshot.camo);
      if (summary?.count === snapshot.value) {
        await extension.storage.local.set({ [storage]: done });
        if (openedDetails === weapon) {
          weaponDetails(weapon)?.close.click();
          openedDetails = null;
        }
        show(snapshot.weapon + ' · synced ' + snapshot.value + '/' + snapshot.goal);
        return true;
      }
    }
    const committedDetails = weaponDetails(weapon);
    if (committedDetails && detailCounter(committedDetails, snapshot)?.count === snapshot.value) {
      // Reopen the dialog to read the tracker state from a newly mounted field.
      committedDetails.close.click();
      await pause(100);
      if (!weaponDetails(weapon) && openWeaponDetails(weapon)) {
        await pause(150);
        const reopened = weaponDetails(weapon);
        if (reopened && detailCounter(reopened, snapshot)?.count === snapshot.value) {
          await extension.storage.local.set({ [storage]: done });
          reopened.close.click();
          openedDetails = null;
          show(snapshot.weapon + ' · synced ' + snapshot.value + '/' + snapshot.goal);
          return true;
        }
      }
    }
    show('Tracked camo update paused · could not verify the exact count');
    return true;
  }
  async function syncTrackedRows(session, rows, sunburnOnly = false) {
    if (!rows?.length || !multiplayerSelected()) return false;
    const key = sunburnOnly ? 'sunburnCompletion' : 'trackedProgress';
    const saved = (await extension.storage.local.get(key))[key];
    let state = saved?.session === session ? saved : { session, handled: [] };
    if (state.pending || state.pendingId != null) {
      const { pending, pendingId, ...clean } = state;
      // An interrupted write with a known event ID is never repeated.
      state = pendingId == null ? clean : { ...clean, handled: [...clean.handled, pendingId] };
      await extension.storage.local.set({ [key]: state });
    }
    const pending = rows.filter(row => row.type === 'camo' && row.camo &&
      (sunburnOnly ? isSunburn(row) && row.confirmed === true && row.value === 40 && row.goal === 40 : !isSunburn(row)) &&
      !state.handled.includes(row.id));
    const handled = row => ({ ...state, handled: [...state.handled, row.id],
      akimboThrough: Math.max(state.akimboThrough || 0, row.akimboThrough || 0) });
    const completions = pending.filter(row => row.confirmed === true);
    completions.sort((a, b) => Number(focusedWeapon(weaponKey(b.weapon))) - Number(focusedWeapon(weaponKey(a.weapon))));
    for (const row of completions) {
      if (await syncGrindingCompletion(weaponKey(row.weapon), row.camo, key, state, handled(row), row.goal)) return true;
    }
    const counters = pending.filter(row => row.confirmed !== true);
    const snapshot = counters.find(row => latestWeapon && weaponKey(row.weapon) === weaponKey(latestWeapon)) || counters[0];
    if (!snapshot) return false;
    const name = weaponKey(snapshot.weapon);
    const done = handled(snapshot);
    if (weaponDetails(name)) return reconcileDetails(snapshot, key, state, done);
    if (!focusedWeapon(name)) {
      if (Date.now() - lastFocusAttempt >= 3500) {
        lastFocusAttempt = Date.now();
        focusWeapon(name);
      }
      show('Tracked progress · locating ' + snapshot.weapon);
      return true;
    }
    if (militaryIncomplete(name)) return false;
    const readTarget = () => findTarget(name, snapshot.camo, snapshot.goal) ?? trackedControl(snapshot);
    const target = readTarget();
    if (target && (target.completed || target.count >= snapshot.value)) {
      await extension.storage.local.set({ [key]: done });
      show(snapshot.weapon + ' · ' + snapshot.camo + ' progress verified');
      return true;
    }
    if (target?.button?.isConnected && !target.button.disabled &&
        Number.isInteger(snapshot.value) && Number.isInteger(snapshot.goal) &&
        target.count < snapshot.value && snapshot.value <= snapshot.goal) {
      await extension.storage.local.set({ [key]: { ...state, pendingId: snapshot.id } });
      const current = readTarget();
      if (!multiplayerSelected() || !focusedWeapon(name) || current?.button !== target.button ||
          current.count !== target.count || !target.button.isConnected || target.button.disabled) {
        await extension.storage.local.set({ [key]: state });
        return true;
      }
      current.button.click();
      for (let attempt = 0; attempt < 8; attempt++) {
        await pause(150);
        if (!multiplayerSelected() || !focusedWeapon(name)) break;
        const observed = readTarget();
        if (observed && observed.count >= current.count + 1) {
          await extension.storage.local.set({ [key]: observed.count >= snapshot.value ? done : state });
          show(snapshot.camo + ' · ' + observed.count + '/' + snapshot.goal);
          return true;
        }
        if (!observed && snapshot.value === snapshot.goal && pastCompletionStage(name, snapshot.camo)) {
          await extension.storage.local.set({ [key]: done });
          return true;
        }
      }
      show(snapshot.camo + ' · could not verify the automatic +1');
      return true;
    }
    return reconcileDetails(snapshot, key, state, done);
  }
  async function syncAkimbo(session, count, timing, receivedTimes) {
    if (!count || !multiplayerSelected()) return false;
    const key = 'akimboCursor';
    const saved = await extension.storage.local.get([key, 'trackedProgress']);
    const state = saved[key]?.session === session ? saved[key] : { session, cursor: 0 };
    const tracked = saved.trackedProgress;
    const baseline = Math.max(state.cursor, tracked?.session === session ? tracked.akimboThrough || 0 : 0);
    if (baseline >= count) return false;
    if (state.pending) {
      show('Space Time sync paused after an unverified edit');
      return true;
    }
    const weapon = 'sang9mm';
    if (!focusedWeapon(weapon)) {
      if (Date.now() - lastFocusAttempt >= 3500) {
        lastFocusAttempt = Date.now();
        focusWeapon(weapon);
      }
      show('Space Time progress waiting for the Sang 9mm card');
      return true;
    }
    const snapshot = {
      weapon: 'Sang 9mm', camo: 'Space Time', objective: 'Kills with akimbo',
      goal: 50, value: 50
    };
    const readTarget = () => {
      if (!multiplayerSelected() || !focusedWeapon(weapon)) return null;
      return trackedControl(snapshot, snapshot.camo);
    };
    const target = readTarget();
    if (!target) return false;
    if (target.completed || target.count === snapshot.goal) {
      await extension.storage.local.set({ [key]: { session, cursor: count } });
      if (openedDetails === weapon) {
        weaponDetails(weapon)?.close.click();
        openedDetails = null;
      }
      show('Space Time already complete · 50/50');
      return true;
    }
    if (!target.button?.isConnected || target.button.disabled) {
      show('Space Time: ' + (count - baseline) + ' queued · +1 is unavailable');
      return true;
    }
    await extension.storage.local.set({ [key]: { ...state, pending: true } });
    const current = readTarget();
    if (current?.button !== target.button || current?.count !== target.count ||
        current.completed || !target.button.isConnected || target.button.disabled) {
      await extension.storage.local.set({ [key]: state });
      return false;
    }
    current.button.click();
    for (let attempt = 0; attempt < 8; attempt++) {
      const updated = readTarget();
      if (updated?.count === target.count + 1) {
        await extension.storage.local.set({ [key]: { session, cursor: baseline + 1 } });
        if (openedDetails === weapon && baseline + 1 >= count) {
          weaponDetails(weapon)?.close.click();
          openedDetails = null;
        }
        if (timing?.[baseline + 1]) {
          console.info('[Camo Connector] Space Time +1 timing', {
            ...timing[baseline + 1], receivedAt: receivedTimes.get(String(baseline + 1)),
            appliedAt: new Date().toISOString(), count: updated.count
          });
        }
        show('Space Time +1 · ' + updated.count + '/50');
        return true;
      }
      await pause(75);
    }
    show('Space Time sync paused after an unverified click');
    return true;
  }
  async function syncGold(session, names) {
    if (!names?.length || !multiplayerSelected()) return false;
    const key = 'goldProgress';
    const saved = (await extension.storage.local.get(key))[key];
    const state = saved?.session === session ? saved : { session, handled: [] };
    if (state.pending) return false;
    const pending = names.filter(name => !state.handled.includes(name));
    pending.sort((a, b) => Number(focusedWeapon(weaponKey(b))) - Number(focusedWeapon(weaponKey(a))));
    for (const name of pending) {
      if (await syncGrindingCompletion(weaponKey(name), 'Gold', key, state,
        { ...state, handled: [...state.handled, name] })) return true;
    }
    return false;
  }
  let busy = false;
  let timingSession = null;
  const firstReceived = new Map();
  let latestWeapon = null;
  let needsWeaponFocus = false;
  let lastWeaponFocusAttempt = 0;
  let checkedAllWeapons = false;
  let lastFocusAttempt = 0;
  let lastFocusReport = 0;
  function reportTrackerFocus() {
    if (Date.now() - lastFocusReport < 2500 || !multiplayerSelected()) return;
    lastFocusReport = Date.now();
    try {
      const focus = JSON.parse(localStorage.getItem('mw4-camo-progress'))?.focus;
      if (typeof focus !== 'string' || !focusedWeapon(weaponKey(focus))) return;
      const heading = weaponHeadings().find(node => weaponKey(label(node)) === weaponKey(focus));
      void extension.runtime.sendMessage({ type: 'mw4-focus', weapon: heading ? label(heading) : focus }).catch(() => {
  const extension = globalThis.browser ?? globalThis.chrome;});
    } catch {}
  }

  async function withSyncLock(work) {
    if (typeof exportFunction !== 'function') {
      return navigator.locks.request('mw4-sunburn-sync', { ifAvailable: true },
        lock => lock ? work() : undefined);
    }
    // Firefox cannot await a content-script promise returned to Web Locks.
    const port = extension.runtime.connect({ name: 'mw4-sync-lock' });
    try {
      const acquired = await new Promise(resolve => {
        port.onMessage.addListener(message => resolve(message?.acquired === true));
        port.onDisconnect.addListener(() => resolve(false));
      });
      if (acquired) await work();
    } finally { port.disconnect(); }
  }

  async function poll() {
    if (busy) return;
    busy = true;
    try {
      const result = await extension.runtime.sendMessage('sunburn-events');
      if (!result?.events) return show(result?.error || 'Waiting for local bridge');
      reportTrackerFocus();
      const receivedAt = new Date().toISOString();
      const { session, count, akimboCount, akimboTiming, weapon, military, challenges, trackedRows, catalogMilitary,
        goldUnlocks, matchState } = result.events;
      matchNotice = matchState?.kind === 'match-voting' ? 'Map voting: match counters reset' :
        matchState?.kind === 'match-confirmed' ? 'Next: ' + (matchState.matchMode || 'Match about to begin (mode unreadable)') +
          (/^(?:HARDCORE|HC)\b/i.test(matchState.matchMode) ? ' (check your selected weapon)' : '') : '';
      if (session !== timingSession) {
        timingSession = session;
        firstReceived.clear();
      }
      for (const number of Object.keys(akimboTiming || {})) {
        if (!firstReceived.has(number)) firstReceived.set(number, receivedAt);
      }
      await withSyncLock(async () => {
        const saved = (await extension.storage.local.get(storageKey))[storageKey];
        let state = saved?.session === session ? saved : { session, cursor: 0 };
        if (weapon !== latestWeapon) {
          latestWeapon = weapon;
          needsWeaponFocus = Boolean(weapon);
          lastWeaponFocusAttempt = 0;
          checkedAllWeapons = false;
          lastFocusAttempt = 0;
        }
        if (needsWeaponFocus && multiplayerSelected() && !openedDetails) {
          const normalized = weaponKey(weapon);
          const key = aliases.get(normalized) || normalized;
          if (focusedWeapon(key)) {
            needsWeaponFocus = false;
          } else if (Date.now() - lastWeaponFocusAttempt >= 3500) {
            lastWeaponFocusAttempt = Date.now();
            if (focusWeapon(key)) return show('Switching focus to ' + weapon);
          }
        }
        const detected = weapon ? weaponKey(weapon) : null;
        const activeWeapon = aliases.get(detected) || detected;
        const canSync = name => {
          const key = weaponKey(name);
          return (!activeWeapon || key === activeWeapon) && focusedWeapon(key);
        };
        const activeRows = trackedRows?.filter(row => canSync(row.weapon));
        if (canSync('Sang 9mm') && await syncMilitary(session, military)) return;
        for (const [name, tiers] of catalogMilitary || []) {
          if (canSync(name) && await syncMilitary(session, tiers, name)) return;
        }
        if (await syncTrackedRows(session, activeRows, true)) return;
        const target = findTarget();
        const signalIsCurrent = canSync('Signal 50');
        if (!state.pending && signalIsCurrent && state.cursor < count && (target?.count === 40 ||
            (!target && multiplayerSelected() && focusedWeapon('signal50')))) {
          state = { ...state, cursor: count };
          await extension.storage.local.set({ [storageKey]: state });
        }
        const sunburnPending = signalIsCurrent && !state.pending && state.cursor < count;

        if (!sunburnPending && await syncTrackedRows(session, activeRows)) return;

        if (!sunburnPending && canSync('Sang 9mm') && challenges?.includes('menu-space-time') &&
            await syncChallenges(session, ['menu-space-time'], 'spaceTimeCompletion')) return;
        if (!sunburnPending && canSync('Sang 9mm') &&
            await syncAkimbo(session, akimboCount, akimboTiming, firstReceived)) return;
        if (!sunburnPending && canSync('Sang 9mm') && await syncChallenges(session, challenges)) return;
        if (!sunburnPending && await syncGold(session, goldUnlocks?.filter(canSync))) return;
        if (signalIsCurrent && state.pending && (weapon || target))
          return show('Sunburn sync paused after an unverified +1. Check the tracker count.');
        if (!target) return show(weapon ? weapon + ' · focus synced' : 'Ready · waiting for camo detections');
        if (!sunburnPending) return show(weapon ? weapon + ' · focus synced' : 'Ready · waiting for camo detections');
        if (!target.button) return;

        // Persist before clicking so a reload cannot replay an ambiguous write.
        await extension.storage.local.set({
          [storageKey]: { ...state, pending: true }
        });
        const current = findTarget();
        if (current?.button !== target.button || current.count !== target.count) {
          await extension.storage.local.set({ [storageKey]: state });
          return show('Tracker changed. Checking again.');
        }
        current.button.click();
        for (let attempt = 0; attempt < 8; attempt++) {
          await pause(150);
          const observed = findTarget();
          if (observed?.count === target.count + 1 ||
              (!observed && target.count === 39 && multiplayerSelected() && focusedWeapon('signal50'))) {
            const next = { ...state, cursor: state.cursor + 1 };
            await extension.storage.local.set({ [storageKey]: next });
            return show('Applied +1 · ' + (target.count + 1) + '/40');
          }
        }
        show('Paused after an unverified click. Check the tracker count before restarting the bridge.');
      });
    } catch (error) {
      show(error.message || 'Connection interrupted');
    } finally {
      busy = false;
    }
  }
  show('Connecting');
  poll();
  setInterval(poll, 500);
})();


