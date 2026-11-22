export function digitShape(isInk, left, right, top, bottom) {
  const shape = [];
  for (let row = 0; row < 12; row++) {
    const y0 = top + Math.floor(row * (bottom - top) / 12);
    const y1 = top + Math.ceil((row + 1) * (bottom - top) / 12);
    for (let col = 0; col < 8; col++) {
      const x0 = left + Math.floor(col * (right - left) / 8);
      const x1 = left + Math.ceil((col + 1) * (right - left) / 8);
      let ink = 0;
      for (let y = y0; y < y1; y++) {
        for (let x = x0; x < x1; x++) ink += Number(isInk(x, y));
      }
      shape.push(Math.round(9 * ink / ((y1 - y0) * (x1 - x0))));
    }
  }
  return shape;
}

export function classifyTotal(shapes, templates) {
  if (!templates || !shapes.length) return null;
  const digits = shapes.map(({ shape, narrow }) => {
    const scores = templates.map(examples => Math.min(...examples.map(example =>
      shape.reduce((sum, cell, i) => sum + Math.abs(cell - Number(example[i])), 0) / shape.length)));
    if (!narrow) scores[1] = Infinity;
    const ordered = scores.map((score, digit) => ({ score, digit })).sort((a, b) => a.score - b.score);
    const [best, next] = ordered;
    return best.score <= 1.7 && next.score - best.score >= .48 ? best.digit : null;
  });
  if (digits.includes(null) || (digits.length > 1 && digits[0] === 0)) return null;
  return Number(digits.join(''));
}

// Wager uses tall, pale-gold numerals on a textured panel. Keep their native
// aspect ratio and reject clipped or fragmented rows before matching shapes.
export function wagerShapes(data, width, height, floor) {
  const ink = (x, y) => {
    const i = (y * width + x) * 4;
    return data[i] >= floor && data[i + 1] >= floor - 10 && data[i] - data[i + 2] >= 14;
  };
  const shapes = [];
  let left = -1;
  for (let x = 0; x <= width; x++) {
    let count = 0;
    if (x < width) for (let y = 0; y < height; y++) count += Number(ink(x, y));
    if (count >= height * .05) {
      if (left < 0) left = x;
      continue;
    }
    if (left < 0) continue;
    let top = height, bottom = 0;
    for (let y = 0; y < height; y++) {
      for (let col = left; col < x; col++) {
        if (ink(col, y)) { top = Math.min(top, y); bottom = y + 1; break; }
      }
    }
    const h = bottom - top, aspect = (x - left) / h;
    if (h < height * .57 || h > height * .93 || aspect < .13 || aspect > .68 ||
        left === 0 || x === width || shapes.length === 5) return [];
    shapes.push({ shape: digitShape(ink, left, x, top, bottom), narrow: aspect < .38 });
    left = -1;
  }
  return shapes;
}

