'use strict';

// Keep the transition by metric so a polling render can replace its DOM node.
const updateProgress = (() => {
  const metrics = new Map();
  const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
  function valueAt(metric, now) {
    const t = Math.min(1, Math.max(0, (now - metric.start) / 600));
    return metric.from + (metric.count - metric.from) * (1 - (1 - t) ** 3);
  }
  return (node, key, count, goal, ring = false) => {
    const now = performance.now();
    let metric = metrics.get(key);
    if (!metric || metric.count !== count || metric.goal !== goal) {
      const from = metric && metric.goal === goal && count > metric.count && !reducedMotion.matches
        ? valueAt(metric, now) : count;
      metric = { from, count, goal, start: now };
      metrics.set(key, metric);
    }
    cancelAnimationFrame(metric.frame);
    if (!ring) node.max = goal || 1;
    function draw(time) {
      const active = metric.from !== count && time < metric.start + 600 && !reducedMotion.matches;
      const value = active ? valueAt(metric, time) : count;
      if (ring) node.style.setProperty('--completion', (value / (goal || 1) * 100) + '%');
      else node.value = Math.min(value, goal);
      if (active) metric.frame = requestAnimationFrame(draw);
    }
    draw(now);
  };
})();
