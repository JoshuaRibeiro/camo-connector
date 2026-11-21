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

  function lobbyIcon(top = 1346) {
    const left = 1436, width = 96, height = 48;
    draw(left, top, width, height, width, height);
    const { data } = context.getImageData(0, 0, width, height);
    const pooled = new Float32Array(width * height);
    // Pool once so the icon can be located across the entire search area from a single capture.
    for (let y = 0; y < height - 2; y++) {
      for (let x = 0; x < width - 2; x++) {
        let light = 0;
        for (let dy = 0; dy < 3; dy++) {
          for (let dx = 0; dx < 3; dx++) {
            const i = ((y + dy) * width + x + dx) * 4;
            light += .299 * data[i] + .587 * data[i + 1] + .114 * data[i + 2];
          }
        }
        pooled[y * width + x] = light / 9;
      }
    }
    const patch = new Float32Array(400);
    let best = .69;
    let position = null;
    for (let y = 0; y <= height - 30; y += 2) {
      for (let x = 0; x <= width - 30; x += 2) {
        for (let row = 0; row < 10; row++) {
          for (let col = 0; col < 10; col++) {
            const light = pooled[(y + Math.floor(2.9 * row)) * width + x + Math.floor(2.9 * col)];
            const i = (row * 10 + col) * 4;
            patch[i] = patch[i + 1] = patch[i + 2] = light;
          }
        }
        const score = correlation(patch, signatures.home);
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
        const ink = r >= 180 && g >= 170 && r - b >= 13;
        image.data[i] = image.data[i + 1] = image.data[i + 2] = ink ? 0 : 255;
      }
      context.putImageData(image, 0, 0);
    }
    return crop.toDataURL('image/png').split(',')[1];
  }

  function lobbyMask(rect, redFloor, greenFloor, outputWidth = rect[2] * 4, trim = false) {
    const scale = 4;
    draw(...rect, outputWidth, 45 * scale);
    const { width, height } = crop;
    const image = context.getImageData(0, 0, width, height);
    const columns = new Uint16Array(width);
    const light = new Uint8Array(width * height);
    for (let y = 0; y < height; y++) {
      for (let x = 0; x < width; x++) {
        const i = (y * width + x) * 4;
        const r = image.data[i];
        const g = image.data[i + 1];
        light[y * width + x] = Math.min(r, g);
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
    const closedUpperLoop = glyphs.map(([start, end]) => {
      const rowHasInk = (y, right) => {
        for (let x = start; x < right; x++) {
          if (image.data[(y * width + x) * 4] === 0) return true;
        }
        return false;
      };
      let top = 0;
      let bottom = height - 1;
      while (top < bottom && !rowHasInk(top, end)) top++;
      while (bottom > top && !rowHasInk(bottom, end)) bottom--;
      const span = end - start;
      const glyphHeight = bottom - top;
      const leftEdge = start + Math.ceil(span * .25);
      const first = top + Math.ceil(glyphHeight * .16);
      const last = top + Math.floor(glyphHeight * .36);
      let inkRows = 0;
      for (let y = first; y <= last; y++) if (rowHasInk(y, leftEdge)) inkRows++;
      if (last - first < 3 || inkRows < (last - first + 1) * .60) return false;

      // Both 3 and 9 have an upper-left hook in the small HUD font. Only 9
      // connects that side to the middle bar. Use the original pixels to
      // retain a faint connection that the OCR threshold may have removed.
      const middle = start + Math.ceil(span * .55);
      const hole = [];
      for (let y = first; y <= last; y += 2) {
        for (let x = start + Math.ceil(span * .28); x < middle; x += 2) {
          hole.push(light[y * width + x]);
        }
      }
      if (!hole.length) return false;
      hole.sort((a, b) => a - b);
      const threshold = Math.max(105, hole[Math.floor(hole.length / 2)] + 42);
      let connectedRows = 0;
      for (let y = top + Math.ceil(glyphHeight * .40); y <= top + Math.floor(glyphHeight * .56); y++) {
        let connected = 0;
        const left = start + Math.floor(span * .10);
        for (let x = left; x < middle; x++) {
          if (light[y * width + x] >= threshold) connected++;
        }
        if (connected >= (middle - left) * .90) connectedRows++;
      }
      return connectedRows >= Math.max(3, glyphHeight * .045);
    });
    let left = 0;
    if (trim) {
      const padding = 16;
      left = Math.max(0, glyphs[0][0] - padding);
      const right = Math.min(width, glyphs.at(-1)[1] + padding);
      // Keep room for longer totals in the source crop, then remove unused space before OCR.
      crop.width = right - left;
    }
    context.putImageData(image, -left, 0);
    return { image: crop.toDataURL('image/png').split(',')[1], glyphs: glyphs.length, closedUpperLoop };
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
      const rect = [icon.x - 81, top, 78, 45];
      const soft = lobbyMask(rect, 145, 135);
      const moderate = lobbyMask(rect, 160, 150);
      const useSoft = soft?.glyphs === moderate?.glyphs && soft?.glyphs >= 4;
      const normal = useSoft ? soft : lobbyMask(rect, 200, 195, rect[2] * 4, !lobby);
      const bright = useSoft ? moderate : lobbyMask(rect, 215, 205, rect[2] * 4, !lobby);
      const primary = maskPair(normal, bright, true);
      // Keep the usual crop for short totals. Wider totals can need room to the left of it.
      const wide = [icon.x - 97, top, 94, 45];
      const alternate = lobby
        ? maskPair(lobbyMask(wide, 190, 180), lobbyMask(wide, 200, 195), true)
        : maskPair(lobbyMask(rect, 159, 149), lobbyMask(rect, 160, 150), true);
      return { kind: 'home', lobbyLine: png([1030, top, icon.x - 1030, 45], 4), label: png([1030, top, 350, 45], 3), rawTotal: png(wide, 4), primary, alternate };
    }

    const banner = [1740, 827, 312, 44];
    if (signatures.wager && similarity(banner, signatures.wager, 64, 12) >= .65) {
      const rect = [1716, 865, 545, 220];
      const primary = maskPair(lobbyMask(rect, 125, 115, 390, true), lobbyMask(rect, 145, 135, 390, true));
      const alternate = maskPair(lobbyMask(rect, 195, 185, 390, true), lobbyMask(rect, 210, 195, 390, true));
      if (!primary && !alternate) return { kind: 'home' };
      return { kind: 'home', label: png(banner, 2), layout: 'wager', primary, alternate };
    }
    return eventsCard() ?? {
      kind: 'home',
      lobbyLine: png([1030, 1348, 448, 37], 4),
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
    const primary = maskPair(...layout.floors.map(([red, green]) => lobbyMask(rect, red, green)));
    // The highlighted tile has a moving glow behind the digits. Use a second
    // pair of masks that excludes the brighter background and trim the OCR crop.
    const alternate = highlighted
      ? maskPair(lobbyMask(rect, 195, 175, 356, true), lobbyMask(rect, 220, 200, 356, true))
      : null;
    if (!primary && !alternate) return { kind: 'home' };
    return { kind: 'home', layout: 'events', label: png(header, 3), primary, alternate };
  }

  function maskPair(normal, bright, checkStroke = false) {
    if (!normal || !bright || normal.glyphs !== bright.glyphs) return null;
    const closedUpperLoop = checkStroke
      ? normal.closedUpperLoop.map((closed, index) => closed && bright.closedUpperLoop[index])
      : undefined;
    return { digits: normal.image, mask: bright.image, glyphs: normal.glyphs, closedUpperLoop };
  }

  return sample;
}
