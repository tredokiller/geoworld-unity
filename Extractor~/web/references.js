'use strict';

// Reference locations and reviewed object locations are deliberately separate.
(() => {
  const endpoint = '/api/references/lyman';
  const photoLayer = L.layerGroup().addTo(map);
  const observationLayer = L.layerGroup().addTo(map);
  const dialog = $('reference-dialog');
  const types = {
    building: [['facade', 'Facade'], ['roof', 'Roof'], ['building', 'Building style']],
    prop: [['bench', 'Bench'], ['street_lamp', 'Street lamp'], ['waste_basket', 'Bin'], ['bollard', 'Bollard'], ['shelter', 'Shelter'], ['bus_stop', 'Bus stop'], ['bicycle_parking', 'Bicycle parking'], ['power_pole', 'Utility pole'], ['post_box', 'Post box'], ['gate', 'Gate'], ['fence', 'Fence reference']],
    surface: [['paving', 'Paving'], ['road_surface', 'Road surface']],
    vegetation: [['tree', 'Tree'], ['hedge', 'Hedge'], ['planting', 'Planting']],
  };
  const placeableProps = new Set(types.prop.map(([type]) => type).filter(type => type !== 'fence'));
  let catalog = null;
  let selectedPhoto = null;
  let editing = null;
  let pendingPlacement = null;
  let selectionEditing = false;
  let pointsVisible = true;
  let lastFocused = null;
  let mutating = false;
  let overheadLayer = null;
  let roofCandidate = null;
  let roofSampleVersion = 0;
  let currentReferenceEnrichment = (store.get('last-result', null) || {}).summary?.referenceEnrichment || null;

  function node(tag, className, text) {
    const el = document.createElement(tag);
    if (className) el.className = className;
    if (text !== undefined) el.textContent = String(text);
    return el;
  }

  function safeLink(label, href) {
    const el = node('a', '', label);
    if (!href || typeof href !== 'string') return node('span', '', label);
    try {
      const url = new URL(href, window.location.origin);
      if (!['https:', 'http:'].includes(url.protocol)) return node('span', '', label);
      el.href = url.href;
      el.target = '_blank';
      el.rel = 'noopener noreferrer';
      return el;
    } catch { return node('span', '', label); }
  }

  function imageSource(photo) {
    // Images in a downloaded reference collection must stay on this app's origin.
    if (!photo.imageUrl || typeof photo.imageUrl !== 'string') return '';
    try {
      const url = new URL(photo.imageUrl, window.location.origin);
      return url.origin === window.location.origin && url.pathname !== '/' ? url.href : '';
    } catch { return ''; }
  }

  function friendly(value) { return String(value || '').replace(/_/g, ' '); }

  function dateText(value) {
    if (!value) return 'unknown';
    // Avoid timezone shifts for dates supplied without a timestamp.
    const match = String(value).match(/^(\d{4})(?:-(\d{2})(?:-(\d{2}))?)?/);
    return match ? [match[1], match[2], match[3]].filter(Boolean).join('-') : String(value);
  }

  function datesOf(photo) { return photo.dateMetadata || photo.metadata?.dateMetadata || {}; }

  function cameraDate(photo) {
    const value = datesOf(photo).exif?.DateTimeOriginal;
    const match = String(value || '').match(/^(\d{4})[.:-](\d{2})[.:-](\d{2})/);
    return match ? `${match[1]}-${match[2]}-${match[3]}` : null;
  }

  function photoDate(photo, compact = false) {
    if (photo.capturedAt) return `Captured ${dateText(photo.capturedAt)}`;
    const camera = cameraDate(photo);
    if (camera) return compact ? `Camera date ${camera} · unverified` : `Reported camera date: ${camera} (unverified camera clock)`;
    if (datesOf(photo).descriptionDateMeaning === 'source_reported_capture_date') return `Source reports ${dateText(datesOf(photo).descriptionDateText)} · unverified`;
    const originalYear = String(datesOf(photo).descriptionDateText || '').match(/\b(?:19|20)\d{2}\b/);
    return compact && originalYear ? `Capture date unknown · uploaded ${originalYear[0]}` : 'Capture date unknown';
  }

  function hasPosition(photo) { return photo.position && Number.isFinite(photo.position.lat) && Number.isFinite(photo.position.lon); }
  function isOverhead(photo) { return photo.kind === 'overhead_image' && photo.crs === 'EPSG:3857' && RoofColour.boundsValid(photo.bounds); }

  function setStatus(message, error = false) {
    $('reference-status').textContent = message;
    $('reference-status').classList.toggle('reference-error', error);
    $('reference-dialog-status').textContent = message;
    $('reference-dialog-status').classList.toggle('reference-error', error);
  }

  async function api(path = '', body) {
    const options = body === undefined ? {} : {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
    };
    const response = await fetch(endpoint + path, options);
    let data;
    try { data = await response.json(); } catch { throw new Error('The reference service returned an unreadable response.'); }
    if (!response.ok) throw new Error(data.error || `Request failed (${response.status})`);
    return data;
  }

  async function loadCatalog() {
    catalog = await api();
    if (!Array.isArray(catalog.photos) || !Array.isArray(catalog.observations)) throw new Error('The reference collection is incomplete.');
    renderCatalog();
    if (selectedPhoto) {
      selectedPhoto = catalog.photos.find(photo => photo.id === selectedPhoto.id) || null;
      if (selectedPhoto) renderPhoto();
    }
  }

  function renderCatalog() {
    const count = catalog.photos.length;
    const pointCount = catalog.photos.filter(hasPosition).length;
    $('reference-count').textContent = `${count} photos · ${pointCount} points`;
    $('reference-intro').textContent = `${count} photo references; ${pointCount} have map locations. Images without coordinates remain in the gallery.`;
    $('reference-toggle').disabled = !pointCount && !catalog.observations.some(item => item.placement?.mode === 'point');
    $('reference-download').hidden = !count;
    const list = $('reference-list');
    list.replaceChildren();
    photoLayer.clearLayers();
    catalog.photos.forEach((photo, index) => {
      const card = node('button', 'reference-thumb');
      card.type = 'button';
      card.title = `${photo.title} · ${photoDate(photo)}`;
      const img = node('img');
      img.src = imageSource(photo);
      img.alt = '';
      img.loading = 'lazy';
      card.append(img, node('span', 'reference-thumb-title', photo.title), node('small', '', photoDate(photo, true)));
      card.append(node('small', 'reference-location-note', isOverhead(photo) ? 'Overhead image · bounded area' : hasPosition(photo) ? 'Reference location supplied' : 'Location not supplied'));
      card.addEventListener('click', () => openPhoto(photo));
      list.append(card);
      const position = photo.position;
      if (hasPosition(photo)) {
        const marker = L.marker([position.lat, position.lon], {
          icon: L.divIcon({ className: 'reference-map-icon', html: `<span aria-hidden="true">${index + 1}</span>`, iconSize: [30, 34], iconAnchor: [15, 34] }),
          title: `Reference photo ${index + 1}: ${photo.title}`,
          keyboard: true,
          zIndexOffset: 300,
        });
        marker.bindTooltip(node('span', '', `${isOverhead(photo) ? 'Image extent centre' : 'Photo ' + (index + 1)} · ${photo.title}`));
        marker.on('click', () => { if (!pendingPlacement) openPhoto(photo); });
        marker.addTo(photoLayer);
      }
    });
    const coverage = $('reference-coverage-list');
    coverage.replaceChildren();
    for (const check of catalog.coverage || []) {
      const item = node('div', 'reference-coverage-item');
      item.append(safeLink(check.provider, check.sourceUrl), node('p', 'hint', check.message || friendly(check.status)));
      if (check.checkedAt) item.append(node('small', 'hint', `Checked ${dateText(check.checkedAt)}`));
      coverage.append(item);
    }
    renderPlacements();
    updateEnrichmentButton();
  }

  function renderPlacements() {
    observationLayer.clearLayers();
    for (const observation of catalog.observations) {
      const place = observation.placement;
      if (!place || place.mode !== 'point' || !Number.isFinite(place.lat) || !Number.isFinite(place.lon)) continue;
      const marker = L.circleMarker([place.lat, place.lon], { radius: 6, weight: 2, color: '#ffffff', fillColor: '#0d9488', fillOpacity: 1 });
      marker.bindTooltip(node('span', '', `Reviewed placement · ${friendly(observation.type)}`));
      marker.on('click', () => {
        if (pendingPlacement) return;
        const photo = catalog.photos.find(item => item.id === observation.photoId);
        if (photo) openPhoto(photo);
      });
      marker.addTo(observationLayer);
    }
  }

  function openPhoto(photo) {
    if (selectedPhoto?.id !== photo.id) { roofCandidate = null; roofSampleVersion++; }
    selectedPhoto = photo;
    resetForm();
    renderPhoto();
    lastFocused = document.activeElement;
    if (!dialog.open) dialog.showModal();
    $('reference-close').focus();
  }

  function closePhoto() {
    dialog.close();
    if (lastFocused && document.contains(lastFocused)) lastFocused.focus();
  }

  function renderPhoto() {
    const photo = selectedPhoto;
    $('reference-title').textContent = photo.title;
    $('reference-eyebrow').textContent = isOverhead(photo) ? 'Imported overhead image' : 'Historical reference';
    $('reference-position-guidance').textContent = isOverhead(photo)
      ? 'The map point marks the image extent centre, not a camera. Check image alignment, capture date and roof visibility before accepting any sampled colour. Roof shape is reviewed separately.'
      : 'Photo points are reference locations. The exact camera and object positions are not established. Photos without coordinates stay unplaced.';
    $('reference-image').src = imageSource(photo);
    $('reference-image').alt = photo.title;
    const meta = $('reference-photo-meta');
    meta.replaceChildren(node('p', 'reference-capture-date', photoDate(photo)));
    const dateMetadata = datesOf(photo);
    if (dateMetadata.descriptionDateText) {
      const descriptionDate = dateMetadata.descriptionDateText.replace(/\s*\((?:original )?upload date\)/i, '');
      meta.append(node('p', 'hint', dateMetadata.descriptionDateMeaning === 'source_reported_capture_date'
        ? `Source-reported capture date: ${descriptionDate}. Camera clock and timezone are unverified.`
        : `Original upload: ${descriptionDate} (from the source description).`));
    }
    if (photo.uploadedAt) meta.append(node('p', 'hint', `${photo.provider === 'Wikimedia Commons' ? 'Archived on Commons' : 'Imported locally'}: ${dateText(photo.uploadedAt)}. This is not the capture date.`));
    if (hasPosition(photo)) meta.append(node('p', 'hint', isOverhead(photo) ? 'Image extent centre; this is not a camera location.' : photo.positionSource ? `Reference location: ${photo.positionSource}` : 'Reference location; camera position is not verified.'));
    else meta.append(node('p', 'hint', 'Location not supplied. No map point has been assigned.'));
    meta.append(node('p', 'hint', photo.attribution || 'Attribution: see original source.'));
    const links = node('p', 'reference-source-links');
    links.append(safeLink('Original photo & history', photo.sourceUrl));
    if (photo.license) links.append(node('span', '', ' · '), safeLink(photo.license.name, photo.license.url));
    meta.append(links);
    if (isOverhead(photo)) {
      const actions = node('div', 'reference-actions');
      const show = node('button', 'small', 'Show overhead on map');
      show.type = 'button';
      show.addEventListener('click', () => { showOverhead(photo); closePhoto(); });
      const suggest = node('button', 'small', 'Suggest roof colour');
      suggest.type = 'button';
      suggest.disabled = !currentFile || !preview?.features?.length;
      suggest.title = suggest.disabled ? 'Open or preview a building export first.' : 'Choose one actual building footprint, then review a colour suggestion.';
      suggest.addEventListener('click', () => startRoofSuggestion(photo));
      actions.append(show, suggest);
      meta.append(actions, node('p', 'hint', 'Colour suggestions use only this imported image. They need a clear roof at 1 m/pixel or finer; trees, clouds, shadows and image alignment can affect the result.'));
    } else if (hasPosition(photo)) {
      const locate = node('button', 'small', 'Show reference location');
      locate.type = 'button';
      locate.addEventListener('click', () => {
        closePhoto();
        map.setView([photo.position.lat, photo.position.lon], 17);
      });
      meta.append(locate);
    }
    // The catalog retains the full date evidence; the viewer explains it in plain language above.
    for (const note of photo.notes || []) {
      if (/DateTimeOriginal|uploadedAt|capturedAt|\bEXIF\b/i.test(note)) continue;
      meta.append(node('p', 'hint', note));
    }
    const observations = $('reference-observations');
    observations.replaceChildren();
    const items = catalog.observations.filter(item => item.photoId === photo.id);
    if (!items.length) observations.append(node('p', 'hint', 'No details recorded yet. Add one below after reviewing the photo.'));
    for (const observation of items) observations.append(observationCard(observation));
    renderRoofCandidate();
  }

  function hideOverhead() {
    if (overheadLayer) overheadLayer.remove();
    overheadLayer = null;
    $('overhead-map-controls').hidden = true;
  }

  function showOverhead(photo) {
    if (!isOverhead(photo) || !imageSource(photo)) return;
    hideOverhead();
    if (!map.getPane('referenceOverhead')) {
      map.createPane('referenceOverhead');
      map.getPane('referenceOverhead').style.zIndex = '390';
      map.getPane('referenceOverhead').style.pointerEvents = 'none';
    }
    overheadLayer = L.imageOverlay(imageSource(photo), photo.bounds, {
      pane: 'referenceOverhead', opacity: Number($('overhead-opacity').value) / 100, interactive: false,
    }).addTo(map);
    overheadLayer.on('error', () => setStatus('The imported image could not be displayed. Try reopening its reference.', true));
    $('overhead-map-title').textContent = photo.title;
    $('overhead-map-controls').hidden = false;
    map.fitBounds(photo.bounds, { padding: [40, 40], maxZoom: 20 });
  }

  function readFile(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(reader.result);
      reader.onerror = () => reject(new Error('The selected image could not be read.'));
      reader.readAsDataURL(file);
    });
  }

  async function importOverhead(event) {
    event.preventDefault();
    if (mutating) return;
    const status = $('overhead-import-status');
    status.classList.remove('reference-error');
    const file = $('overhead-file').files[0];
    try {
      if (!file || !['image/png', 'image/jpeg'].includes(file.type)) throw new Error('Choose a PNG or JPEG file.');
      if (file.size > 12 * 1024 * 1024) throw new Error('This file exceeds 12 MB. Export a smaller image first.');
      const bounds = [[Number($('overhead-south').value), Number($('overhead-west').value)], [Number($('overhead-north').value), Number($('overhead-east').value)]];
      if (!RoofColour.boundsValid(bounds)) throw new Error('North must be above south, and east to the right of west. Supply valid image bounds.');
      if (!$('overhead-reuse').checked) throw new Error('Confirm that you own the image or its license permits derived data.');
      const sourceUrl = $('overhead-source-url').value.trim(), licenseUrl = $('overhead-license-url').value.trim();
      for (const value of [sourceUrl, licenseUrl]) {
        if (value && !/^https?:\/\//i.test(value)) throw new Error('Source and license links must begin with http:// or https://.');
      }
      mutating = true;
      $('overhead-import-save').disabled = true;
      status.textContent = 'Importing local image…';
      const result = await api('/photos', {
        title: $('overhead-title').value.trim(), kind: 'overhead_image', crs: $('overhead-crs').value, bounds,
        sourceUrl, attribution: $('overhead-attribution').value.trim(),
        license: { name: $('overhead-license').value.trim(), url: licenseUrl },
        capturedAt: $('overhead-date').value || null, reuseAllowed: true, dataUrl: await readFile(file),
      });
      await loadCatalog();
      $('overhead-import-form').reset();
      status.textContent = 'Image imported. Show it on the map and check alignment before sampling a roof.';
      const photo = catalog.photos.find(item => item.id === result.photo?.id);
      if (photo) openPhoto(photo);
    } catch (error) { status.textContent = error.message; status.classList.add('reference-error'); }
    finally { mutating = false; $('overhead-import-save').disabled = false; updateEnrichmentButton(); }
  }

  function startRoofSuggestion(photo) {
    if (!isOverhead(photo) || !preview?.features?.length || !currentFile) return;
    roofCandidate = null;
    roofSampleVersion++;
    showOverhead(photo);
    // Boxes are visual approximations. Sampling always uses the original footprint.
    view.show = 'footprint';
    for (const button of $('show-mode').querySelectorAll('button')) button.classList.toggle('active', button.dataset.value === 'footprint');
    renderPreview();
    startPlacement({ type: 'roof' }, 'roof');
    pendingPlacement.photo = photo;
    $('reference-placement-message').textContent = 'Click a building footprint inside your overhead image to review a roof colour suggestion.';
  }

  async function imagePixels(photo) {
    const source = imageSource(photo);
    if (!source) throw new Error('Only a locally imported image can be sampled.');
    const img = new Image();
    await new Promise((resolve, reject) => {
      img.onload = resolve;
      img.onerror = () => reject(new Error('The imported image could not be loaded for sampling.'));
      img.src = source;
    });
    if (!img.naturalWidth || !img.naturalHeight || img.naturalWidth * img.naturalHeight > 25000000) throw new Error('Use an image of at most 25 million pixels for browser sampling.');
    const canvas = document.createElement('canvas');
    canvas.width = img.naturalWidth; canvas.height = img.naturalHeight;
    const context = canvas.getContext('2d', { willReadFrequently: true });
    if (!context) throw new Error('Your browser could not read image pixels.');
    context.drawImage(img, 0, 0);
    return context.getImageData(0, 0, canvas.width, canvas.height);
  }

  async function sampleBuildingRoof(feature) {
    const photo = pendingPlacement?.photo;
    const buildingId = feature.properties?.id;
    if (!photo || buildingId === undefined) return;
    const original = preview.features.find(item => String(item.properties?.id) === String(buildingId));
    const exportFile = currentFile;
    cancelPlacement(false);
    roofCandidate = null;
    openPhoto(photo);
    const sampleVersion = ++roofSampleVersion;
    setStatus('Reading interior footprint pixels from your imported image…');
    try {
      const pixels = await imagePixels(photo);
      const result = RoofColour.suggest({ data: pixels.data, width: pixels.width, height: pixels.height, bounds: photo.bounds, geometry: original?.geometry });
      if (selectedPhoto?.id !== photo.id || currentFile !== exportFile || sampleVersion !== roofSampleVersion) return;
      if (!result.ok) { roofCandidate = null; renderRoofCandidate(); setStatus(result.reason, true); return; }
      roofCandidate = { ...result, photoId: photo.id, buildingId: String(buildingId), title: photo.title,
        geometryComplete: original?.properties?.footprintGeometryComplete === true };
      renderRoofCandidate();
      setStatus('Colour suggested. Check the image and save it only if the selected roof is clear and aligned.');
    } catch (error) {
      if (selectedPhoto?.id === photo.id && currentFile === exportFile && sampleVersion === roofSampleVersion) setStatus(`Could not suggest a roof colour: ${error.message}`, true);
    }
  }

  function renderRoofCandidate() {
    const area = $('roof-colour-review');
    area.replaceChildren();
    area.hidden = !roofCandidate || roofCandidate.photoId !== selectedPhoto?.id;
    if (area.hidden) return;
    const candidate = roofCandidate;
    const title = node('p');
    const swatch = node('span', 'roof-colour-swatch');
    swatch.style.backgroundColor = candidate.hex;
    swatch.setAttribute('aria-hidden', 'true');
    title.append(swatch, node('strong', '', `Suggested roof colour ${candidate.hex}`));
    area.append(title, node('p', 'hint', `Building ${candidate.buildingId} · ${candidate.acceptedSamples.toLocaleString()} usable footprint pixels · ${candidate.resolutionM.toFixed(2)} m/pixel.`),
      node('p', 'hint', 'This is an approximate colour under the photographed lighting. Check for cloud, tree cover, shadows and alignment. Roof shape has not been inferred.'));
    if (!candidate.geometryComplete) area.append(node('p', 'warn', 'This older footprint may omit courtyards or other gaps. Check the visible roof, courtyards, trees, shadows and image alignment before saving; the colour may include non-roof surfaces.'));
    const actions = node('div', 'reference-actions');
    const save = node('button', 'primary small', 'Save roof colour');
    save.type = 'button'; save.disabled = mutating;
    save.addEventListener('click', async () => {
      if (mutating || roofCandidate !== candidate) return;
      mutating = true; save.disabled = true;
      try {
        await api('/observations', {
          photoId: candidate.photoId, kind: 'building', type: 'roof',
          description: `Approximate roof colour sampled from ${candidate.title}; reviewed and accepted by the user for building ${candidate.buildingId}. Median of ${candidate.acceptedSamples} interior footprint pixels at approximately ${candidate.resolutionM.toFixed(2)} m/pixel. Lighting, occlusion and alignment can affect colour; roof shape was not inferred.${candidate.geometryComplete ? '' : ' Legacy footprint may omit courtyards; user review required.'}`,
          attributes: { roofColor: candidate.hex }, confidence: 'medium', evidence: 'inferred',
          placement: { mode: 'building', buildingId: candidate.buildingId },
        });
        roofCandidate = null;
        await loadCatalog();
        setStatus('Reviewed roof colour saved. Create an enriched copy to include it in the building export.');
      } catch (error) { setStatus(error.message, true); save.disabled = false; }
      finally { mutating = false; updateEnrichmentButton(); }
    });
    const discard = node('button', 'small ghost', 'Discard suggestion');
    discard.type = 'button';
    discard.addEventListener('click', () => { roofCandidate = null; renderRoofCandidate(); });
    actions.append(save, discard); area.append(actions);
  }

  function observationCard(observation) {
    const card = node('article', 'reference-observation');
    const title = node('div', 'row between');
    title.append(node('strong', '', friendly(observation.type)), node('span', 'reference-confidence', `${observation.confidence} confidence`));
    card.append(title, node('p', '', observation.description));
    const attributes = Object.entries(observation.attributes || {}).filter(([, value]) => value !== '' && value != null);
    if (attributes.length) {
      const values = node('dl', 'reference-attributes');
      for (const [key, value] of attributes) {
        values.append(node('dt', '', key.replace(/([A-Z])/g, ' $1').toLowerCase()), node('dd', '', typeof value === 'object' ? JSON.stringify(value) : value));
      }
      card.append(values);
    }
    card.append(node('p', 'hint', observation.evidence === 'inferred' ? 'Inferred from the photo' : 'Visible in the photo'));
    if (observation.reviewer) card.append(node('p', 'hint', observation.reviewer + (observation.reviewedAt ? ` · ${dateText(observation.reviewedAt)}` : '')));
    const place = observation.placement;
    if (place) {
      card.append(node('p', 'reference-linked', place.mode === 'building' ? `Linked to building ${place.buildingId}` : `Placed at ${place.lat.toFixed(6)}, ${place.lon.toFixed(6)}`));
    } else {
      card.append(node('p', 'hint', 'Reference only · no object location assigned'));
    }
    const actions = node('div', 'reference-observation-actions');
    const edit = node('button', 'small ghost', 'Edit detail');
    edit.type = 'button';
    edit.addEventListener('click', () => editObservation(observation));
    actions.append(edit);
    if (observation.kind === 'building') {
      const link = node('button', 'small', place ? 'Change building' : 'Link a building');
      link.type = 'button';
      link.disabled = !currentFile || !preview || !preview.features.length;
      link.title = link.disabled ? 'Open or preview an export to select one of its building outlines.' : 'Choose the matching building outline on the map.';
      link.addEventListener('click', () => startPlacement(observation, 'building'));
      actions.append(link);
    } else if (observation.kind === 'prop' && placeableProps.has(observation.type)) {
      const placeButton = node('button', 'small', place ? 'Move on map' : 'Place on map');
      placeButton.type = 'button';
      placeButton.addEventListener('click', () => startPlacement(observation, 'point'));
      actions.append(placeButton);
    }
    if (place) {
      const clear = node('button', 'small ghost', 'Clear placement');
      clear.type = 'button';
      clear.addEventListener('click', async () => {
        clear.disabled = true;
        try { await savePlacement(observation, null); } catch (error) { setStatus(error.message, true); clear.disabled = false; }
      });
      actions.append(clear);
    }
    card.append(actions);
    return card;
  }

  function updateTypes(selected) {
    const kind = $('reference-kind').value;
    const select = $('reference-type');
    select.replaceChildren();
    const choices = [...types[kind]];
    if (selected && !choices.some(([value]) => value === selected)) choices.push([selected, friendly(selected)]);
    for (const [value, label] of choices) {
      const option = node('option', '', label);
      option.value = value;
      select.append(option);
    }
    if (selected) select.value = selected;
    $('reference-building-fields').hidden = kind !== 'building';
  }

  function resetForm() {
    editing = null;
    $('reference-form').reset();
    $('reference-form-title').textContent = 'Add an observed detail';
    $('reference-edit-cancel').hidden = true;
    $('reference-form-status').textContent = '';
    $('reference-edit-details').open = false;
    updateTypes();
  }

  function editObservation(observation) {
    resetForm();
    editing = observation;
    $('reference-kind').value = observation.kind;
    updateTypes(observation.type);
    $('reference-description').value = observation.description || '';
    $('reference-confidence').value = observation.confidence;
    $('reference-evidence').value = observation.evidence;
    const attributes = observation.attributes || {};
    for (const [id, key] of [['reference-wall-color', 'wallColor'], ['reference-roof-color', 'roofColor'], ['reference-wall-material', 'wallMaterial'], ['reference-roof-shape', 'roofShape']]) $(id).value = attributes[key] || '';
    $('reference-form-title').textContent = 'Edit observed detail';
    $('reference-edit-cancel').hidden = false;
    $('reference-edit-details').open = true;
    $('reference-description').focus();
  }

  async function submitObservation(event) {
    event.preventDefault();
    if (mutating || !selectedPhoto) return;
    const kind = $('reference-kind').value;
    const attributes = editing && editing.kind === kind ? { ...editing.attributes } : {};
    if (kind === 'building') {
      for (const [id, key] of [['reference-wall-color', 'wallColor'], ['reference-roof-color', 'roofColor'], ['reference-wall-material', 'wallMaterial'], ['reference-roof-shape', 'roofShape']]) {
        const value = $(id).value.trim();
        if (value) attributes[key] = value; else delete attributes[key];
      }
    }
    const observation = {
      photoId: selectedPhoto.id, kind, type: $('reference-type').value,
      description: $('reference-description').value.trim(), attributes,
      confidence: $('reference-confidence').value, evidence: $('reference-evidence').value,
      placement: editing && editing.kind === kind && editing.type === $('reference-type').value ? editing.placement : null,
    };
    if (editing) observation.id = editing.id;
    mutating = true;
    $('reference-save').disabled = true;
    $('reference-form-status').textContent = 'Saving…';
    try {
      await api('/observations', observation);
      await loadCatalog();
      resetForm();
      setStatus('Detail saved. Linked buildings and placed objects can be included in an enriched export.');
    } catch (error) { $('reference-form-status').textContent = error.message; }
    finally { mutating = false; $('reference-save').disabled = false; }
  }

  function startPlacement(observation, mode) {
    cancelPlacement(false);
    stopPicking();
    rectDrawer.disable();
    if (mode === 'building' && view.show === 'none') {
      view.show = 'footprint';
      for (const button of $('show-mode').querySelectorAll('button')) button.classList.toggle('active', button.dataset.value === 'footprint');
      renderPreview();
    }
    selectionEditing = Boolean(selection && selection.editing && selection.editing.enabled());
    if (selectionEditing) selection.editing.disable();
    pendingPlacement = { observation, mode };
    dialog.close();
    $('reference-placement-message').textContent = mode === 'building' ? 'Click the matching building outline in the open export.' : `Click the map where this ${friendly(observation.type)} belongs.`;
    $('reference-placement').hidden = false;
    map.getContainer().classList.add('reference-picking');
    map.closePopup();
  }

  function cancelPlacement(reopen = false) {
    const wasPlacing = pendingPlacement !== null;
    pendingPlacement = null;
    $('reference-placement').hidden = true;
    map.getContainer().classList.remove('reference-picking');
    if (selectionEditing && selection && selection.editing) selection.editing.enable();
    selectionEditing = false;
    if (reopen && wasPlacing && selectedPhoto) openPhoto(selectedPhoto);
  }

  async function savePlacement(observation, placement) {
    await api('/observations', { id: observation.id, placement });
    await loadCatalog();
    setStatus(placement ? 'Location saved. Create an enriched copy to include the detail in your export.' : 'Placement cleared. The detail remains in the reference collection.');
  }

  async function finishPlacement(placement) {
    if (!pendingPlacement) return;
    const observation = pendingPlacement.observation;
    cancelPlacement(false);
    try {
      setStatus('Saving location…');
      await savePlacement(observation, placement);
      if (selectedPhoto) openPhoto(selectedPhoto);
    } catch (error) { setStatus(error.message, true); }
  }

  function handleBuildingClick(feature, event) {
    if (!pendingPlacement) return false;
    if (pendingPlacement.mode === 'roof') {
      sampleBuildingRoof(feature);
    } else if (pendingPlacement.mode === 'point') {
      finishPlacement({ mode: 'point', lat: event.latlng.lat, lon: event.latlng.lng });
    } else {
      const buildingId = feature.properties && feature.properties.id;
      if (buildingId !== undefined && currentFile) finishPlacement({ mode: 'building', buildingId: String(buildingId) });
    }
    return true;
  }

  function updateEnrichmentButton() {
    const linked = catalog && catalog.observations.some(item => item.placement);
    const canApply = linked || currentReferenceEnrichment;
    $('reference-enrich').disabled = !currentFile || !canApply || mutating;
    $('reference-enrich').title = !currentFile ? 'Open or preview a building export first.' : !canApply ? 'Link a detail to a building or place an object first.' : `Create a new enriched copy of ${currentFile}`;
    if (catalog) $('reference-guidance').textContent = currentFile
      ? 'Link a detail to a matching building or place an object, then create an enriched copy. The original export is preserved.'
      : 'Open or preview an area to link building details. Street objects can be placed after you review their location.';
  }

  function updatePackageLink(data) {
    const link = $('reference-enriched-download');
    const path = data && (data.referencePackageUrl || data.summary?.referencePackageUrl);
    link.hidden = true;
    link.removeAttribute('href');
    if (!path) return;
    try {
      const url = new URL(path, location.origin);
      if (url.origin === location.origin && url.pathname.startsWith('/exports/')) {
        link.href = url.href;
        link.hidden = false;
      }
    } catch { /* Invalid download paths are not rendered. */ }
  }

  async function enrichExport() {
    if (!currentFile || mutating) return;
    if (activeJob) { setStatus('Wait for the current area preview to finish first.', true); return; }
    mutating = true;
    updateEnrichmentButton();
    setStatus('Creating enriched copy…');
    try {
      const data = await api('/enrich', { fileName: currentFile });
      updatePackageLink(data);
      restoreSelection(data.polygon);
      store.set('last-result', data);
      try { await showResults(data); }
      catch (error) {
        setStatus(`The enriched copy was saved, but its map preview could not be opened: ${error.message}. You can download the enriched package.`, true);
        return;
      }
      const result = data.enrichment || {};
      setStatus(result.message || `Created enriched copy: ${result.buildings || 0} building details, ${result.props || 0} placed objects; ${result.skipped || 0} skipped.`);
    } catch (error) { setStatus(`Could not create enriched copy: ${error.message}`, true); }
    finally { mutating = false; updateEnrichmentButton(); }
  }

  $('reference-toggle').addEventListener('click', () => {
    pointsVisible = !pointsVisible;
    for (const layer of [photoLayer, observationLayer]) pointsVisible ? layer.addTo(map) : layer.remove();
    $('reference-toggle').textContent = pointsVisible ? 'Hide points' : 'Show points';
    $('reference-toggle').setAttribute('aria-pressed', String(pointsVisible));
  });
  $('reference-close').addEventListener('click', closePhoto);
  dialog.addEventListener('click', event => {
    if (event.target !== dialog) return;
    const rect = dialog.getBoundingClientRect();
    if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) closePhoto();
  });
  $('reference-form').addEventListener('submit', submitObservation);
  $('reference-kind').addEventListener('change', () => updateTypes());
  $('reference-edit-cancel').addEventListener('click', resetForm);
  $('reference-placement-cancel').addEventListener('click', () => cancelPlacement(true));
  $('reference-enrich').addEventListener('click', enrichExport);
  $('overhead-import-form').addEventListener('submit', importOverhead);
  $('overhead-hide').addEventListener('click', hideOverhead);
  $('overhead-opacity').addEventListener('input', () => { if (overheadLayer) overheadLayer.setOpacity(Number($('overhead-opacity').value) / 100); });
  for (const id of ['draw-rect', 'clear-sel', 'origin-pick']) $(id).addEventListener('click', () => cancelPlacement(false));
  window.addEventListener('keydown', event => { if (event.key === 'Escape' && pendingPlacement) cancelPlacement(true); });
  window.addEventListener('extractor:export-opened', event => {
    cancelPlacement(false);
    roofCandidate = null;
    roofSampleVersion++;
    currentReferenceEnrichment = event.detail.summary?.referenceEnrichment || null;
    updatePackageLink(event.detail);
    updateEnrichmentButton();
    if (selectedPhoto) renderPhoto();
  });
  map.on('click', event => {
    if (pendingPlacement && pendingPlacement.mode === 'point') finishPlacement({ mode: 'point', lat: event.latlng.lat, lon: event.latlng.lng });
  });
  window.referenceLibrary = { handleBuildingClick, isPlacing: () => pendingPlacement !== null };
  updateTypes();
  updatePackageLink(store.get('last-result', null));
  loadCatalog().catch(error => {
    $('reference-intro').textContent = 'The photo references could not be loaded.';
    setStatus(error.message, true);
  });
})();
