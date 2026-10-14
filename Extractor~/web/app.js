'use strict';

const UKRAINE_BOUNDS = L.latLngBounds([44.2, 22.0], [52.5, 40.3]);
const LARGE_AREA_KM2 = 150;

const CATEGORY_COLORS = {
  house: '#f2b134',
  apartments: '#e4572e',
  industrial: '#9b7fd4',
  commercial: '#2bb3b1',
  public: '#4c9be8',
  religious: '#f06aa6',
  garage: '#9aa5b1',
  shed: '#c8b48f',
  farm: '#7bb661',
  utility: '#d38bf0',
  ruins: '#7a7a7a',
  construction: '#d4a017',
  other: '#d0d0d0',
};

const $ = (id) => document.getElementById(id);

let activeJob = null;
let logCount = 0;

const store = {
  get(key, fallback) {
    try { const v = localStorage.getItem(key); return v === null ? fallback : JSON.parse(v); } catch { return fallback; }
  },
  set(key, value) {
    try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* storage unavailable */ }
  },
};

// ---------------------------------------------------------------- map

const map = L.map('map', {
  preferCanvas: true,
  maxBounds: UKRAINE_BOUNDS.pad(0.4),
  minZoom: 5,
  maxZoom: 20,
});
map.fitBounds(UKRAINE_BOUNDS);

map.createPane('labels');
map.getPane('labels').style.zIndex = 350;
map.getPane('labels').style.pointerEvents = 'none';
map.createPane('groundImg'); // terrain and ground pictures, under everything vector
map.getPane('groundImg').style.zIndex = 250;
map.getPane('groundImg').style.pointerEvents = 'none';
map.createPane('envVec'); // roads, water, trees: above the pictures, below building outlines
map.getPane('envVec').style.zIndex = 360;
const envRenderer = L.canvas({ pane: 'envVec' });

const esriUrl = 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}';
const esriAttr = 'Imagery © Esri, Maxar, Earthstar Geographics';
const imageryOpts = { maxZoom: 20, maxNativeZoom: 19, attribution: esriAttr };

const baseLayers = {
  'Satellite + labels': L.layerGroup([
    L.tileLayer(esriUrl, imageryOpts),
    L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}',
      { maxZoom: 20, maxNativeZoom: 19, pane: 'labels' }),
  ]),
  'Satellite': L.tileLayer(esriUrl, imageryOpts),
  'OpenStreetMap': L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png',
    { maxZoom: 20, maxNativeZoom: 19, attribution: '© OpenStreetMap contributors' }),
};
baseLayers['Satellite + labels'].addTo(map);

const footprintLayer = L.featureGroup().addTo(map);
L.control.layers(baseLayers, null, { position: 'topright' }).addTo(map);
L.control.scale({ imperial: false }).addTo(map);

const CoordsControl = L.Control.extend({
  options: { position: 'bottomleft' },
  onAdd() {
    this._div = L.DomUtil.create('div', 'coords');
    this._div.textContent = 'Move the cursor over the map';
    return this._div;
  },
  update(latlng) { this._div.textContent = `${latlng.lat.toFixed(6)}, ${latlng.lng.toFixed(6)}`; },
});
const coords = new CoordsControl().addTo(map);
map.on('mousemove', (e) => coords.update(e.latlng));

loadUkraineOutline();

async function loadUkraineOutline() {
  let geojson = store.get('ukraine-outline-v1', null);
  if (!geojson) {
    try {
      const url = 'https://nominatim.openstreetmap.org/search?country=ukraine&polygon_geojson=1&polygon_threshold=0.005&format=json&limit=1';
      const res = await fetch(url, { headers: { 'Accept-Language': 'en' } });
      const data = await res.json();
      geojson = data[0] && data[0].geojson;
      if (geojson) store.set('ukraine-outline-v1', geojson);
    } catch { return; } // the outline is decoration only
  }
  if (geojson) {
    L.geoJSON(geojson, {
      interactive: false,
      style: { color: '#ffcc33', weight: 1.5, opacity: 0.8, fill: false, dashArray: '6 4' },
    }).addTo(map);
  }
}

// ---------------------------------------------------------------- search

const COORD_RE = /^\s*(-?\d+(?:[.,]\d+)?)\s*[,;\s]\s*(-?\d+(?:[.,]\d+)?)\s*$/;

$('search-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const q = $('search').value.trim();
  const list = $('search-results');
  list.innerHTML = '';
  if (!q) return;

  const m = q.match(COORD_RE);
  if (m) {
    map.setView([parseFloat(m[1].replace(',', '.')), parseFloat(m[2].replace(',', '.'))], 16);
    return;
  }
  try {
    const url = 'https://nominatim.openstreetmap.org/search?format=jsonv2&countrycodes=ua&limit=8&q=' + encodeURIComponent(q);
    const res = await fetch(url, { headers: { 'Accept-Language': 'uk,en' } });
    const results = await res.json();
    if (!results.length) {
      list.innerHTML = '<li><small>Nothing found</small></li>';
      return;
    }
    for (const r of results) {
      const li = document.createElement('li');
      const [name, ...rest] = r.display_name.split(', ');
      li.innerHTML = `${escapeHtml(name)}<small>${escapeHtml(rest.slice(0, 3).join(', '))}</small>`;
      li.addEventListener('click', () => {
        const [s, n, w, ea] = r.boundingbox.map(Number);
        map.fitBounds([[s, w], [n, ea]], { maxZoom: 16 });
        list.innerHTML = '';
      });
      list.appendChild(li);
    }
  } catch (err) {
    list.innerHTML = `<li><small>Search failed: ${escapeHtml(err.message)}</small></li>`;
  }
});

// ---------------------------------------------------------------- selection