export function createRecognizer({ draw, context, crop, signatures }) {
  function correlation(pixels, signature) {
    let sumA = 0, sumB = 0, squaresA = 0, squaresB = 0, products = 0;
    for (let i = 0; i < signature.length; i++) {
      const index = i * 4;
      const a = .299 * pixels[index] + .587 * pixels[index + 1] + .114 * pixels[index + 2];
      const b = signature[i];
      sumA += a;
      sumB += b;
      squaresA += a * a;
      squaresB += b * b;
      products += a * b;
    }
    const n = signature.length;
    const spread = squaresA - sumA * sumA / n;
    if (spread < n * 64) return 0;
    return (products - sumA * sumB / n) / Math.sqrt(spread * (squaresB - sumB * sumB / n));
  }

  function similarity(rect, signature, width, height = width) {
    draw(...rect, width, height);
    return correlation(context.getImageData(0, 0, width, height).data, signature);
  }

  const iconWidth = 96;
  const iconHeight = 48;
  const mean = signatures.home.reduce((sum, value) => sum + value, 0) / signatures.home.length;
  const iconShape = signatures.home.map(value => value - mean);
  const iconEnergy = iconShape.reduce((sum, value) => sum + value * value, 0);
  const offsets = iconShape.map((_, i) =>
    Math.floor(2.9 * Math.floor(i / 10)) * iconWidth + Math.floor(2.9 * (i % 10)));
  const stride = iconWidth + 1;
  const sums = new Float64Array(stride * (iconHeight + 1));
  const pooled = new Float32Array(iconWidth * iconHeight);

  function lobbyIcon(top = 1346) {
    const left = 1436, width = iconWidth, height = iconHeight;
    draw(left, top, width, height, width, height);
    const { data } = context.getImageData(0, 0, width, height);
    for (let y = 0; y < height; y++) {
      let row = 0;
      for (let x = 0; x < width; x++) {
        const i = (y * width + x) * 4;
        row += .299 * data[i] + .587 * data[i + 1] + .114 * data[i + 2];
        sums[(y + 1) * stride + x + 1] = sums[y * stride + x + 1] + row;
      }
    }
    for (let y = 0; y < height - 2; y++) {
      for (let x = 0; x < width - 2; x++) {
        const i = y * stride + x;
        pooled[y * width + x] =
          (sums[i + 3 * stride + 3] - sums[i + 3 * stride] - sums[i + 3] + sums[i]) / 9;
      }
    }
    let best = .69;
    let position = null;
    for (let y = 0; y <= height - 30; y += 2) {
      for (let x = 0; x <= width - 30; x += 2) {
        let sum = 0, squares = 0, dot = 0;
        for (let i = 0; i < offsets.length; i++) {
          const light = pooled[y * width + x + offsets[i]];
          sum += light;
          squares += light * light;
          dot += light * iconShape[i];
        }
        const energy = squares - sum * sum / offsets.length;
        if (energy < offsets.length * 64) continue;
        const score = dot / Math.sqrt(energy * iconEnergy);
        if (score > best) {
          best = score;
          position = { x: left + x, y: top + y };
        }
      }
    }
    return position;
  }

  function png(rect, scale, mask = false) {
    draw(...rect, rect[2] * scale, rect[3] * scale);
    if (mask) {
      const image = context.getImageData(0, 0, crop.width, crop.height);
      for (let i = 0; i < image.data.length; i += 4) {
        const [r, g, b] = [image.data[i], image.data[i + 1], image.data[i + 2]];
        const ink = mask === 'highlight' ? r >= 245 && g >= 245 && r >= b :
          r >= 180 && g >= 170 && r - b >= 13;
        image.data[i] = image.data[i + 1] = image.data[i + 2] = ink ? 0 : 255;
      }
      context.putImageData(image, 0, 0);
    }
    return crop.toDataURL('image/png').split(',')[1];
  }

  function lobbyMask(rect, redFloor, greenFloor, outputWidth = rect[2] * 4, trim = false, matchDigits = false) {
    const scale = 4;
    draw(...rect, outputWidth, 45 * scale);
    const { width, height } = crop;
    const image = context.getImageData(0, 0, width, height);
    const columns = new Uint16Array(width);
    for (let y = 0; y < height; y++) {
      for (let x = 0; x < width; x++) {
        const i = (y * width + x) * 4;
        const r = image.data[i];
        const g = image.data[i + 1];
        // The pale gold digits occupy a fixed-height row next to the trophy icon.
        const ink = y >= 7 * scale && y < 38 * scale && r >= redFloor && g >= greenFloor;
        if (ink) columns[x]++;
        image.data[i] = image.data[i + 1] = image.data[i + 2] = ink ? 0 : 255;
      }
    }

    const glyphs = [];
    let start = -1;
    for (let x = 0; x <= width; x++) {
      if (columns[x] >= 3 * scale) {
        if (start === -1) start = x;
      } else if (start !== -1) {
        const span = x - start;
        if (span >= 3 * scale) {
          if (span > 18 * scale || start < scale || x > width - scale) return null;
          glyphs.push([start, x]);
        }
        start = -1;
      }
    }
    if (glyphs.length === 0 || glyphs.length > 5) return null;
    const shapes = [];
    if (matchDigits) {
      const isInk = (x, y) => image.data[(y * width + x) * 4] === 0;
      for (const [start, end] of glyphs) {
        const rowHasInk = y => {
          for (let x = start; x < end; x++) if (isInk(x, y)) return true;
          return false;
        };
        let top = 0;
        let bottom = height;
        while (top < bottom && !rowHasInk(top)) top++;
        while (bottom > top && !rowHasInk(bottom - 1)) bottom--;
        const glyphHeight = bottom - top;
        if (glyphHeight - 1 >= 14 * scale && glyphHeight - 1 <= 27 * scale) {
          shapes.push({
            shape: digitShape(isInk, start, end, top, bottom),
            narrow: (end - start) / glyphHeight < .55
          });
        }
      }
    }
    let left = 0;
    if (trim) {
      const padding = 16;
      left = Math.max(0, glyphs[0][0] - padding);
      const right = Math.min(width, glyphs.at(-1)[1] + padding);
      // Keep room for longer totals in the source crop, then remove unused space before OCR.
      crop.width = right - left;
    }
    const templateTotal = shapes.length === glyphs.length
      ? classifyTotal(shapes, signatures.digits) : null;
    context.putImageData(image, -left, 0);
    return { image: crop.toDataURL('image/png').split(',')[1], glyphs: glyphs.length, templateTotal };
  }

  function lobbyTemplateTotal(rect, minimumRedBlueDifference) {
    const scale = 4;
    const width = rect[2] * scale, height = rect[3] * scale;
    draw(...rect, width, height);
    const { data } = context.getImageData(0, 0, width, height);
    const read = floor => {
      const isInk = (x, y) => {
        const i = (y * width + x) * 4;
        return Math.min(data[i], data[i + 1]) >= floor &&
          data[i] - data[i + 2] >= minimumRedBlueDifference;
      };
      const columns = new Uint16Array(width);
      for (let y = 6 * scale; y < 39 * scale; y++) {
        for (let x = 0; x < width; x++) columns[x] += Number(isInk(x, y));
      }
      const spans = [];
      for (let x = 0; x < width; x++) {
        if (columns[x] < 4 * scale) continue;
        const left = x;
        while (x < width && columns[x] >= 4 * scale) x++;
        const last = spans.at(-1);
        // One faint column can split an otherwise complete glyph after thresholding.
        if (last && left - last[1] < scale) last[1] = x;
        else spans.push([left, x]);
      }
      if (!spans.length || spans.length > 5 || spans[0][0] < scale ||
          width - spans.at(-1)[1] > 14 * scale || spans.at(-1)[1] > width - scale) return null;
      const shapes = [];
      for (const [left, right] of spans) {
        let top = 6 * scale, bottom = 39 * scale;
        const rowHasInk = y => {
          for (let x = left; x < right; x++) if (isInk(x, y)) return true;
          return false;
        };
        while (top < bottom && !rowHasInk(top)) top++;
        while (bottom > top && !rowHasInk(bottom - 1)) bottom--;
        const span = right - left, glyphHeight = bottom - top;
        if (span < 2 * scale || span > 18 * scale ||
            glyphHeight < 14 * scale || glyphHeight > 27 * scale) return null;
        shapes.push({ shape: digitShape(isInk, left, right, top, bottom), narrow: span / glyphHeight < .55 });
      }
      return classifyTotal(shapes, signatures.digits);
    };
    const totals = [read(145), read(165), read(185)].filter(total => total !== null);
    return totals.length >= 2 && totals.every(total => total === totals[0]) ? totals[0] : null;
  }

  function sample() {
    const matchScore = similarity([544, 1283, 82, 79], signatures.match, 16);
    if (matchScore >= .67) {
      if (signatures.matchZero && similarity([662, 1299, 28, 42], signatures.matchZero, 14, 21) >= .92) {
        return { kind: 'match', zero: true };
      }
      const rect = [649, 1296, 75, 54];
      return { kind: 'match', digits: png(rect, 3), mask: png(rect, 3, true) };
    }

    const lobby = lobbyIcon();
    const icon = lobby ?? lobbyIcon(234);
    if (icon) {
      const top = icon.y - 8;
      const wide = [icon.x - 97, top, 94, 45];
      const rect = [icon.x - 81, top, 78, 45];
      if (lobby) {
        // In the bottom lobby HUD the full label remains legible even when the
        // digit crop blends into the scene. Require the labelled number to agree.
        const templateTotal = lobbyTemplateTotal(wide, 12) ?? lobbyTemplateTotal(wide, -10);
        if (templateTotal !== null) {
          return { kind: 'home', lobbyLine: png([1030, top, icon.x - 1030, 45], 2), templateTotal, templateNeedsLine: true };
        }
        // Exclude the surrounding scenery from the line. A separate wide
        // highlights-only crop retains faint strokes against bright backgrounds.
        return { kind: 'home',
          lobbyLine: png([1030, top + 4, icon.x - 1030, 36], 2),
          alternateLine: png([1030, top, icon.x - 1030, 45], 2),
          rawTotal: png([icon.x - 82, top + 4, 79, 36], 2), foregroundTotal: png(wide, 4, 'highlight') };
      }
      const soft = lobbyMask(rect, 145, 135);
      const moderate = lobbyMask(rect, 160, 150);
      const useSoft = soft?.glyphs === moderate?.glyphs && soft?.glyphs >= 4;
      const normal = useSoft ? soft : lobbyMask(rect, 200, 195, rect[2] * 4, true);
      const bright = useSoft ? moderate : lobbyMask(rect, 215, 205, rect[2] * 4, true);
      const primary = maskPair(normal, bright);
      const alternate = maskPair(lobbyMask(rect, 159, 149), lobbyMask(rect, 160, 150));
      return { kind: 'home', lobbyLine: png([1030, top, icon.x - 1030, 45], 2), label: png([1030, top, 350, 45], 3),
        foregroundTotal: png(wide, 4, true), rawTotal: png(wide, 4), primary, alternate };
    }

    const banner = [1740, 827, 312, 44];
    if (signatures.wager && similarity(banner, signatures.wager, 64, 12) >= .65) {
      const area = [1716, 880, 545, 185];
      draw(...area, area[2], area[3]);
      const pixels = context.getImageData(0, 0, area[2], area[3]).data;
      const match = floor => classifyTotal(wagerShapes(pixels, area[2], area[3], floor), signatures.digits);
      const templateTotal = match(170);
      if (templateTotal !== null && templateTotal === match(205)) {
        return { kind: 'home', label: png(banner, 2), layout: 'wager', templateTotal };
      }
      const rect = [1716, 865, 545, 220];
      const primary = maskPair(lobbyMask(rect, 125, 115, 390, true), lobbyMask(rect, 145, 135, 390, true));
      const alternate = maskPair(lobbyMask(rect, 195, 185, 390, true), lobbyMask(rect, 210, 195, 390, true));
      if (!primary && !alternate) return { kind: 'home' };
      return { kind: 'home', label: png(banner, 2), layout: 'wager', primary, alternate };
    }
    return eventsCard() ?? {
      kind: 'home',
      lobbyLine: png([1030, 1348, 448, 37], 2),
      foregroundTotal: png([1378, 1348, 100, 37], 4, true),
      rawTotal: png([1378, 1348, 100, 37], 4)
    };
  }

  function eventsCard() {
    const header = [190, 67, 154, 55];
    if (!signatures.eventsHeader || similarity(header, signatures.eventsHeader, 36, 12) < .80) return null;

    // Highlighting the card shifts the badge and the number seven pixels to the right.
    const layouts = [
      { x: 228, signature: signatures.eventsBright, floors: [[155, 145], [190, 175]] },
      { x: 221, signature: signatures.eventsDim, floors: [[77, 75], [95, 91]] }
    ];
    const [bright, dim] = layouts.map(layout => similarity([layout.x, 488, 32, 32], layout.signature, 12));
    if (Math.max(bright, dim) < .73) return null;
    const highlighted = bright >= dim;
    const layout = layouts[highlighted ? 0 : 1];
    const rect = [layout.x + 39, 482, 89, 45];
    const primary = maskPair(...layout.floors.map(([red, green]) => lobbyMask(rect, red, green, 356, false, true)));
    // The highlighted tile has a moving glow behind the digits. Use a second
    // pair of masks that excludes the brighter background and trim the OCR crop.
    const alternate = highlighted
      ? maskPair(lobbyMask(rect, 195, 175, 356, true), lobbyMask(rect, 220, 200, 356, true))
      : null;
    if (!primary && !alternate) return { kind: 'home' };
    return { kind: 'home', layout: 'events', label: png(header, 3), rawTotal: png(rect, 4), primary, alternate, templateTotal: primary?.templateTotal ?? null };
  }

  function maskPair(normal, bright) {
    if (!normal || !bright || normal.glyphs !== bright.glyphs) return null;
    const templateTotal = normal.templateTotal === bright.templateTotal ? normal.templateTotal : null;
    return { digits: normal.image, mask: bright.image, glyphs: normal.glyphs, templateTotal };
  }

  return sample;
}
