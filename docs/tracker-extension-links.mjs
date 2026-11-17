const browsers = [
  { name: 'Chrome', host: 'chromewebstore.google.com', pattern: /Chrome\//i },
  { name: 'Firefox', host: 'addons.mozilla.org', pattern: /Firefox\//i },
  { name: 'Edge', host: 'microsoftedge.microsoft.com', pattern: /Edg\//i },
  { name: 'Opera', host: 'addons.opera.com', pattern: /(?:OPR|Opera)\//i }
];

export function mountCamoConnectorInstallLinks(container, listings, userAgent = navigator.userAgent) {
  // Chromium-based Opera and Edge also include Chrome in their user agent.
  const current = browsers.find(browser => browser.name !== 'Chrome' && browser.pattern.test(userAgent)) ||
    browsers.find(browser => browser.pattern.test(userAgent));
  const ordered = current ? [current, ...browsers.filter(browser => browser !== current)] : browsers;
  const links = document.createDocumentFragment();
  for (const browser of ordered) {
    let url;
    try { url = new URL(listings[browser.name]); } catch { continue; }
    if (url.protocol !== 'https:' || url.hostname !== browser.host || url.port || url.username || url.password) continue;
    const link = document.createElement('a');
    link.href = url.href;
    link.textContent = 'Install for ' + browser.name;
    link.className = 'btn';
    links.append(link);
  }
  container.replaceChildren(links);
  container.hidden = !container.childElementCount;
}