const selectionStyle = { color: '#ffcc33', weight: 2, fillColor: '#ffcc33', fillOpacity: 0.07 };
const rectDrawer = new L.Draw.Rectangle(map, { shapeOptions: selectionStyle, showArea: false });
let selection = null;

$('draw-rect').addEventListener('click', () => {
  stopPicking();
  rectDrawer.enable();
  $('draw-rect').classList.add('active');
});
$('clear-sel').addEventListener('click', () => {
  stopPicking();
  rectDrawer.disable();
  setSelection(null);
});

function isBusyDrawing() {
  return picking || rectDrawer.enabled() || Boolean(window.referenceLibrary && window.referenceLibrary.isPlacing());
}

map.on(L.Draw.Event.DRAWSTOP, () => $('draw-rect').classList.remove('active'));
map.on(L.Draw.Event.CREATED, (e) => setSelection(e.layer));
map.on(`${L.Draw.Event.EDITRESIZE} ${L.Draw.Event.EDITMOVE}`, () => updateSelection());

function setSelection(layer) {
  if (selection) map.removeLayer(selection);
  selection = layer;
  if (layer) {
    layer.addTo(map);
    layer.editing.enable();
    layer.on('edit', updateSelection);
  }
  updateSelection();
}

function selectionRing() {
  if (!selection) return null;
  const ll = selection.getLatLngs();
  return Array.isArray(ll[0]) ? ll[0] : ll;
}

function updateSelection() {
  const ring = selectionRing();
  if (!ring) {
    $('sel-info').textContent = 'Drag on the map to draw a rectangle. Then drag a corner to resize it or the centre to move it.';
    $('extract').disabled = true;
    updateOriginMarker();
    return;
  }
  const km2 = L.GeometryUtil.geodesicArea(ring) / 1e6;
  const b = selection.getBounds();
  const w = b.getSouthWest().distanceTo(b.getSouthEast()) / 1000;
  const h = b.getSouthWest().distanceTo(b.getNorthWest()) / 1000;
  $('sel-info').innerHTML = `<b>${km2.toFixed(2)} km²</b> · ${w.toFixed(2)} × ${h.toFixed(2)} km` +
    (km2 > LARGE_AREA_KM2 ? ' · <span style="color:var(--danger)">large area, may take several minutes</span>' : '');
  $('extract').disabled = activeJob !== null;
  updateOriginMarker();
  if (previewRingKey !== null) $('stale').hidden = ringKey() === previewRingKey;
}

// ---------------------------------------------------------------- origin

const originMarker = L.circleMarker([0, 0], {
  radius: 7, color: '#ffffff', weight: 2, fillColor: '#e4572e', fillOpacity: 1, className: 'origin-marker', interactive: false,
});
let picking = false;

for (const radio of document.querySelectorAll('input[name="origin"]')) {
  radio.addEventListener('change', () => { saveOrigin(); applyOriginMode(); });
}
for (const id of ['origin-lat', 'origin-lon']) {
  $(id).addEventListener('input', () => { saveOrigin(); updateOriginMarker(); });
}
$('origin-pick').addEventListener('click', () => (picking ? stopPicking() : startPicking()));

function originMode() {
  return document.querySelector('input[name="origin"]:checked').value;
}

function customOrigin() {
  const lat = parseFloat($('origin-lat').value.replace(',', '.'));
  const lon = parseFloat($('origin-lon').value.replace(',', '.'));
  return Number.isFinite(lat) && Number.isFinite(lon) ? { lat, lon } : null;
}

function currentOrigin() {
  if (originMode() === 'custom') return customOrigin();
  if (!selection) return null;
  const c = selection.getBounds().getCenter();
  return { lat: c.lat, lon: c.lng };
}

function applyOriginMode() {
  $('origin-custom').hidden = originMode() !== 'custom';
  if (originMode() !== 'custom') stopPicking();
  updateOriginMarker();
}

function updateOriginMarker() {
  const o = currentOrigin();
  if (o) {
    originMarker.setLatLng([o.lat, o.lon]);
    if (!map.hasLayer(originMarker)) originMarker.addTo(map);
    originMarker.bindTooltip('Unity origin (0, 0, 0)');
  } else if (map.hasLayer(originMarker)) {
    map.removeLayer(originMarker);
  }
}

function startPicking() {
  rectDrawer.disable();
  picking = true;
  $('origin-pick').classList.add('active');
  map.getContainer().classList.add('picking');
}

function stopPicking() {
  picking = false;
  $('origin-pick').classList.remove('active');
  map.getContainer().classList.remove('picking');
}

map.on('click', (e) => {
  if (!picking) return;
  $('origin-lat').value = e.latlng.lat.toFixed(7);
  $('origin-lon').value = e.latlng.lng.toFixed(7);
  saveOrigin();
  stopPicking();
  updateOriginMarker();
});

function saveOrigin() {
  store.set('origin', { mode: originMode(), lat: $('origin-lat').value, lon: $('origin-lon').value });
}

(function restoreSettings() {
  const o = store.get('origin', null);
  if (o) {
    $('origin-lat').value = o.lat || '';
    $('origin-lon').value = o.lon || '';
    const radio = document.querySelector(`input[name="origin"][value="${o.mode}"]`);
    if (radio) radio.checked = true;
  }
  const opts = store.get('options', null);
  if (opts) {
    $('src-osm').checked = opts.osm !== false;
    $('src-ms').checked = opts.ms !== false;
    if (opts.minArea !== undefined) $('min-area').value = opts.minArea;
    $('opt-elevation').checked = opts.elevation !== false;
    $('opt-heightmap').checked = opts.heightmap !== false;
    $('opt-ground').checked = opts.ground !== false;
    $('opt-roofs').checked = opts.roofs !== false;
    $('opt-conflicts').checked = opts.conflicts !== false;
    $('opt-radar').checked = opts.radar !== false;
    if (opts.heightCell) $('height-cell').value = opts.heightCell;
    if (opts.groundCell) $('ground-cell').value = opts.groundCell;
  }
  applyEnvOptions();
  applyOriginMode();
})();

