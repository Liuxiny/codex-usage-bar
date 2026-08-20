(() => {
  const VERSION = '0.4.16';
  const ROOT_ID = 'codex-usage-native';
  const POPOVER_ID = 'codex-usage-native-popover';
  const STYLE_ID = 'codex-usage-native-style';
  const HELP_ID = 'application-menu-trigger-help-menu';
  const NATIVE_MENU_CLASS = 'no-drag bg-codex-application-menu text-default shadow-application-menu z-50 flex select-none flex-col overflow-y-auto rounded-sm py-1';
  const NATIVE_MENU_ITEM_CLASS = 'text-default outline-hidden rounded-sm mx-1 px-4 py-[var(--padding-row-y)] text-xs leading-normal font-normal';
  const NATIVE_MENU_SEPARATOR_CLASS = 'my-2 h-[0.5px] shrink-0 bg-border-strong';

  if (window.__codexUsageBar && typeof window.__codexUsageBar.destroy === 'function') {
    window.__codexUsageBar.destroy();
  }

  const CSS = `
#${ROOT_ID} {
  display: none;
  align-items: center;
  gap: 6px;
  height: 28px;
  min-width: 0;
  box-sizing: border-box;
  overflow: hidden;
  white-space: nowrap;
  user-select: none;
  -webkit-user-select: none;
}
#${ROOT_ID} .cub-item {
  display: inline-flex;
  position: relative;
  align-items: center;
  gap: 5px;
  min-width: 0;
  height: 22px;
  white-space: nowrap;
  pointer-events: none;
}
#${ROOT_ID} .cub-trigger-copy {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  max-width: 320px;
  overflow: hidden;
  opacity: 1;
  transform: translateX(0);
  transform-origin: left center;
}
#${ROOT_ID} .cub-meter {
  position: relative;
  width: 14px;
  height: 14px;
  flex: 0 0 auto;
  overflow: visible;
  contain: layout paint;
}
#${ROOT_ID} .cub-ring {
  position: absolute;
  inset: 0;
  width: 14px;
  height: 14px;
  opacity: 1;
  transform: translate3d(0, 0, 0);
  will-change: opacity;
  transition: opacity 80ms linear;
}
#${ROOT_ID} .cub-ring-track {
  fill: none;
  outline: 0;
  stroke: var(--cub-track);
  stroke-width: 3;
}
#${ROOT_ID} .cub-ring-progress {
  fill: none;
  outline: 0;
  stroke: var(--cub-accent);
  stroke-width: 3;
  stroke-linecap: round;
  stroke-dasharray: 100;
  transition: stroke-dashoffset 220ms cubic-bezier(.22,.75,.2,1);
}
#${ROOT_ID} .cub-bar {
  position: absolute;
  left: 0;
  top: 50%;
  width: 100%;
  height: 5px;
  border-radius: 999px;
  overflow: hidden;
  opacity: 0;
  border: 0;
  outline: 0;
  box-shadow: none;
  background: var(--cub-track);
  transform: translate3d(0, -50%, 0) scaleX(0);
  transform-origin: left center;
  will-change: opacity, transform;
  backface-visibility: hidden;
  transition: opacity 80ms linear, transform 140ms cubic-bezier(.22,.75,.2,1);
}
#${ROOT_ID} .cub-bar-fill {
  display: block;
  height: 100%;
  min-width: 0;
  width: 100%;
  border-radius: inherit;
  border: 0;
  outline: 0;
  box-shadow: none;
  background: var(--cub-accent);
  transform: scaleX(0);
  transform-origin: left center;
  will-change: transform;
  transition: transform 220ms cubic-bezier(.22,.75,.2,1);
}
#${ROOT_ID}[data-open="true"] .cub-item {
  flex: 1 1 0;
  width: 0;
  gap: 0;
}
#${ROOT_ID}[data-open="true"] .cub-meter {
  position: absolute;
  left: 0;
  right: 0;
  width: auto;
}
#${ROOT_ID}[data-open="true"] .cub-trigger-copy {
  opacity: 0;
}
#${ROOT_ID}[data-open="true"] .cub-ring { opacity: 0; }
#${ROOT_ID}[data-open="true"] .cub-bar { opacity: 1; transform: translate3d(0, -50%, 0) scaleX(1); }
#${ROOT_ID} .cub-percent,
#${ROOT_ID} .cub-reset,
#${ROOT_ID} .cub-separator { font-variant-numeric: tabular-nums; }
#${ROOT_ID} .cub-percent { color: var(--cub-accent); font-weight: 650; }
#${ROOT_ID} .cub-separator { opacity: .42; }
#${ROOT_ID} .cub-reset { opacity: .82; }

#${POPOVER_ID} {
  position: fixed;
  z-index: 2147483000;
  display: block;
  width: max-content;
  min-width: max(164px, var(--cub-trigger-min-width, 0px));
  max-width: min(420px, calc(100vw - 16px));
  box-sizing: border-box;
  padding: 8px 10px;
  border: var(--cub-menu-border, 0px solid transparent);
  border-radius: var(--cub-menu-radius, 7.5px);
  background: var(--cub-menu-bg, var(--cub-surface)) !important;
  color: var(--cub-menu-fg, var(--cub-foreground));
  box-shadow: var(--cub-menu-shadow, rgba(0,0,0,.42) 0 4px 12px 0);
  font-family: var(--cub-menu-font, inherit);
  font-size: var(--cub-menu-font-size, 13px);
  line-height: var(--cub-menu-line-height, 19.5px);
  letter-spacing: var(--cub-menu-letter-spacing, normal);
  white-space: nowrap;
  user-select: none;
  -webkit-user-select: none;
  opacity: 0;
  transform: translateY(-4px);
  transform-origin: top left;
  pointer-events: none;
  will-change: opacity, transform;
  transition: opacity 120ms ease-out, transform 150ms cubic-bezier(.2,.8,.2,1);
}
#${POPOVER_ID}[data-open="true"] {
  opacity: 1;
  transform: translateY(0);
  pointer-events: auto;
}
#${POPOVER_ID} .cub-detail-window + .cub-detail-window {
  margin-top: 10px;
  padding-top: 10px;
  border-top: var(--cub-menu-separator-width, 0.5px) solid var(--cub-menu-separator, var(--cub-border));
}
#${POPOVER_ID} .cub-detail-quota {
  display: flex;
  align-items: baseline;
  justify-content: flex-start;
  gap: 12px;
  width: 100%;
  margin-top: 0;
  font-weight: 600;
  color: var(--cub-menu-fg, var(--cub-foreground));
}
#${POPOVER_ID} .cub-detail-percent {
  color: var(--cub-accent);
  font-weight: 650;
}
#${POPOVER_ID} .cub-detail-reset,
#${POPOVER_ID} .cub-token-line {
  margin-top: 5px;
  color: var(--cub-menu-fg, var(--cub-foreground));
  opacity: 1;
  font-weight: 400;
  font-variant-numeric: tabular-nums;
}
#${POPOVER_ID} .cub-token-line {
  margin-top: 10px;
  padding-top: 9px;
  border-top: var(--cub-menu-separator-width, 0.5px) solid var(--cub-menu-separator, var(--cub-border));
  opacity: 1;
}
#${POPOVER_ID} .cub-token-value {
  color: var(--cub-accent);
  font-weight: 650;
}
#${POPOVER_ID} .cub-token-unit {
  color: var(--cub-menu-fg, var(--cub-foreground));
  font-weight: 650;
}
@media (prefers-reduced-motion: reduce) {
  #${ROOT_ID} *, #${ROOT_ID} *::before, #${ROOT_ID} *::after,
  #${POPOVER_ID}, #${POPOVER_ID} *, #${POPOVER_ID} *::before, #${POPOVER_ID} *::after {
    transition-duration: 1ms !important;
    animation-duration: 1ms !important;
  }
}
`;

  let lastState = { windows: [], tokens: null, i18n: { catalogs: {} } };
  let activeLocale = 'en';
  let root = null;
  let popover = null;
  let style = null;
  let themeObserver = null;
  let domObserver = null;
  let mountScheduled = false;
  let themeCheckTimer = null;
  let appearanceFallbackTimer = null;
  let themeSignatureCache = null;
  let isOpen = false;
  let closedTriggerWidth = 0;
  let themeSnapshot = null;
  let appliedTheme = null;
  let themeCaptureCount = 0;
  let menubarElement = null;
  let hoveredMenubarItem = null;
  let menuBridgeFrame = 0;



  function normalizeLocaleCode(value) {
    return String(value || '').trim().replace(/_/g, '-').toLowerCase();
  }

  function localeCatalogs() {
    const catalogs = lastState?.i18n?.catalogs;
    return catalogs && typeof catalogs === 'object' ? catalogs : {};
  }

  function resolveLocale() {
    const raw = normalizeLocaleCode(document.documentElement?.lang || '');
    const catalogs = localeCatalogs();
    if (raw === 'zh' || raw.startsWith('zh-')) return 'zh';
    if (raw && catalogs[raw]) return raw;
    const primary = raw.split('-')[0];
    if (primary && catalogs[primary]) return primary;
    return 'en';
  }

  function getMessageByPath(catalog, key) {
    if (!catalog || typeof catalog !== 'object') return undefined;
    let value = catalog;
    for (const part of String(key).split('.')) {
      if (!value || typeof value !== 'object' || !(part in value)) return undefined;
      value = value[part];
    }
    return typeof value === 'string' ? value : undefined;
  }

  function t(key, fallback = '') {
    const catalogs = localeCatalogs();
    const locale = activeLocale || resolveLocale();
    return getMessageByPath(catalogs[locale], key)
      ?? getMessageByPath(catalogs.en, key)
      ?? fallback;
  }

  function fillTemplate(template, values) {
    return String(template || '').replace(/\{([a-zA-Z0-9_]+)\}/g, (_, key) =>
      Object.prototype.hasOwnProperty.call(values, key) ? String(values[key]) : `{${key}}`
    );
  }

  function resetPoint(value) {
    if (value === null || value === undefined) return null;
    if (typeof value === 'number' && Number.isFinite(value)) {
      const date = new Date(value * 1000);
      return Number.isNaN(date.getTime()) ? null : { date, dateOnly: false };
    }
    const text = String(value);
    const m = text.match(/^(\d{4})-(\d{2})-(\d{2})$/);
    if (m) {
      const date = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]), 0, 0, 0, 0);
      return Number.isNaN(date.getTime()) ? null : { date, dateOnly: true };
    }
    const date = new Date(text);
    return Number.isNaN(date.getTime()) ? null : { date, dateOnly: false };
  }

  function sameLocalDay(a, b) {
    return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
  }

  function tomorrowOf(now) {
    const d = new Date(now);
    d.setDate(d.getDate() + 1);
    return d;
  }

  function hhmm(date) {
    return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`;
  }

  function formatDatePrefix(date, now) {
    const values = { year: date.getFullYear(), month: date.getMonth() + 1, day: date.getDate() };
    const key = date.getFullYear() === now.getFullYear() ? 'date.monthDay' : 'date.yearMonthDay';
    const fallback = key === 'date.monthDay' ? '{month}/{day}' : '{year}/{month}/{day}';
    return fillTemplate(t(key, fallback), values);
  }

  function formatResetCompact(value, fallback = '', now = new Date()) {
    const point = resetPoint(value);
    if (!point) return fallback || '';
    const d = point.date;
    if (sameLocalDay(d, now)) return point.dateOnly ? '' : hhmm(d);
    if (sameLocalDay(d, tomorrowOf(now))) {
      const tomorrow = t('date.tomorrow', 'Tomorrow');
      return point.dateOnly ? tomorrow : `${tomorrow} ${hhmm(d)}`;
    }
    const prefix = formatDatePrefix(d, now);
    return point.dateOnly ? prefix : `${prefix} ${hhmm(d)}`;
  }

  function formatResetFull(value, fallback = '', now = new Date()) {
    const point = resetPoint(value);
    if (!point) return t('usage.resetUnavailable', fallback || 'Reset time unavailable');
    const d = point.date;
    let prefix;
    if (sameLocalDay(d, now)) prefix = t('date.today', 'Today');
    else if (sameLocalDay(d, tomorrowOf(now))) prefix = t('date.tomorrow', 'Tomorrow');
    else prefix = formatDatePrefix(d, now);
    const reset = t('date.reset', 'reset');
    return point.dateOnly ? `${prefix} ${reset}` : `${prefix} ${hhmm(d)} ${reset}`;
  }

  function applyLocale(force = false) {
    const next = resolveLocale();
    if (!force && next === activeLocale) return false;
    activeLocale = next;
    if (root) root.setAttribute('aria-label', t('usage.aria', 'Codex Usage'));
    if (popover) popover.setAttribute('aria-label', t('usage.detailsAria', 'Codex usage details'));
    drawTrigger();
    drawPopover();
    if (isOpen) positionPopover();
    return true;
  }

  function getTheme() {
    const html = document.documentElement;
    if (html.classList.contains('electron-dark')) return 'dark';
    if (html.classList.contains('electron-light')) return 'light';
    try {
      return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    } catch (_) {
      return 'dark';
    }
  }

  function parseVisibleColor(value) {
    const text = String(value || '').trim();
    if (!text || text === 'transparent' || text === 'auto') return null;
    if (/^rgba?\(\s*0\s*,\s*0\s*,\s*0\s*,\s*0(?:\.0+)?\s*\)$/i.test(text)) return null;
    return text;
  }

  function normalizedBorder(cs) {
    if (!cs) return '0px solid transparent';
    const width = parseFloat(cs.borderTopWidth || '0');
    if (!Number.isFinite(width) || width <= 0 || cs.borderTopStyle === 'none') return '0px solid transparent';
    return cs.border || `${cs.borderTopWidth} ${cs.borderTopStyle} ${cs.borderTopColor}`;
  }

  function captureThemeSnapshot(theme = getTheme()) {
    const host = document.body || document.documentElement;
    const probeHost = document.createElement('div');
    probeHost.setAttribute('aria-hidden', 'true');
    probeHost.style.cssText = 'position:fixed;left:-10000px;top:-10000px;visibility:hidden;pointer-events:none;z-index:-1;';

    const menu = document.createElement('div');
    menu.className = NATIVE_MENU_CLASS;
    menu.setAttribute('role', 'menu');

    const item = document.createElement('div');
    item.className = NATIVE_MENU_ITEM_CLASS;
    item.setAttribute('role', 'menuitem');
    item.textContent = 'Usage style probe';

    const separator = document.createElement('div');
    separator.className = NATIVE_MENU_SEPARATOR_CLASS;
    separator.setAttribute('role', 'separator');

    menu.append(item, separator);
    probeHost.appendChild(menu);
    host.appendChild(probeHost);

    let snapshot;
    try {
      const menuStyle = getComputedStyle(menu);
      const itemStyle = getComputedStyle(item);
      const separatorStyle = getComputedStyle(separator);
      const help = getHelp();
      const helpStyle = help ? getComputedStyle(help) : null;
      const rootStyle = getComputedStyle(document.documentElement);

      const accent =
        parseVisibleColor(rootStyle.getPropertyValue('--codex-base-accent')) ||
        parseVisibleColor(rootStyle.getPropertyValue('--color-text-accent')) ||
        (theme === 'dark' ? '#ff6363' : '#339cff');

      snapshot = {
        theme,
        menu: {
          background: parseVisibleColor(menuStyle.backgroundColor) || (theme === 'dark' ? '#171717' : '#f6f6f6'),
          foreground: parseVisibleColor(itemStyle.color) || parseVisibleColor(menuStyle.color) || (theme === 'dark' ? '#fefefe' : '#030303'),
          border: normalizedBorder(menuStyle),
          radius: menuStyle.borderRadius || '7.5px',
          shadow: menuStyle.boxShadow && menuStyle.boxShadow !== 'none' ? menuStyle.boxShadow : 'rgba(0, 0, 0, 0.42) 0px 4px 12px 0px',
          fontFamily: itemStyle.fontFamily || menuStyle.fontFamily || 'Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
          fontSize: itemStyle.fontSize || '13px',
          fontWeight: itemStyle.fontWeight || '400',
          lineHeight: itemStyle.lineHeight || '19.5px',
          letterSpacing: itemStyle.letterSpacing || 'normal',
          separator: parseVisibleColor(separatorStyle.backgroundColor) || (theme === 'dark' ? 'rgba(254, 254, 254, 0.15)' : 'rgba(3, 3, 3, 0.137)'),
          separatorWidth: separatorStyle.height || '0.5px'
        },
        trigger: helpStyle ? {
          fontFamily: helpStyle.fontFamily || null,
          fontSize: helpStyle.fontSize || null,
          fontWeight: helpStyle.fontWeight || null,
          lineHeight: helpStyle.lineHeight || null,
          letterSpacing: helpStyle.letterSpacing || null,
          color: parseVisibleColor(helpStyle.color)
        } : null,
        accent
      };
    } finally {
      probeHost.remove();
    }

    themeCaptureCount += 1;
    return snapshot;
  }

  function ensureStyle() {
    style = document.getElementById(STYLE_ID);
    if (!style) {
      style = document.createElement('style');
      style.id = STYLE_ID;
      style.textContent = CSS;
      (document.head || document.documentElement).appendChild(style);
    }
  }

  function getHelp() { return document.getElementById(HELP_ID); }

  function setThemeVars(el, snapshot) {
    if (!el || !snapshot) return;
    const native = snapshot.menu;
    el.style.setProperty('--cub-surface', native.background);
    el.style.setProperty('--cub-border', native.separator);
    el.style.setProperty('--cub-track', native.separator);
    el.style.setProperty('--cub-foreground', native.foreground);
    el.style.setProperty('--cub-accent', snapshot.accent);
    el.style.setProperty('--cub-menu-bg', native.background);
    el.style.setProperty('--cub-menu-fg', native.foreground);
    el.style.setProperty('--cub-menu-border', native.border);
    el.style.setProperty('--cub-menu-radius', native.radius);
    el.style.setProperty('--cub-menu-shadow', native.shadow);
    el.style.setProperty('--cub-menu-font', native.fontFamily);
    el.style.setProperty('--cub-menu-font-size', native.fontSize);
    el.style.setProperty('--cub-menu-line-height', native.lineHeight);
    el.style.setProperty('--cub-menu-letter-spacing', native.letterSpacing);
    el.style.setProperty('--cub-menu-separator', native.separator);
    el.style.setProperty('--cub-menu-separator-width', native.separatorWidth);
  }

  function applyTriggerTypography(snapshot) {
    if (!root || !snapshot?.trigger) return;
    const trigger = snapshot.trigger;
    if (trigger.fontFamily) root.style.fontFamily = trigger.fontFamily;
    if (trigger.fontSize) root.style.fontSize = trigger.fontSize;
    if (trigger.lineHeight) root.style.lineHeight = trigger.lineHeight;
    if (trigger.letterSpacing) root.style.letterSpacing = trigger.letterSpacing;
    if (trigger.fontWeight) root.style.fontWeight = trigger.fontWeight;
    if (trigger.color) root.style.color = trigger.color;
  }

  function applySnapshotToMountedElements(snapshot = themeSnapshot) {
    if (!snapshot) return;
    if (root) {
      root.dataset.theme = snapshot.theme;
      setThemeVars(root, snapshot);
      applyTriggerTypography(snapshot);
    }
    if (popover) {
      popover.dataset.theme = snapshot.theme;
      setThemeVars(popover, snapshot);
    }
  }

  function readThemeSignature() {
    const htmlStyle = getComputedStyle(document.documentElement);
    const help = getHelp();
    const helpStyle = help ? getComputedStyle(help) : null;
    return [
      getTheme(),
      document.documentElement.className || '',
      document.body?.className || '',
      htmlStyle.getPropertyValue('--codex-base-accent').trim(),
      htmlStyle.getPropertyValue('--color-text-accent').trim(),
      htmlStyle.getPropertyValue('--color-text').trim(),
      htmlStyle.getPropertyValue('--color-background-application-menu').trim(),
      htmlStyle.getPropertyValue('--color-codex-application-menu').trim(),
      htmlStyle.getPropertyValue('--color-border-application-menu-separator').trim(),
      htmlStyle.getPropertyValue('--color-background-primary-solid').trim(),
      htmlStyle.getPropertyValue('--font-sans').trim(),
      htmlStyle.getPropertyValue('--font-openai-sans').trim(),
      htmlStyle.getPropertyValue('--text-xs').trim(),
      htmlStyle.getPropertyValue('--text-sm').trim(),
      helpStyle?.fontFamily || '',
      helpStyle?.fontSize || '',
      helpStyle?.fontWeight || '',
      helpStyle?.lineHeight || '',
      helpStyle?.letterSpacing || '',
      helpStyle?.color || ''
    ].join('\u001f');
  }

  function applyTheme(force = false) {
    const signature = readThemeSignature();
    if (!force && themeSnapshot && signature === themeSignatureCache) return false;
    const theme = getTheme();
    themeSnapshot = captureThemeSnapshot(theme);
    appliedTheme = theme;
    themeSignatureCache = signature;
    applySnapshotToMountedElements(themeSnapshot);
    if (isOpen) {
      syncPopoverMinimumWidth();
      requestAnimationFrame(positionPopover);
    }
    return true;
  }

  function scheduleThemeCheck(force = false, delay = 80) {
    if (themeCheckTimer) clearTimeout(themeCheckTimer);
    themeCheckTimer = setTimeout(() => {
      themeCheckTimer = null;
      if (force) applyTheme(true);
      else applyTheme(false);
    }, delay);
  }

  function isPluginEventTarget(target) {
    return Boolean(target && (root?.contains(target) || popover?.contains(target)));
  }

  function isAppearanceInputTarget(target) {
    if (!(target instanceof Element)) return false;
    const input = target.closest('input, select, [role=slider], [role=switch], [role=radio], [role=combobox], [role=listbox]');
    if (!input) return false;
    if (input instanceof HTMLInputElement) {
      const type = String(input.type || 'text').toLowerCase();
      return ['range', 'color', 'number', 'checkbox', 'radio'].includes(type);
    }
    return true;
  }

  function isAppearanceClickTarget(target) {
    if (!(target instanceof Element)) return false;
    return Boolean(target.closest('button, [role=button], [role=radio], [role=switch], [role=option], [role=slider], [role=combobox]'));
  }

  function scheduleAppearanceFallback(delay = 280) {
    if (appearanceFallbackTimer) clearTimeout(appearanceFallbackTimer);
    appearanceFallbackTimer = setTimeout(() => {
      appearanceFallbackTimer = null;
      applyTheme(false);
    }, delay);
  }

  function normalizeRemaining(value) {
    const number = Number(value);
    if (!Number.isFinite(number)) return 0;
    return Math.max(0, Math.min(100, number));
  }

  function createMeter() {
    const meter = document.createElement('span');
    meter.className = 'cub-meter';

    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 14 14');
    svg.setAttribute('aria-hidden', 'true');
    svg.classList.add('cub-ring');

    const track = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
    track.setAttribute('cx', '7'); track.setAttribute('cy', '7'); track.setAttribute('r', '5.1');
    track.setAttribute('pathLength', '100'); track.classList.add('cub-ring-track');

    const progress = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
    progress.setAttribute('cx', '7'); progress.setAttribute('cy', '7'); progress.setAttribute('r', '5.1');
    progress.setAttribute('pathLength', '100'); progress.setAttribute('transform', 'rotate(-90 7 7)');
    progress.classList.add('cub-ring-progress');
    svg.append(track, progress);

    const bar = document.createElement('span');
    bar.className = 'cub-bar';
    const fill = document.createElement('span');
    fill.className = 'cub-bar-fill';
    bar.appendChild(fill);
    meter.append(svg, bar);
    return meter;
  }

  function setMeterValue(meter, remaining) {
    const actual = normalizeRemaining(remaining);
    // Keep a 2% visual floor so a nearly-empty quota is still visible. The text and
    // stored value remain the real percentage; only the ring/bar geometry is floored.
    const visual = actual <= 2 ? 2 : actual;
    const progress = meter?.querySelector('.cub-ring-progress');
    const fill = meter?.querySelector('.cub-bar-fill');
    if (progress) progress.style.strokeDashoffset = String(100 - visual);
    if (fill) fill.style.transform = `scaleX(${visual / 100})`;
    if (meter) {
      meter.dataset.remaining = String(actual);
      meter.dataset.visualRemaining = String(visual);
    }
  }

  function createTriggerItem() {
    const item = document.createElement('span');
    item.className = 'cub-item';
    item.appendChild(createMeter());

    const copy = document.createElement('span');
    copy.className = 'cub-trigger-copy';

    const percent = document.createElement('span');
    percent.className = 'cub-percent';
    copy.appendChild(percent);

    const sep = document.createElement('span');
    sep.className = 'cub-separator';
    sep.textContent = '\u00b7';
    copy.appendChild(sep);

    const reset = document.createElement('span');
    reset.className = 'cub-reset';
    copy.appendChild(reset);

    item.appendChild(copy);
    return item;
  }

  function updateTriggerItem(item, data) {
    const remaining = normalizeRemaining(data?.remaining);
    setMeterValue(item.querySelector('.cub-meter'), remaining);
    const percent = item.querySelector('.cub-percent');
    if (percent) percent.textContent = `${Math.round(remaining)}%`;

    const sep = item.querySelector('.cub-separator');
    const reset = item.querySelector('.cub-reset');
    const hasReset = Boolean(data?.resetsAt ?? data?.resetText);
    if (sep) sep.style.display = hasReset ? '' : 'none';
    if (reset) {
      reset.style.display = hasReset ? '' : 'none';
      reset.textContent = hasReset ? formatResetCompact(data.resetsAt, data.resetText || '') : '';
    }
  }

  function drawTrigger() {
    if (!root) return;
    const windows = Array.isArray(lastState.windows) ? lastState.windows : [];
    if (windows.length === 0) {
      root.style.display = 'none';
      closePopover(false);
      root.replaceChildren();
      return;
    }

    root.style.display = 'inline-flex';
    const used = new Set();
    windows.forEach((data, index) => {
      const key = String(data?.key ?? index);
      let item = Array.from(root.children).find((child) => child.classList?.contains('cub-item') && child.dataset.key === key);
      if (!item) {
        item = createTriggerItem();
        item.dataset.key = key;
      }
      updateTriggerItem(item, data);
      used.add(item);
      const currentAtIndex = root.children[index];
      if (currentAtIndex !== item) root.insertBefore(item, currentAtIndex || null);
    });

    for (const child of Array.from(root.children)) {
      if (child.classList?.contains('cub-item') && !used.has(child)) child.remove();
    }
  }

  function drawPopover() {
    if (!popover) return;
    popover.replaceChildren();
    const windows = Array.isArray(lastState.windows) ? lastState.windows : [];

    for (const data of windows) {
      const remaining = normalizeRemaining(data.remaining);

      const section = document.createElement('div');
      section.className = 'cub-detail-window';

      const quota = document.createElement('div');
      quota.className = 'cub-detail-quota';
      const label = document.createElement('span');
      label.textContent = t('usage.remaining', 'Codex remaining');
      const percent = document.createElement('span');
      percent.className = 'cub-detail-percent';
      percent.textContent = `${Math.round(remaining)}%`;
      quota.append(label, percent);

      const reset = document.createElement('div');
      reset.className = 'cub-detail-reset';
      reset.textContent = formatResetFull(data.resetsAt, data.resetFullText || '');

      section.append(quota, reset);
      popover.appendChild(section);
    }

    const token = lastState.tokens || {};
    const tokenLine = document.createElement('div');
    tokenLine.className = 'cub-token-line';
    const todayLabel = document.createElement('span');
    todayLabel.textContent = `${t('usage.today', 'Today')} `;
    const todayValue = document.createElement('span');
    todayValue.className = 'cub-token-value';
    todayValue.textContent = token.today || '\u2014';
    const tokenSep = document.createElement('span');
    tokenSep.textContent = ` \u00b7 ${t('usage.lifetime', 'Total')} `;
    const lifetimeValue = document.createElement('span');
    lifetimeValue.className = 'cub-token-value';
    lifetimeValue.textContent = token.lifetime || '\u2014';
    const tokenUnit = document.createElement('span');
    tokenUnit.className = 'cub-token-unit';
    tokenUnit.textContent = ` ${t('usage.tokenUnit', 'Token')}`;
    tokenLine.append(todayLabel, todayValue, tokenSep, lifetimeValue, tokenUnit);
    if (token.todayExact || token.lifetimeExact) {
      tokenLine.title = `${t('usage.today', 'Today')} ${token.todayExact || '\u2014'} ${t('usage.tokenUnit', 'Token')}\n${t('usage.lifetime', 'Total')} ${token.lifetimeExact || '\u2014'} ${t('usage.tokenUnit', 'Token')}`;
    }
    popover.appendChild(tokenLine);
    if (isOpen) syncPopoverMinimumWidth();
  }

  function syncPopoverMinimumWidth() {
    if (!root || !popover) return 0;
    const width = closedTriggerWidth > 0 ? closedTriggerWidth : Math.ceil(root.getBoundingClientRect().width);
    if (width > 0) popover.style.setProperty('--cub-trigger-min-width', `${width}px`);
    return width;
  }

  function positionPopover() {
    if (!root || !popover || !isOpen) return;
    const rr = root.getBoundingClientRect();
    const pr = popover.getBoundingClientRect();
    const pad = 8;
    let left = rr.left;
    if (left + pr.width > innerWidth - pad) left = innerWidth - pad - pr.width;
    if (left < pad) left = pad;
    let top = rr.bottom + 5;
    if (top + pr.height > innerHeight - pad) top = Math.max(pad, rr.top - 5 - pr.height);
    popover.style.left = `${Math.round(left)}px`;
    popover.style.top = `${Math.round(top)}px`;
  }

  function getDirectMenubarItem(target) {
    if (!menubarElement || !(target instanceof Element)) return null;
    const item = target.closest('[role="menuitem"]');
    if (!item || item.parentElement !== menubarElement) return null;
    return item;
  }

  function getNativeOpenTrigger() {
    if (!menubarElement) return null;
    return [...menubarElement.children].find((item) =>
      item !== root &&
      item instanceof HTMLElement &&
      item.getAttribute('role') === 'menuitem' &&
      item.getAttribute('data-state') === 'open'
    ) || null;
  }

  function dispatchMousePointerDown(target) {
    if (!(target instanceof HTMLElement) || !target.isConnected) return false;
    try {
      return target.dispatchEvent(new PointerEvent('pointerdown', {
        bubbles: true,
        cancelable: true,
        composed: true,
        button: 0,
        buttons: 1,
        ctrlKey: false,
        pointerId: 1,
        pointerType: 'mouse',
        isPrimary: true
      }));
    } catch (_) {
      return false;
    }
  }

  function openNativeMenuTrigger(trigger) {
    if (!(trigger instanceof HTMLElement) || trigger === root) return false;
    // Radix MenubarTrigger opens from onPointerDown (not onClick). Replaying the
    // same left-mouse pointerdown keeps this bridge aligned with native behavior.
    return dispatchMousePointerDown(trigger);
  }

  function dismissNativeMenu() {
    if (!getNativeOpenTrigger() || !root) return false;
    // Usage is outside Radix Menu.Content. A pointerdown on it is therefore handled
    // by Radix's normal outside-interaction dismissal path, without reaching into
    // React internals or mutating the native trigger's data-state ourselves.
    dispatchMousePointerDown(root);
    return true;
  }

  function cancelMenuBridgeFrame() {
    if (!menuBridgeFrame) return;
    cancelAnimationFrame(menuBridgeFrame);
    menuBridgeFrame = 0;
  }

  function onMenubarPointerOver(event) {
    const item = getDirectMenubarItem(event.target);
    if (!item || item === hoveredMenubarItem) return;
    hoveredMenubarItem = item;
    cancelMenuBridgeFrame();

    if (item === root) {
      if (isOpen) return;
      const nativeOpen = getNativeOpenTrigger();
      if (!nativeOpen) return;

      // Radix only performs hover switching for triggers registered in its own
      // React tree. Usage is injected externally, so bridge only this boundary:
      // close the currently open native menu, then open Usage on the next frame.
      dismissNativeMenu();
      menuBridgeFrame = requestAnimationFrame(() => {
        menuBridgeFrame = 0;
        if (root?.isConnected && root.matches(':hover') && !getNativeOpenTrigger()) openPopover();
      });
      return;
    }

    if (!isOpen) return;

    // While Usage owns the active menubar session, crossing onto a native trigger
    // should feel exactly like native Radix menubar switching. Close Usage without
    // the width-release delay and let the native trigger open itself by click.
    closePopover(false);
    menuBridgeFrame = requestAnimationFrame(() => {
      menuBridgeFrame = 0;
      if (item.isConnected && item.matches(':hover') && item.getAttribute('data-state') !== 'open') {
        openNativeMenuTrigger(item);
      }
    });
  }

  function onMenubarPointerLeave() {
    hoveredMenubarItem = null;
    cancelMenuBridgeFrame();
  }

  function bindMenubarBridge(nextMenubar) {
    if (menubarElement === nextMenubar) return;
    if (menubarElement) {
      try { menubarElement.removeEventListener('pointerover', onMenubarPointerOver, true); } catch (_) {}
      try { menubarElement.removeEventListener('pointerleave', onMenubarPointerLeave, true); } catch (_) {}
    }
    cancelMenuBridgeFrame();
    hoveredMenubarItem = null;
    menubarElement = nextMenubar || null;
    if (menubarElement) {
      menubarElement.addEventListener('pointerover', onMenubarPointerOver, true);
      menubarElement.addEventListener('pointerleave', onMenubarPointerLeave, true);
    }
  }

  function ensurePopover() {
    popover = document.getElementById(POPOVER_ID);
    if (popover) return popover;
    popover = document.createElement('div');
    popover.id = POPOVER_ID;
    popover.className = '';
    popover.setAttribute('role', 'menu');
    popover.setAttribute('aria-label', t('usage.detailsAria', 'Codex usage details'));
    popover.dataset.open = 'false';
    document.body.appendChild(popover);
    if (!themeSnapshot) applyTheme(true);
    popover.dataset.theme = themeSnapshot.theme;
    setThemeVars(popover, themeSnapshot);
    return popover;
  }


  function openPopover() {
    if (!root || !Array.isArray(lastState.windows) || lastState.windows.length === 0) return;
    ensurePopover();

    // Freeze the closed trigger width while the ring morphs into bars. The popover may
    // grow wider for its content, but opening it must never squeeze the native menubar.
    closedTriggerWidth = Math.ceil(root.getBoundingClientRect().width);
    if (closedTriggerWidth > 0) {
      const frozen = `${closedTriggerWidth}px`;
      root.style.width = frozen;
      root.style.minWidth = frozen;
      root.style.flex = `0 0 ${frozen}`;
    }

    drawPopover();
    syncPopoverMinimumWidth();

    isOpen = true;
    root.dataset.open = 'true';
    root.dataset.state = 'open';
    root.setAttribute('data-state', 'open');
    root.setAttribute('aria-expanded', 'true');
    popover.dataset.open = 'true';

    requestAnimationFrame(positionPopover);
  }

  function closePopover(animate = true) {
    isOpen = false;
    if (root) {
      root.dataset.open = 'false';
      root.dataset.state = 'closed';
      root.setAttribute('data-state', 'closed');
      root.setAttribute('aria-expanded', 'false');
      const releaseFrozenWidth = () => {
        if (!root) return;
        root.style.width = '';
        root.style.minWidth = '';
        root.style.flex = '';
      };
      if (!animate || closedTriggerWidth <= 0) {
        releaseFrozenWidth();
      } else {
        const frozen = `${closedTriggerWidth}px`;
        root.style.width = frozen;
        root.style.minWidth = frozen;
        root.style.flex = `0 0 ${frozen}`;
        setTimeout(() => {
          if (!isOpen) releaseFrozenWidth();
        }, 180);
      }
    }
    if (popover) {
      // Keep the popover mounted. Its native style snapshot was applied once at injection
      // and is reused for every open/close cycle without re-reading computed styles.
      popover.dataset.open = 'false';
    }
  }

  function togglePopover(event) {
    if (event) { event.preventDefault(); event.stopPropagation(); }
    if (isOpen) {
      closePopover();
      return;
    }
    if (getNativeOpenTrigger()) dismissNativeMenu();
    openPopover();
  }

  function onDocumentPointerDown(event) {
    if (!isOpen) return;
    if (root && root.contains(event.target)) return;
    if (popover && popover.contains(event.target)) return;
    closePopover();
  }

  function onDocumentKeyDown(event) {
    if (event.key === 'Escape' && isOpen) {
      closePopover();
      try { root && root.focus({ preventScroll: true }); } catch (_) {}
    }
  }

  function ensureMounted() {
    ensureStyle();
    const help = getHelp();
    const menubar = help && help.closest('[role="menubar"]');
    if (!help || !menubar || !menubar.parentElement) return false;
    bindMenubarBridge(menubar);

    let rootCreated = false;
    root = document.getElementById(ROOT_ID);
    if (!root) {
      rootCreated = true;
      root = document.createElement('button');
      root.id = ROOT_ID;
      root.type = 'button';
      root.className = `${help.className} inline-flex items-center`;
      root.setAttribute('aria-label', t('usage.aria', 'Codex Usage'));
      root.setAttribute('aria-haspopup', 'menu');
      root.setAttribute('aria-expanded', 'false');
      root.setAttribute('role', 'menuitem');
      root.setAttribute('tabindex', '-1');
      root.setAttribute('data-orientation', 'horizontal');
      root.dataset.open = 'false';
      root.dataset.state = 'closed';
      root.setAttribute('data-state', 'closed');
      root.addEventListener('click', togglePopover);
    }

    // Keep Usage inside the native menubar, immediately after Help.
    if (root.parentElement !== menubar || root.previousElementSibling !== help) {
      help.insertAdjacentElement('afterend', root);
    }

    if (!themeSnapshot) {
      // First injection only. Normal usage refreshes do not read computed styles.
      applyTheme(true);
    } else if (rootCreated) {
      // DOM recovery may recreate only the trigger; reuse the cached snapshot.
      root.dataset.theme = themeSnapshot.theme;
      setThemeVars(root, themeSnapshot);
      applyTriggerTypography(themeSnapshot);
    }

    // Create the hidden popover during the first injection so all visual styles are fixed
    // up front. Opening it later does not create/re-style a new element.
    ensurePopover();

    drawTrigger();
    if (isOpen && popover) { drawPopover(); popover.dataset.open = 'true'; positionPopover(); }
    return true;
  }

  function scheduleMount() {
    if (mountScheduled) return;
    mountScheduled = true;
    requestAnimationFrame(() => { mountScheduled = false; ensureMounted(); });
  }

  function render(state) {
    if (Array.isArray(state)) lastState = { windows: state, tokens: null, i18n: lastState.i18n || { catalogs: {} } };
    else lastState = state && typeof state === 'object' ? state : { windows: [], tokens: null, i18n: { catalogs: {} } };
    activeLocale = resolveLocale();
    const mounted = ensureMounted();
    if (root) root.setAttribute('aria-label', t('usage.aria', 'Codex Usage'));
    if (popover) popover.setAttribute('aria-label', t('usage.detailsAria', 'Codex usage details'));
    return mounted;
  }

  function destroy() {
    try { themeObserver && themeObserver.disconnect(); } catch (_) {}
    try { domObserver && domObserver.disconnect(); } catch (_) {}
    try { if (themeCheckTimer) clearTimeout(themeCheckTimer); } catch (_) {}
    try { if (appearanceFallbackTimer) clearTimeout(appearanceFallbackTimer); } catch (_) {}
    try { document.removeEventListener('pointerdown', onDocumentPointerDown, true); } catch (_) {}
    try { document.removeEventListener('keydown', onDocumentKeyDown, true); } catch (_) {}
    try { document.removeEventListener('input', onPotentialThemeControl, true); } catch (_) {}
    try { document.removeEventListener('change', onPotentialThemeControl, true); } catch (_) {}
    try { document.removeEventListener('click', onPotentialThemeControl, true); } catch (_) {}
    try { window.removeEventListener('resize', positionPopover); } catch (_) {}
    try { bindMenubarBridge(null); } catch (_) {}
    try { document.getElementById(ROOT_ID)?.remove(); } catch (_) {}
    try { document.getElementById(POPOVER_ID)?.remove(); } catch (_) {}
    try { document.getElementById(STYLE_ID)?.remove(); } catch (_) {}
    try { delete window.__codexUsageBar; } catch (_) {}
  }

  function onPotentialThemeControl(event) {
    const target = event?.target;
    if (isPluginEventTarget(target)) return;

    // Theme variables normally update through html/body attributes or style nodes and
    // are caught immediately by the MutationObserver below. This delayed fallback only
    // covers custom appearance controls that mutate CSSOM without a DOM mutation. It is
    // intentionally later than the Usage morph animation, so it cannot interrupt it.
    if (event?.type === 'click') {
      if (!isAppearanceClickTarget(target)) return;
      scheduleAppearanceFallback(300);
      return;
    }
    if (!isAppearanceInputTarget(target)) return;
    scheduleAppearanceFallback(event?.type === 'input' ? 220 : 260);
  }

  function appearanceMutation(mutation) {
    if (mutation.type === 'attributes') {
      if (mutation.target === document.documentElement || mutation.target === document.body) {
        if (mutation.target === document.documentElement && mutation.attributeName === 'lang') return false;
        return true;
      }
      return mutation.target instanceof Element && /^(STYLE|LINK)$/.test(mutation.target.tagName);
    }
    if (mutation.type === 'characterData') {
      return mutation.target.parentElement?.tagName === 'STYLE';
    }
    if (mutation.type === 'childList') {
      if (mutation.target === document.head || mutation.target.parentElement === document.head) return true;
      return [...mutation.addedNodes, ...mutation.removedNodes].some((node) =>
        node instanceof Element && (/^(STYLE|LINK)$/.test(node.tagName) || Boolean(node.querySelector?.('style,link[rel=stylesheet]')))
      );
    }
    return false;
  }

  themeObserver = new MutationObserver((mutations) => {
    if (mutations.some((mutation) => mutation.type === 'attributes' && mutation.target === document.documentElement && mutation.attributeName === 'lang')) {
      applyLocale(false);
    }
    if (mutations.some(appearanceMutation)) scheduleThemeCheck(false, 45);
  });
  themeObserver.observe(document.documentElement, { attributes: true });
  if (document.body) themeObserver.observe(document.body, { attributes: true });
  if (document.head) themeObserver.observe(document.head, { attributes: true, childList: true, characterData: true, subtree: true });

  domObserver = new MutationObserver(() => {
    // Mount recovery only. Style capture never runs from this high-frequency observer.
    if (!root || !root.isConnected || !getHelp()) scheduleMount();
  });
  domObserver.observe(document.body || document.documentElement, { childList: true, subtree: true });

  document.addEventListener('pointerdown', onDocumentPointerDown, true);
  document.addEventListener('keydown', onDocumentKeyDown, true);
  document.addEventListener('input', onPotentialThemeControl, true);
  document.addEventListener('change', onPotentialThemeControl, true);
  document.addEventListener('click', onPotentialThemeControl, true);
  window.addEventListener('resize', positionPopover);

  window.__codexUsageBar = {
    version: VERSION,
    render,
    ensureMounted,
    applyTheme,
    destroy,
    getTheme,
    openPopover,
    closePopover,
    debugTheme: () => ({
      theme: getTheme(),
      locale: activeLocale,
      documentLang: document.documentElement?.lang || '',
      appliedTheme,
      themeCaptureCount,
      styleMode: 'native probe on injection; recapture only when native appearance signature changes',
      accent: themeSnapshot?.accent || null,
      menuBackground: themeSnapshot?.menu?.background || null,
      menuForeground: themeSnapshot?.menu?.foreground || null,
      menuFontFamily: themeSnapshot?.menu?.fontFamily || null,
      menuFontSize: themeSnapshot?.menu?.fontSize || null,
      menuLineHeight: themeSnapshot?.menu?.lineHeight || null,
      menuRadius: themeSnapshot?.menu?.radius || null,
      menuShadow: themeSnapshot?.menu?.shadow || null
    })
  };
  ensureMounted();
  return true;
})();
