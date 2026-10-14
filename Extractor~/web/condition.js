'use strict';

// Building condition: what state each building is in, and the evidence behind it. Estimates and
// reports are never turned into facts silently: every entry carries its basis, date and source.
// The evidence belongs to the area that covers the open export (summary.conditionArea); an export of a
// place without one gets it from a radar estimate, which makes the area.
(() => {
  const severity = { intact: 0, damaged: 1, heavily_damaged: 2, destroyed: 3 };
  let area = null; // { id, name, radar } of the open export, or null
  let catalog = null;
  let reviewing = false;
  let radarOverlay = null;
  let busy = false;
  let currentFileName = null;
  let radarTimer = null;

  function node(tag, className, text) {
    const el = document.createElement(tag);
    if (className) el.className = className;
    if (text !== undefined) el.textContent = String(text);
    return el;
  }

  function safeLink(label, href) {
    if (!href || typeof href !== 'string') return node('span', '', label);
    try {
      const url = new URL(href, window.location.origin);
      if (!['https:', 'http:'].includes(url.protocol)) return node('span', '', label);
      const a = node('a', '', label);
      a.href = url.href;
      a.target = '_blank';
      a.rel = 'noopener noreferrer';
      return a;
    } catch { return node('span', '', label); }
  }

  function status(text, error = false) {
    const el = $('condition-status');
    el.textContent = text || '';
    el.classList.toggle('warn', Boolean(error));
  }

  async function api(path, body) {
    if (!area) throw new Error('This area has no condition data yet. Estimate it from radar first.');
    return request(`/api/condition/${encodeURIComponent(area.id)}${path}`, body);
  }

  async function request(url, body) {
    const res = await fetch(url, body === undefined ? {} : {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) { const error = new Error(data.error || `The request failed (${res.status})`); error.data = data; throw error; }
    return data;
  }

  const today = () => new Date().toISOString().slice(0, 10);
  const label = (key) => (CONDITION_STATES.find((s) => s.key === key) || { label: key }).label;

  // ------------------------------------------------------------ summary and map

  function renderSummary() {
    const el = $('condition-summary');
    const features = (typeof preview !== 'undefined' && preview && preview.features) || [];
    const show = $('condition-show');
    $('condition-badge').textContent = area ? area.name : (currentFileName ? 'No data yet' : '');
    $('condition-radar-run').textContent = area && area.radar ? 'Update from the newest scenes' : 'Estimate damage from radar';
    $('condition-radar-run').disabled = busy || !currentFileName;
    $('condition-news-fetch').disabled = !area;
    if (!features.length) {
      el.textContent = 'Open an export to see its condition.';
      show.disabled = true;
      $('condition-review').disabled = true;
      $('condition-apply').disabled = true;
      return;
    }
    const counts = { destroyed: 0, heavily_damaged: 0, damaged: 0, intact: 0 };
    const bases = { observed: 0, reported: 0, mapped: 0, estimated: 0 };
    for (const f of features) {
      const c = f.properties.condition;
      if (c && counts[c.state] !== undefined) { counts[c.state] += 1; bases[c.basis] = (bases[c.basis] || 0) + 1; }
    }
    const known = Object.values(counts).reduce((a, b) => a + b, 0);
    el.textContent = known
      ? `${known.toLocaleString()} of ${features.length.toLocaleString()} buildings have a condition: ${counts.destroyed.toLocaleString()} destroyed, ${counts.heavily_damaged.toLocaleString()} heavily damaged, ${counts.damaged.toLocaleString()} damaged, ${counts.intact.toLocaleString()} intact. ${bases.estimated.toLocaleString()} of them are radar estimates; ${bases.reported + bases.observed + bases.mapped} rest on a report, a review or OpenStreetMap.`
      : area
        ? 'This export has no condition yet. Create a package with condition to add the evidence of this area.'
        : 'There is no condition for this area yet: every building would be drawn whole. Estimate the damage from radar to get it.';
    show.disabled = false;
    $('condition-review').disabled = !area;
    $('condition-apply').disabled = busy || !currentFileName || !area;
  }

  function showOnMap() {
    view.color = 'condition';
    for (const b of document.querySelectorAll('#color-mode button')) b.classList.toggle('active', b.dataset.value === 'condition');
    if (view.show === 'none') {
      view.show = 'footprint';
      for (const b of document.querySelectorAll('#show-mode button')) b.classList.toggle('active', b.dataset.value === 'footprint');
    }
    renderPreview();
  }

  // ------------------------------------------------------------ sources and radar

  function renderSources() {
    const list = $('condition-source-list');
    list.replaceChildren();
    const sources = (catalog ? catalog.sources : []).filter((s) => s.kind !== 'osm' && s.kind !== 'satellite');
    if (!sources.length) { list.append(node('p', 'hint', 'No reports yet.')); return; }
    for (const s of sources) {
      const linked = catalog.assessments.filter((a) => a.sourceId === s.id && a.status !== 'rejected').length;
      const calibrates = catalog.rasters.some((r) => (r.calibration || []).some((c) => c.sourceId === s.id));
      const uses = [linked ? `${linked} building${linked === 1 ? '' : 's'} linked` : '', calibrates ? 'sets how many buildings the radar marks' : ''].filter(Boolean);
      const item = node('div', 'condition-source');
      item.append(node('b', '', s.title));
      const meta = node('small', '', [s.publisher, s.publishedAt, uses.join(', ') || 'not used yet'].filter(Boolean).join(' · ') + ' ');
      if (s.url) meta.append(safeLink('open', s.url));
      item.append(meta);
      list.append(item);
    }
  }

  function renderRadar() {
    const list = $('condition-radar-list');
    list.replaceChildren();
    const rasters = catalog ? catalog.rasters : [];
    if (!rasters.length) { list.append(node('p', 'hint', 'No radar estimate yet. Estimate it to download the Sentinel-1 scenes of this area (a few hundred MB and about ten minutes for a town the first time).')); return; }
    for (const r of rasters) {
      const item = node('div', 'condition-radar-item');
      item.append(node('b', '', `Orbit ${r.relativeOrbit} (${r.orbitState || '?'})`));
      item.append(node('div', '', `Newest scene ${r.monitoring && r.monitoring.latest || '?'} · ${r.monitoring ? r.monitoring.scenes : '?'} recent scenes against ${r.baseline ? r.baseline.scenes : '?'} from ${r.baseline ? r.baseline.start : '?'} to ${r.baseline ? r.baseline.end : '?'}`));
      item.append(node('div', 'hint', `Score lines: damaged ${r.thresholds.damaged}, heavily damaged ${r.thresholds.heavily_damaged}, destroyed ${r.thresholds.destroyed}` +
        (r.calibrated ? ' (calibrated with published figures for this area)' : ' (the general ones: not calibrated for this area)')));
      if (r.bounds) {
        const row = node('label');
        const box = node('input');
        box.type = 'checkbox';
        box.checked = Boolean(radarOverlay);
        box.addEventListener('change', () => {
          if (radarOverlay) { map.removeLayer(radarOverlay); radarOverlay = null; }
          if (box.checked) {
            radarOverlay = L.imageOverlay(r.previewUrl, r.bounds, { opacity: 0.65, interactive: false }).addTo(map);
            map.fitBounds(r.bounds);
          }
        });
        row.append(box, node('span', '', 'Show the radar change map'));
        item.append(row);
      }
      list.append(item);
    }
  }

  // The radar estimate of the open export's area (made from the export's bounds when it has none), then a new
  // package of the export with the condition, which opens in its place.
  async function runRadar() {
    if (busy || !currentFileName) return;
    busy = true;
    $('condition-radar-run').disabled = true;
    $('condition-radar-job').hidden = false;
    status('Reading the newest Sentinel-1 scenes. The first run takes about ten minutes for a town.');
    try {
      const { id } = await request('/api/condition/radar', { fileName: currentFileName });
      const job = await new Promise((resolve, reject) => {
        radarTimer = setInterval(async () => {
          try {
            const res = await fetch(`/api/jobs/${id}/status`);
            const job = await res.json();
            $('condition-radar-bar').style.width = `${Math.round((job.progress || 0) * 100)}%`;
            $('condition-radar-stage').textContent = job.stage || '';
            if (job.status === 'done') { clearInterval(radarTimer); resolve(job); }
            else if (job.status === 'error' || job.status === 'cancelled') { clearInterval(radarTimer); reject(new Error(job.error || 'The radar run was stopped')); }
          } catch (error) { clearInterval(radarTimer); reject(error); }
        }, 2000);
      });
      const copy = job.summary && job.summary.copy;
      if (copy) {
        const link = $('condition-download');
        link.href = copy.packageUrl;
        link.hidden = false;
        await showResults(copy); // opens the new export, which sets the area and loads its catalog
        status(`The radar estimate is up to date. ${(copy.conditionEnrichment && copy.conditionEnrichment.message) || ''} The new package has the condition.`);
      } else {
        await load();
        status('The radar estimate is up to date.');
      }
    } catch (error) {
      status(`Radar estimate failed: ${error.message}`, true);
    } finally {
      busy = false;
      $('condition-radar-run').disabled = false;
      $('condition-radar-job').hidden = true;
      renderSummary();
    }
  }

  // ------------------------------------------------------------ news

  function renderNews(data) {
    const list = $('condition-news-list');
    list.replaceChildren();
    const known = new Set((catalog ? catalog.sources : []).map((s) => s.url));
    const items = ((data && data.items) || []).filter((item) => !known.has(item.url));
    if (!items.length) { list.append(node('p', 'hint', data && data.fetchedAt ? 'Nothing new in the last news.' : 'No news fetched yet.')); return; }
    for (const item of items.slice(0, 40)) {
      const row = node('div', 'condition-source');
      row.append(node('b', '', item.title));
      const meta = node('small', '', [item.domain, item.seenAt].filter(Boolean).join(' · ') + ' ');
      meta.append(safeLink('open', item.url), document.createTextNode(' · '));
      const add = node('a', '', 'add as report');
      add.href = '#';
      add.addEventListener('click', (event) => {
        event.preventDefault();
        $('condition-source-title').value = item.title;
        $('condition-source-publisher').value = item.domain || '';
        $('condition-source-url').value = item.url;
        $('condition-source-date').value = item.seenAt || '';
        $('condition-source-kind').value = 'news';
        $('condition-source-form').scrollIntoView({ block: 'nearest' });
        status('Check the report below and press Add report. Then use Review mode to link it to the building it names.');
      });
      meta.append(add);
      row.append(meta);
      list.append(row);
    }
  }

  async function fetchNews() {
    const button = $('condition-news-fetch');
    button.disabled = true;
    $('condition-news-note').textContent = 'Asking GDELT for the latest headlines. It can take half a minute…';
    try {
      const data = await api('/news', {});
      renderNews(data);
      $('condition-news-note').textContent = `${data.items.length} headlines for “${data.query}” (${data.fetchedAt}). Nothing is applied to a building by itself.`;
    } catch (error) {
      if (error.data && error.data.items) renderNews(error.data);
      $('condition-news-note').textContent = `${error.message} The last headlines are shown.`;
    } finally { button.disabled = false; }
  }

  async function addSource(event) {
    event.preventDefault();
    try {
      await api('/sources', { kind: $('condition-source-kind').value, title: $('condition-source-title').value,
        publisher: $('condition-source-publisher').value, url: $('condition-source-url').value,
        publishedAt: $('condition-source-date').value || null, accessedAt: today() });
      event.target.reset();
      await load();
      status('Report added. Turn on Review mode and click the building it names.');
    } catch (error) { status(`Could not add the report: ${error.message}`, true); }
  }

  // ------------------------------------------------------------ review

  function setReviewing(on) {
    reviewing = on;
    $('condition-review').setAttribute('aria-pressed', String(on));
    $('condition-review').classList.toggle('active', on);
    document.body.classList.toggle('condition-reviewing', on);
    if (on) {
      showOnMap();
      status('Review mode: click a building, choose its state and where you know it from.');
    } else {
      map.closePopup();
      status('');
    }
  }

  async function ensureOwnSource() {
    if (catalog && catalog.sources.some((s) => s.id === 'user-review')) return 'user-review';
    await api('/sources', { id: 'user-review', kind: 'visual_review', title: 'Reviewed by me on the map',
      publisher: 'Building Extractor user', note: 'A person looked at imagery or a photo and set the state.', accessedAt: today() });
    await load();
    return 'user-review';
  }

  function evidenceFor(buildingId) {
    return (catalog ? catalog.assessments : []).filter((a) => a.status !== 'rejected' && a.target && (
      (a.target.mode === 'building' && a.target.buildingId === buildingId) ||
      (a.target.mode === 'buildings' && a.target.buildingIds.includes(buildingId))));
  }

  function reviewForm(feature, latlng) {
    const p = feature.properties;
    const form = node('form', 'condition-form');
    form.append(node('h4', '', p.name || `${p.category} · ${p.id}`));
    const current = p.condition;
    form.append(node('div', 'hint', current ? `Now: ${label(current.state)} (${current.basis}, ${current.confidence} confidence${current.asOf ? ', ' + current.asOf : ''})` : 'Now: unknown'));

    const states = node('div', 'condition-states');
    for (const s of CONDITION_STATES.filter((x) => x.key !== 'unknown')) {
      const row = node('label', 'condition-state');
      const radio = node('input');
      radio.type = 'radio';
      radio.name = 'state';
      radio.value = s.key;
      radio.required = true;
      const swatch = node('span', 'swatch');
      swatch.style.background = s.color;
      row.append(radio, swatch, node('span', '', s.label));
      states.append(row);
    }
    form.append(states);

    const as = (text, el) => { const l = node('label'); l.append(node('span', '', text), el); return l; };
    const date = node('input'); date.type = 'date'; date.value = today(); date.required = true;
    const basis = node('select');
    for (const [v, t] of [['observed', 'I saw it (imagery or a photo)'], ['reported', 'A report says so']]) basis.append(new Option(t, v));
    const confidence = node('select');
    for (const v of ['high', 'medium', 'low']) confidence.append(new Option(v, v));
    confidence.value = 'medium';
    const source = node('select');
    source.append(new Option('My own review', 'user-review'));
    for (const s of (catalog ? catalog.sources : []).filter((x) => ['news', 'report', 'visual_review', 'user'].includes(x.kind) && x.id !== 'user-review')) {
      source.append(new Option(s.title.slice(0, 60), s.id));
    }
    const note = node('input'); note.type = 'text'; note.maxLength = 500; note.placeholder = 'What is visible or said (optional)';
    form.append(as('State as of', date), as('How do you know', basis), as('Confidence', confidence), as('Source', source), as('Note', note));

    const actions = node('div', 'condition-actions');
    const save = node('button', 'primary small', 'Save');
    save.type = 'submit';
    const clear = node('button', 'small', 'Close');
    clear.type = 'button';
    clear.addEventListener('click', () => map.closePopup());
    actions.append(save, clear);
    form.append(actions);

    const known = evidenceFor(p.id);
    if (known.length) {
      const box = node('div', 'condition-evidence');
      for (const a of known) {
        const s = catalog.sources.find((x) => x.id === a.sourceId);
        box.append(node('div', '', `${label(a.state)} · ${a.asOf} · ${s ? s.title : a.sourceId}`));
      }
      form.append(box);
    }

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      const chosen = form.querySelector('input[name=state]:checked');
      if (!chosen) return;
      save.disabled = true;
      try {
        const sourceId = source.value === 'user-review' ? await ensureOwnSource() : source.value;
        const { assessment } = await api('/assessments', {
          sourceId, state: chosen.value, asOf: date.value, basis: basis.value, confidence: confidence.value,
          note: note.value, target: { mode: 'building', buildingId: p.id } });
        const now = p.condition;
        if (!now || now.basis === 'estimated' || severity[assessment.state] >= severity[now.state]) {
          p.condition = { state: assessment.state, basis: assessment.basis, confidence: assessment.confidence,
            asOf: assessment.asOf, sources: [assessment.sourceId], note: assessment.note || undefined };
        }
        if (catalog) catalog.assessments.push(assessment);
        map.closePopup();
        renderPreview();
        renderSummary();
        status('Saved. Create a package with condition to bring it to Unity.');
      } catch (error) {
        save.disabled = false;
        status(`Could not save: ${error.message}`, true);
      }
    });
    return form;
  }

  function handleBuildingClick(feature, event) {
    if (!reviewing) return false;
    L.popup({ minWidth: 270, maxWidth: 320 }).setLatLng(event.latlng).setContent(reviewForm(feature, event.latlng)).openOn(map);
    return true;
  }

  // ------------------------------------------------------------ package

  async function createPackage() {
    if (!currentFileName || busy) return;
    busy = true;
    $('condition-apply').disabled = true;
    status('Creating the package with condition…');
    try {
      const data = await api('/apply', { fileName: currentFileName });
      const link = $('condition-download');
      link.href = data.packageUrl;
      link.hidden = false;
      try { await showResults(data); } catch (error) {
        status(`The package was saved, but its map preview could not be opened: ${error.message}. You can still download it.`, true);
        return;
      }
      const stats = data.conditionEnrichment || {};
      status(stats.message || 'Package created.');
    } catch (error) {
      status(`Could not create the package: ${error.message}`, true);
    } finally {
      busy = false;
      renderSummary();
    }
  }

  async function load() {
    catalog = null;
    if (radarOverlay) { map.removeLayer(radarOverlay); radarOverlay = null; }
    if (!area) {
      renderSources();
      renderRadar();
      renderNews(null);
      return;
    }
    try {
      catalog = await api('');
      renderSources();
      renderRadar();
      try { renderNews(await api('/news')); } catch { renderNews(null); /* no headlines kept yet */ }
    } catch (error) {
      status(`The condition catalog could not be loaded: ${error.message}`, true);
    }
  }

  $('condition-show').addEventListener('click', showOnMap);
  $('condition-review').addEventListener('click', () => setReviewing(!reviewing));
  $('condition-apply').addEventListener('click', createPackage);
  $('condition-radar-run').addEventListener('click', runRadar);
  $('condition-source-form').addEventListener('submit', addSource);
  $('condition-news-fetch').addEventListener('click', fetchNews);
  window.addEventListener('extractor:export-opened', (event) => {
    currentFileName = event.detail && event.detail.fileName;
    const summary = (event.detail && event.detail.summary) || {};
    const link = $('condition-download');
    if (summary.conditionLayer) {
      link.href = `/exports/${encodeURIComponent(String(currentFileName).replace(/\.json$/, ''))}.zip`;
      link.hidden = false;
    } else link.hidden = true;
    const next = summary.conditionArea || null;
    const changed = (next && next.id) !== (area && area.id);
    area = next;
    if (changed) { if (reviewing) setReviewing(false); load(); }
    // renderSummary needs the preview that showResults sets just after this event
    setTimeout(renderSummary, 0);
  });
  window.conditionReview = { handleBuildingClick, isReviewing: () => reviewing };
  load();
  renderSummary();
})();