for (const id of ['opt-heightmap', 'opt-ground']) $(id).addEventListener('change', applyEnvOptions);
function applyEnvOptions() {
  $('env-detail').hidden = !($('opt-heightmap').checked || $('opt-ground').checked);
  $('height-cell').disabled = !$('opt-heightmap').checked;
  $('ground-cell').disabled = !$('opt-ground').checked;
}

// ---------------------------------------------------------------- extraction

$('extract').addEventListener('click', startExtraction);
$('cancel').addEventListener('click', async () => {
  if (activeJob) await fetch(`/api/jobs/${activeJob}/cancel`, { method: 'POST' });
});

async function startExtraction() {
  const ring = selectionRing();
  if (!ring) return;
  const sources = [];
  if ($('src-osm').checked) sources.push('osm');
  if ($('src-ms').checked) sources.push('microsoft');
  if (!sources.length) { alert('Pick at least one data source.'); return; }

  let origin = null;
  if (originMode() === 'custom') {
    origin = customOrigin();
    if (!origin) { alert('Enter a valid latitude and longitude for the fixed origin, or pick it on the map.'); return; }
  }
  const km2 = L.GeometryUtil.geodesicArea(ring) / 1e6;
  if (km2 > LARGE_AREA_KM2 && !confirm(`The selection is ${km2.toFixed(0)} km². Large areas can take many minutes and produce big files. Continue?`)) return;

  const minArea = parseFloat($('min-area').value) || 0;
  const elevation = $('opt-elevation').checked;
  const heightmap = $('opt-heightmap').checked;
  const ground = $('opt-ground').checked;
  const heightCell = parseFloat($('height-cell').value);
  const groundCell = parseFloat($('ground-cell').value);
  const estimateRoofs = $('opt-roofs').checked;
  const fixConflicts = $('opt-conflicts').checked;
  const radar = $('opt-radar').checked;
  store.set('options', { osm: $('src-osm').checked, ms: $('src-ms').checked, minArea, elevation, heightmap, ground, heightCell, groundCell, roofs: estimateRoofs, conflicts: fixConflicts, radar });

  const body = {
    polygon: ring.map((p) => [p.lat, p.lng]),
    sources,
    origin,
    minArea,
    elevation,
    estimateRoofs,
    fixConflicts,
    radar,
    environment: heightmap || ground ? { heightmap, ground, heightCell, groundCell } : null,
    name: $('export-name').value.trim(),
  };

  setJobUi('running');
  $('log').textContent = '';
  logCount = 0;
  try {
    const res = await fetch('/api/jobs', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const data = await res.json();
    if (!res.ok) throw new Error(data.error || res.statusText);
    activeJob = data.id;
    store.set('last-job', activeJob);
    poll();
  } catch (err) {
    setJobUi('error', `Could not start: ${err.message}. Is server.py running?`);
  }
}

async function poll() {
  if (!activeJob) return;
  let job;
  try {
    const res = await fetch(`/api/jobs/${activeJob}/status?since=${logCount}`);
    job = await res.json();
    if (!res.ok) throw new Error('The server was restarted and this job was lost. Press Preview area again.');
  } catch (err) {
    setJobUi('error', err instanceof TypeError ? 'Lost connection to server.py' : err.message);
    activeJob = null;
    store.set('last-job', null);
    updateSelection();
    return;
  }
  if (job.log.length) {
    const pre = $('log');
    pre.textContent += job.log.join('\n') + '\n';
    pre.scrollTop = pre.scrollHeight;
    logCount = job.logCount;
  }
  $('bar').style.width = `${Math.round(job.progress * 100)}%`;
  $('stage').textContent = job.stage;

  if (job.status === 'running') {
    setTimeout(poll, 600);
    return;
  }
  const id = activeJob;
  activeJob = null;
  updateSelection();
  if (job.status === 'done') {
    setJobUi('done', `Done: ${job.summary.total} buildings`);
    store.set('last-result', { summary: job.summary, exportPath: job.exportPath, polygon: job.polygon });
    await showResults(job);
  } else if (job.status === 'cancelled') {
    setJobUi('error', 'Cancelled');
  } else {
    setJobUi('error', job.error || 'Failed');
    $('log-box').open = true;
  }
}

function setJobUi(state, message) {
  $('job').hidden = false;
  $('job').classList.toggle('error', state === 'error');
  $('cancel').hidden = state !== 'running';
  $('extract').disabled = state === 'running' || !selection;
  if (state === 'running') {
    $('bar').style.width = '0%';
    $('stage').textContent = 'Starting…';
  }
  if (message) $('stage').textContent = message;
}

// ---------------------------------------------------------------- preview

const SOURCE_COLORS = { osm: '#4c9be8', microsoft: '#f2b134' };
const SOURCE_LABELS = { osm: 'OpenStreetMap', microsoft: 'Microsoft ML (satellite)' };
const HEIGHT_BANDS = [
  { max: 4, label: 'under 4 m', color: '#fde68a' },
  { max: 8, label: '4–8 m', color: '#fbbf24' },
  { max: 15, label: '8–15 m', color: '#f97316' },
  { max: 30, label: '15–30 m', color: '#dc2626' },
  { max: Infinity, label: '30 m and taller', color: '#7f1d1d' },
];

// Damage states (see extractor/condition.py); a building without evidence is "unknown", not intact.
const CONDITION_STATES = [
  { key: 'destroyed', label: 'Destroyed', color: '#b91c1c' },
  { key: 'heavily_damaged', label: 'Heavily damaged', color: '#ea580c' },
  { key: 'damaged', label: 'Damaged', color: '#eab308' },
  { key: 'intact', label: 'Intact', color: '#22c55e' },
  { key: 'unknown', label: 'Unknown', color: '#8b95a1' },
];
const conditionKey = (p) => (p.condition && p.condition.state) || 'unknown';
const conditionColor = (p) => CONDITION_STATES.find((s) => s.key === conditionKey(p)).color;

// Roofs (see extractor/roofs.py). Every fact has a source: mapped in OSM, reviewed from an image, or a typical
// value for that kind of building ("estimated"); estimated ones are drawn with a dashed outline.
const ROOF_UNKNOWN = '#4b5563';
const ROOF_SHAPES = [
  { key: 'flat', label: 'Flat', color: '#38bdf8' },
  { key: 'gabled', label: 'Gabled', color: '#f97316' },
  { key: 'hipped', label: 'Hipped', color: '#a855f7' },
  { key: 'skillion', label: 'Skillion (one slope)', color: '#22c55e' },
  { key: 'other', label: 'Other shape', color: '#ec4899' },
  { key: 'unknown', label: 'Unknown', color: ROOF_UNKNOWN },
];
const roofShapeKey = (p) => {
  const shape = p.roof && p.roof.shape;
  if (!shape) return 'unknown';
  return ROOF_SHAPES.some((s) => s.key === shape) ? shape : 'other';
};
const roofShapeColor = (p) => ROOF_SHAPES.find((s) => s.key === roofShapeKey(p)).color;

// Roof colours are shown as they are; the legend groups them into families.
const ROOF_FAMILIES = [
  { key: 'dark', label: 'Black / very dark', color: '#26282a' },
  { key: 'darkGrey', label: 'Dark grey', color: '#4b4d4f' },
  { key: 'grey', label: 'Grey', color: '#8b9195' },
  { key: 'light', label: 'Light grey / white', color: '#d4d7d9' },
  { key: 'red', label: 'Red', color: '#8a3a2e' },
  { key: 'brown', label: 'Brown / terracotta', color: '#a34a32' },
  { key: 'yellow', label: 'Yellow / sand', color: '#c9a94e' },
  { key: 'green', label: 'Green', color: '#3f6a4a' },
  { key: 'blue', label: 'Blue', color: '#46627f' },
  { key: 'purple', label: 'Purple', color: '#7e5a9b' },
  { key: 'unknown', label: 'Unknown', color: ROOF_UNKNOWN },
];
function roofFamilyKey(hex) {
  const m = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(hex || '');
  if (!m) return 'unknown';
  const [r, g, b] = [m[1], m[2], m[3]].map((v) => parseInt(v, 16) / 255);
  const max = Math.max(r, g, b), min = Math.min(r, g, b), l = (max + min) / 2, d = max - min;
  if (l < 0.17) return 'dark';
  const s = d === 0 ? 0 : d / (1 - Math.abs(2 * l - 1));
  if (s < 0.15) return l < 0.42 ? 'darkGrey' : l < 0.68 ? 'grey' : 'light';
  let h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  h = (h * 60 + 360) % 360;
  if (h < 12 || h >= 345) return 'red';
  if (h < 45) return 'brown';
  if (h < 70) return 'yellow';
  if (h < 170) return 'green';
  if (h < 260) return 'blue';
  return 'purple';
}
const roofColorOf = (p) => (p.roof && p.roof.color) || null;
const roofColorFill = (p) => roofColorOf(p) || ROOF_UNKNOWN;
const roofIsEstimated = (p) => !!p.roof && (view.color === 'roofShape' ? p.roof.shapeSource : p.roof.colorSource) === 'estimated';

const view = { show: 'footprint', color: 'category' };
let preview = null; // FeatureCollection of the last finished job
let previewRingKey = null;

setupSegmented('show-mode', (v) => { view.show = v; renderPreview(); });
setupSegmented('color-mode', (v) => { view.color = v; renderPreview(); });

function setupSegmented(id, onChange) {
  const root = $(id);
  root.addEventListener('click', (e) => {
    const btn = e.target.closest('button');
    if (!btn) return;
    for (const b of root.querySelectorAll('button')) b.classList.toggle('active', b === btn);
    onChange(btn.dataset.value);
  });
}

async function showResults(job) {
  const s = job.summary;
  $('results').hidden = false;
  $('r-total').textContent = s.total.toLocaleString();
  $('r-osm').textContent = (s.sources.osm || 0).toLocaleString();
  $('r-ms').textContent = (s.sources.microsoft || 0).toLocaleString();
  $('r-area').textContent = s.areaKm2.toFixed(2);
  $('download').href = `/exports/${encodeURIComponent(s.fileName)}`;
  $('download').setAttribute('download', s.fileName);
  $('download').textContent = `Download buildings JSON (${s.fileSizeMB} MB)`;
  $('saved-path').textContent = `Also saved to ${job.exportPath}`;
  const warnings = s.warnings || [];
  $('warnings').hidden = warnings.length === 0;
  $('warnings').textContent = warnings.join(' ');
  $('conflicts-note').hidden = !s.conflicts;
  if (s.conflicts) $('conflicts-note').textContent = conflictsText(s.conflicts);

  const res = await fetch(`/api/previews/${encodeURIComponent(s.fileName)}`);
  if (!res.ok) throw new Error('Preview file not found');
  preview = await res.json();
  previewRingKey = ringKey();
  $('stale').hidden = true;
  currentFile = s.fileName;
  window.dispatchEvent(new CustomEvent('extractor:export-opened', { detail: {
    fileName: currentFile, summary: s, referencePackageUrl: job.referencePackageUrl || s.referencePackageUrl,
  } }));
  renderPreview();
  await showEnvironment(s.environment);
  loadRecent();
}

function renderPreview() {
  footprintLayer.clearLayers();
  renderLegend();
  if (!preview || view.show === 'none') return;
  const both = view.show === 'both';

  if (view.show === 'footprint' || both) {
    L.geoJSON(preview, {
      style: (f) => ({ color: view.color === 'roofColor' ? '#e5e7eb' : colorOf(f.properties), fillColor: colorOf(f.properties),
        weight: 1, fillOpacity: view.color === 'roofColor' ? 0.9 : both ? 0.3 : 0.45,
        dashArray: (view.color === 'condition' && f.properties.condition && f.properties.condition.basis === 'estimated')
          || ((view.color === 'roofShape' || view.color === 'roofColor') && roofIsEstimated(f.properties)) ? '2 2' : null }),
      onEachFeature: bindBuildingPopup,
    }).addTo(footprintLayer);
  }
  if (view.show === 'box' || both) {
    const boxes = {
      type: 'FeatureCollection',
      features: preview.features.map((f) => ({ type: 'Feature', properties: f.properties, box: f.box, geometry: { type: 'Polygon', coordinates: [f.box] } })),
    };
    L.geoJSON(boxes, {
      style: (f) => both
        ? { color: '#ffffff', weight: 1.5, dashArray: '4 3', fill: false }
        : { color: colorOf(f.properties), weight: 1, fillOpacity: 0.45 },
      onEachFeature: bindBuildingPopup,
    }).addTo(footprintLayer);
  }
}

const highlight = L.polygon([], { color: '#ffffff', weight: 2, dashArray: '5 4', fill: false, interactive: false });

function bindBuildingPopup(f, layer) {
  layer.on('click', (e) => {
    if (window.referenceLibrary && window.referenceLibrary.handleBuildingClick(f, e)) return;
    if (window.conditionReview && window.conditionReview.handleBuildingClick(f, e)) return;
    if (isBusyDrawing()) return; // the click belongs to the drawing / origin tool
    highlight.setLatLngs(f.box.map(([lon, lat]) => [lat, lon])).addTo(map);
    L.popup().setLatLng(e.latlng).setContent(popupHtml(f.properties)).openOn(map);
  });
}
map.on('popupclose', () => highlight.remove());

function colorOf(p) {
  if (view.color === 'condition') return conditionColor(p);
  if (view.color === 'roofShape') return roofShapeColor(p);
  if (view.color === 'roofColor') return roofColorFill(p);
  if (view.color === 'source') return SOURCE_COLORS[p.source] || CATEGORY_COLORS.other;
  if (view.color === 'height') return HEIGHT_BANDS.find((b) => p.height < b.max).color;
  return CATEGORY_COLORS[p.category] || CATEGORY_COLORS.other;
}

function renderLegend() {
  const table = $('legend');
  table.innerHTML = '';
  if (!preview) return;
  const counts = new Map();
  let rows;
  let legendNote = null;
  if (view.color === 'condition') {
    for (const f of preview.features) counts.set(conditionKey(f.properties), (counts.get(conditionKey(f.properties)) || 0) + 1);
    rows = CONDITION_STATES.filter((s) => counts.has(s.key)).map((s) => [s.label, s.color, counts.get(s.key)]);
  } else if (view.color === 'roofShape' || view.color === 'roofColor') {
    const byShape = view.color === 'roofShape';
    let estimated = 0, known = 0;
    for (const f of preview.features) {
      const key = byShape ? roofShapeKey(f.properties) : roofFamilyKey(roofColorOf(f.properties));
      counts.set(key, (counts.get(key) || 0) + 1);
      if (key !== 'unknown') {
        known += 1;
        if (roofIsEstimated(f.properties)) estimated += 1;
      }
    }
    rows = (byShape ? ROOF_SHAPES : ROOF_FAMILIES).filter((s) => counts.has(s.key)).map((s) => [s.label, s.color, counts.get(s.key)]);
    legendNote = known === 0
      ? 'No roof data in this export. Extract again to get roof shape and colour.'
      : `Dashed outline: ${estimated.toLocaleString()} of ${known.toLocaleString()} are estimated from the building type, not observed.`;
  } else if (view.color === 'source') {
    for (const f of preview.features) counts.set(f.properties.source, (counts.get(f.properties.source) || 0) + 1);
    rows = [...counts].map(([k, n]) => [SOURCE_LABELS[k] || k, SOURCE_COLORS[k], n]);
  } else if (view.color === 'height') {
    for (const f of preview.features) {
      const band = HEIGHT_BANDS.find((b) => f.properties.height < b.max);
      counts.set(band, (counts.get(band) || 0) + 1);
    }
    rows = HEIGHT_BANDS.filter((b) => counts.has(b)).map((b) => [b.label, b.color, counts.get(b)]);
  } else {
    for (const f of preview.features) counts.set(f.properties.category, (counts.get(f.properties.category) || 0) + 1);
    rows = [...counts].sort((a, b) => b[1] - a[1]).map(([k, n]) => [k, CATEGORY_COLORS[k] || CATEGORY_COLORS.other, n]);
  }
  for (const [label, color, n] of rows) {
    const tr = table.insertRow();
    tr.innerHTML = `<td><span class="swatch" style="background:${color}"></span>${escapeHtml(label)}</td><td>${n.toLocaleString()}</td>`;
  }
  if (legendNote) table.insertRow().innerHTML = `<td colspan="2" class="legend-note">${escapeHtml(legendNote)}</td>`;
}

// After a page reload, pick up the last job if it is still running, otherwise
// the last finished preview (saved on disk, so this also survives a server restart).
(async function resumeLastSession() {
  let job = null;
  const id = store.get('last-job', null);
  if (id) {
    try {
      const res = await fetch(`/api/jobs/${id}/status`);
      if (res.ok) job = await res.json();
      else store.set('last-job', null); // server restarted since; the job is gone
    } catch { /* server not reachable; nothing to resume */ }
  }
  if (job && job.status === 'running') {
    restoreSelection(job.polygon);
    setJobUi('running');
    activeJob = id;
    poll();
    return;
  }
  const last = store.get('last-result', null);
  if (!last) return;
  restoreSelection(last.polygon);
  try {
    await showResults(last);
  } catch {
    $('results').hidden = true; // export was deleted from disk
  }
})();

// ---------------------------------------------------------------- recent exports

let currentFile = null;

async function loadRecent() {
  let files = [];
  try {
    const res = await fetch('/api/exports');
    files = await res.json();
  } catch { return; }
  $('recent-box').hidden = files.length === 0;
  const list = $('recent');
  list.innerHTML = '';
  for (const f of files) {
    const li = document.createElement('li');
    li.title = f.fileName;
    li.classList.toggle('current', f.fileName === currentFile);
    li.innerHTML = `${escapeHtml(f.fileName.replace(/\.json$/, ''))}<small>${escapeHtml(f.modified)} · ${f.sizeMB} MB</small>`;
    li.addEventListener('click', () => openExport(f.fileName));
    list.appendChild(li);
  }
}

async function openExport(fileName) {
  if (activeJob) return;
  try {
    const res = await fetch(`/api/open/${encodeURIComponent(fileName)}`);
    const data = await res.json();
    if (!res.ok) throw new Error(data.error || res.statusText);
    restoreSelection(data.polygon);
    store.set('last-result', data);
    $('job').hidden = true;
    await showResults(data);
  } catch (err) {
    alert(`Could not open ${fileName}: ${err.message}`);
  }
}

loadRecent();

function restoreSelection(polygon) {
  if (!polygon || polygon.length < 3) return;
  // Selections are always rectangles; L.rectangle keeps corner-drag resizing rectangular.
  const layer = L.rectangle(L.latLngBounds(polygon), selectionStyle);
  setSelection(layer);
  map.fitBounds(layer.getBounds(), { padding: [40, 40] });
}

function ringKey() {
  const ring = selectionRing();
  return ring ? ring.map((p) => `${p.lat.toFixed(7)},${p.lng.toFixed(7)}`).join(';') : '';
}

const ROOF_SOURCE_NOTE = { mapped: 'mapped in OSM', reviewed: 'reviewed from an image', estimated: 'estimated, not observed' };

function roofRows(r) {
  const note = (src, conf) => src
    ? ` <span style="color:#888">(${escapeHtml(ROOF_SOURCE_NOTE[src] || src)}${conf != null ? `, ${Math.round(conf * 100)}% sure` : ''})</span>` : '';
  const rows = [];
  if (r.shape) rows.push(['Roof', `${escapeHtml(r.shape)}${note(r.shapeSource, r.shapeConfidence)}`]);
  if (r.pitch != null || r.rise != null) {
    const parts = [];
    if (r.pitch != null) parts.push(`${r.pitch}° pitch${note(r.pitchSource)}`);
    if (r.rise != null && r.rise > 0) parts.push(`${r.rise} m high${note(r.riseSource)}`);
    if (parts.length) rows.push(['Roof size', parts.join(' · ')]);
  }
  if (r.ridge) rows.push(['Ridge', `${r.ridge === 'along' ? 'along' : 'across'} the long side, ${r.ridgeBearing}° from north${note(r.ridgeSource)}`]);
  if (r.slopeBearing != null) rows.push(['Slopes down to', `${r.slopeBearing}° from north`]);
  if (r.material) rows.push(['Roof material', `${escapeHtml(String(r.material).replace(/_/g, ' '))}${note(r.materialSource)}`]);
  if (r.color && /^#[0-9a-f]{6}$/i.test(r.color)) {
    rows.push(['Roof colour', `<span class="swatch" style="background:${r.color}"></span>${r.color}${note(r.colorSource, r.colorConfidence)}`]);
  } else if (r.colorRaw) {
    rows.push(['Mapped roof colour', escapeHtml(String(r.colorRaw))]);
  }
  return rows;
}

function conflictsText(c) {
  const n = (v) => (v || 0).toLocaleString();
  return `Overlaps fixed: ${n(c.overlapCut)} outlines cut, ${n(c.overlapDropped)} dropped. ` +
    (c.roadsChecked ? `Roads: ${n(c.roadMoved)} moved off, ${n(c.roadCut)} cut at the edge, ${n(c.roadDropped)} dropped` +
      (c.osmOnRoad ? `; ${n(c.osmOnRoad)} OSM buildings on a road kept as mapped.` : '.') : 'Roads were not checked (no road data).');
}

function adjustmentText(a) {
  const what = a.reason === 'road' ? `road ${escapeHtml(a.against)}` : escapeHtml(a.against);
  if (a.action === 'moved') return `moved ${a.movedM.toFixed(1)} m off ${what}`;
  const before = a.areaBefore ? ` (was ${a.areaBefore.toFixed(0)} m²)` : '';
  const more = a.alsoAgainst && a.alsoAgainst.length ? ` and ${a.alsoAgainst.length} more` : '';
  return `cut along ${what}${more}${before}`;
}

function popupHtml(p) {
  const rows = [
    ['Category', `${p.category} <span style="color:#888">(${escapeHtml(p.buildingTag)})</span>`],
    ['Source', p.source === 'osm' ? 'OpenStreetMap' : 'Microsoft ML'],
    ['Size', `${p.width.toFixed(1)} × ${p.length.toFixed(1)} m`],
    ['Footprint', `${p.area.toFixed(0)} m²`],
    ['Height', `${p.height.toFixed(1)} m · ${p.levels} fl. <span style="color:#888">(${p.heightSource})</span>`],
    ['Rotation', `${p.rotation.toFixed(1)}°`],
  ];
  if (p.groundElevation != null) {
    const rel = p.y != null ? ` <span style="color:#888">(Y ${p.y > 0 ? '+' : ''}${p.y.toFixed(1)} m from origin)</span>` : '';
    rows.push(['Ground', `${p.groundElevation.toFixed(1)} m a.s.l.${rel}`]);
  }
  if (p.name) rows.unshift(['Name', escapeHtml(p.name)]);
  if (p.footprintAdjusted && p.footprintAdjusted.length) rows.push(['Outline', p.footprintAdjusted.map(adjustmentText).join('; ')]);
  // The roof block already merges mapped, reviewed and estimated values; older exports only have the raw rows.
  if (p.roof) rows.push(...roofRows(p.roof));
  const mapped = p.mappedAppearance || {};
  for (const [key, label] of [['roofShape', 'Mapped roof'], ['roofColorRaw', 'Mapped roof colour'], ['roofMaterial', 'Mapped roof material'], ['wallColorRaw', 'Mapped wall colour'], ['wallMaterial', 'Mapped wall material']]) {
    if (mapped[key] && !(p.roof && key.startsWith('roof'))) rows.push([label, escapeHtml(String(mapped[key]))]);
  }
  const reviewed = p.visualDetails || {};
  for (const [key, label] of [['roofShape', 'Reviewed roof'], ['roofColor', 'Reviewed roof colour'], ['wallColor', 'Reviewed wall colour']]) {
    if (reviewed[key] && !(p.roof && key.startsWith('roof'))) rows.push([label, escapeHtml(String(reviewed[key]))]);
  }
  const state = p.condition && p.condition.state;
  if (state) {
    const c = p.condition;
    const label = CONDITION_STATES.find((s) => s.key === state).label;
    rows.push(['Condition', `${label} <span style="color:#888">(${escapeHtml(c.basis)}, ${escapeHtml(c.confidence)} confidence${c.asOf ? ', ' + escapeHtml(c.asOf) : ''})</span>`]);
    if (c.note) rows.push(['Evidence', escapeHtml(c.note)]);
  }
  return `<table>${rows.map(([k, v]) => `<tr><td>${k}</td><td>${v}</td></tr>`).join('')}</table>` +
    `<div style="color:#888;margin-top:4px">${escapeHtml(p.id)}</div>`;
}

// ---------------------------------------------------------------- terrain & ground

const ROAD_COLORS = { motorway: '#ff7a45', trunk: '#ff7a45', primary: '#ff9f43', secondary: '#ffd166', tertiary: '#fff1a8' };
const env = { data: null, ground: null, hill: null, roads: null, water: null, trees: null, props: null };

for (const id of ['lyr-ground', 'lyr-hill', 'lyr-roads', 'lyr-water', 'lyr-trees', 'lyr-props']) $(id).addEventListener('change', applyEnvLayers);
$('lyr-opacity').addEventListener('input', () => {
  const o = $('lyr-opacity').value / 100;
  for (const k of ['ground', 'hill']) if (env[k]) env[k].setOpacity(o);
});
map.on('zoomend', restyleEnvLines);

function clearEnvironment() {
  for (const k of ['ground', 'hill', 'roads', 'water', 'trees', 'props']) {
    if (env[k]) { map.removeLayer(env[k]); env[k] = null; }
  }
  env.data = null;
}

async function showEnvironment(info) {
  clearEnvironment();
  $('env-box').hidden = !info;
  $('download-env').hidden = !info;
  if (!info) return;

  const s = info.summary || {};
  const hm = s.heightmap;
  $('e-relief').textContent = hm ? Math.round(hm.max - hm.min) : '–';
  $('e-roads').textContent = s.roads ? s.roads.km.toFixed(1) : '–';
  $('e-water').textContent = s.water != null ? (s.water + s.waterways).toLocaleString() : '–';
  $('e-canopy').textContent = s.canopyShare != null ? Math.round(s.canopyShare * 100) : '–';
  const photoProps = s.photoReferences && s.photoReferences.props > 0 ? ` (${s.photoReferences.props} from photo references)` : '';
  $('e-props').textContent = `${s.props || 0} street objects / entrances${photoProps}; ${s.barriers || 0} fence and wall lines. Older exports need re-extraction for these details.`;
  const warnings = info.warnings || [];
  $('env-warnings').hidden = warnings.length === 0;
  $('env-warnings').textContent = warnings.join(' ');
  $('download-env').href = `/exports/${encodeURIComponent(info.zip)}`;
  $('download-env').setAttribute('download', info.zip);
  $('download-env').textContent = `Download terrain & ground (.zip, ${info.zipSizeMB} MB)`;

  const opacity = $('lyr-opacity').value / 100;
  const ov = info.overlays || {};
  if (ov.materials) env.ground = L.imageOverlay(ov.materials.url, ov.materials.bounds, { pane: 'groundImg', opacity, interactive: false });
  if (ov.hillshade) env.hill = L.imageOverlay(ov.hillshade.url, ov.hillshade.bounds, { pane: 'groundImg', opacity, interactive: false });
  $('lyr-ground').disabled = !ov.materials;
  $('lyr-hill').disabled = !ov.hillshade;

  renderEnvLegend(info);
  try {
    const res = await fetch(`/api/env/${encodeURIComponent(currentFile)}/preview`);
    if (res.ok) env.data = await res.json();
  } catch { /* the pictures still work without the vector layers */ }
  buildEnvVectors();
  applyEnvLayers();
}

function renderEnvLegend(info) {
  const table = $('env-legend');
  table.innerHTML = '';
  const areas = (info.summary && info.summary.materials) || {};
  for (const c of info.legend || []) {
    const km2 = areas[c.name];
    if (!km2) continue;
    const tr = table.insertRow();
    tr.title = c.description;
    tr.innerHTML = `<td><span class="swatch" style="background:${c.color}"></span>${escapeHtml(c.name.replace('_', ' '))}</td>` +
      `<td>${km2 >= 0.01 ? km2.toFixed(2) : '<0.01'} km²</td>`;
  }
}

function metersPerPixel() {
  return 40075016.686 * Math.cos(map.getCenter().lat * Math.PI / 180) / Math.pow(2, map.getZoom() + 8);
}

// Lines are drawn at their real width in metres once that is wider than a pixel or two.
function roadStyle(p) {
  const path = p.kind === 'path';
  return {
    renderer: envRenderer,
    color: ROAD_COLORS[p.type] || (path ? '#f7e7b4' : '#ffffff'),
    weight: Math.min(Math.max(p.width / metersPerPixel(), path ? 1 : 1.5), 16),
    opacity: p.tunnel ? 0.35 : 0.95, lineCap: 'butt', lineJoin: 'round',
    dashArray: path || p.type === 'track' ? '4 3' : null,
  };
}

function waterwayStyle(p) {
  return { renderer: envRenderer, color: '#3b8be0', weight: Math.min(Math.max(p.width / metersPerPixel(), 1.5), 20), opacity: 0.9 };
}

function restyleEnvLines() {
  if (env.roads) env.roads.eachLayer((g) => g.eachLayer && g.eachLayer((l) => l.feature && l.feature.properties.layer === 'road' && l.setStyle(roadStyle(l.feature.properties))));
  if (env.water) env.water.eachLayer((g) => g.eachLayer && g.eachLayer((l) => l.feature && l.feature.properties.layer === 'waterway' && l.setStyle(waterwayStyle(l.feature.properties))));
}

function buildEnvVectors() {
  const features = env.data ? env.data.features : [];
  const of = (layer) => features.filter((f) => f.properties.layer === layer);
  const detailPopup = (f, layer) => {
    const p = f.properties;
    const fromPhoto = p.source === 'manual_photo' || p.source === 'photo_reference';
    let content = `<b>${escapeHtml((p.type || '').replaceAll('_', ' '))}</b>` +
      (p.name ? `<br>${escapeHtml(p.name)}` : '') +
      (fromPhoto ? '<br>Placed from a reviewed photo reference' : '<br>Mapped in OpenStreetMap');
    if (fromPhoto && p.confidence) content += `<br>Confidence: ${escapeHtml(p.confidence)}`;
    if (fromPhoto && p.capturedAt) content += `<br>Photo captured: ${escapeHtml(String(p.capturedAt).slice(0, 10))}`;
    layer.bindPopup(content);
  };
  env.props = L.layerGroup([
    L.geoJSON(of('prop'), {
      pointToLayer: (f, ll) => L.circleMarker(ll, { renderer: envRenderer, radius: 4, color: '#78350f', weight: 1, fillColor: '#fbbf24', fillOpacity: 0.95 }),
      onEachFeature: detailPopup,
    }),
    L.geoJSON(of('barrier'), {
      style: { renderer: envRenderer, color: '#fbbf24', weight: 2, dashArray: '6 3' },
      onEachFeature: detailPopup,
    }),
  ]);

  env.roads = L.layerGroup([
    L.geoJSON(of('road'), {
      style: (f) => roadStyle(f.properties),
      onEachFeature: (f, layer) => layer.on('click', (e) => {
        if (isBusyDrawing()) return;
        const p = f.properties;
        const rows = [
          ['Road', escapeHtml(p.name || p.type)],
          ['Class', escapeHtml(p.type)],
          ['Width', `${p.width.toFixed(1)} m <span style="color:#888">(${p.widthSource})</span>`],
          ['Ground', escapeHtml(p.material.replace('_', ' '))],
        ];
        if (p.bridge) rows.push(['', 'bridge']);
        if (p.tunnel) rows.push(['', 'tunnel']);
        L.popup().setLatLng(e.latlng).setContent(`<table>${rows.map(([k, v]) => `<tr><td>${k}</td><td>${v}</td></tr>`).join('')}</table>`).openOn(map);
      }),
    }),
    L.geoJSON(of('rail'), { style: { renderer: envRenderer, color: '#b9a29a', weight: 2, dashArray: '8 4' }, interactive: false }),
  ]);

  env.water = L.layerGroup([
    L.geoJSON(of('water'), { style: { renderer: envRenderer, color: '#2f6fbf', weight: 1, fillColor: '#3b78c8', fillOpacity: 0.55 }, interactive: false }),
    L.geoJSON(of('waterway'), { style: (f) => waterwayStyle(f.properties), interactive: false }),
  ]);

  env.trees = L.layerGroup([
    L.geoJSON(of('tree'), {
      pointToLayer: (f, ll) => L.circleMarker(ll, { renderer: envRenderer, radius: 3, color: '#14532d', weight: 1, fillColor: '#4ade80', fillOpacity: 0.9 }),
      interactive: false,
    }),
    L.geoJSON([...of('treeRow'), ...of('hedge')], { style: { renderer: envRenderer, color: '#22c55e', weight: 2, dashArray: '2 4' }, interactive: false }),
  ]);
}

function applyEnvLayers() {
  const want = {
    ground: $('lyr-ground').checked, hill: $('lyr-hill').checked,
    roads: $('lyr-roads').checked, water: $('lyr-water').checked, trees: $('lyr-trees').checked, props: $('lyr-props').checked,
  };
  for (const k of Object.keys(want)) {
    const layer = env[k];
    if (!layer) continue;
    if (want[k] && !map.hasLayer(layer)) layer.addTo(map);
    if (!want[k] && map.hasLayer(layer)) map.removeLayer(layer);
  }
  if (env.hill && map.hasLayer(env.hill)) env.hill.bringToBack();
}

function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
