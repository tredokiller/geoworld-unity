'use strict';

// Operates only on explicitly supplied local-image pixels and a building footprint.
// A colour suggestion is evidence for human review, never a roof classification.
(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.RoofColour = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  const radius = 6378137;
  const radians = Math.PI / 180;
  const mercator = lat => Math.log(Math.tan(Math.PI / 4 + lat * radians / 2));
  const fail = reason => ({ ok: false, reason });

  function boundsValid(bounds) {
    if (!Array.isArray(bounds) || bounds.length !== 2 || bounds.some(p => !Array.isArray(p) || p.length !== 2 || !p.every(Number.isFinite))) return false;
    const [[south, west], [north, east]] = bounds;
    return south > -85 && north < 85 && south < north && west >= -180 && east <= 180 && west < east;
  }

  function inside(point, ring) {
    let hit = false;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
      const a = ring[i], b = ring[j];
      if ((a[1] > point[1]) !== (b[1] > point[1]) && point[0] < (b[0] - a[0]) * (point[1] - a[1]) / (b[1] - a[1]) + a[0]) hit = !hit;
    }
    return hit;
  }

  function edgeDistanceSquared(point, ring) {
    let result = Infinity;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
      const a = ring[j], b = ring[i];
      const dx = b[0] - a[0], dy = b[1] - a[1];
      const length = dx * dx + dy * dy;
      const t = length ? Math.max(0, Math.min(1, ((point[0] - a[0]) * dx + (point[1] - a[1]) * dy) / length)) : 0;
      result = Math.min(result, (point[0] - a[0] - t * dx) ** 2 + (point[1] - a[1] - t * dy) ** 2);
    }
    return result;
  }

  function median(values) {
    values.sort((a, b) => a - b);
    const middle = Math.floor(values.length / 2);
    return values.length % 2 ? values[middle] : Math.round((values[middle - 1] + values[middle]) / 2);
  }

  function suggest({ data, width, height, bounds, geometry }) {
    if (!Number.isInteger(width) || !Number.isInteger(height) || width < 1 || height < 1 || !data || data.length !== width * height * 4) return fail('The local image pixels could not be read.');
    if (!boundsValid(bounds)) return fail('Supply the north-up image bounds before sampling.');
    const [[south, west], [north, east]] = bounds;
    const closestEquator = south <= 0 && north >= 0 ? 0 : Math.min(Math.abs(south), Math.abs(north));
    const resolutionX = radius * (east - west) * radians * Math.cos(closestEquator * radians) / width;
    const resolutionY = radius * (north - south) * radians / height;
    const resolutionM = Math.max(resolutionX, resolutionY);
    if (resolutionM > 1) return fail(`This image is too coarse (${resolutionM.toFixed(2)} m/pixel). Use an image with 1 m/pixel or finer detail.`);
    let polygons;
    if (geometry?.type === 'Polygon') polygons = [geometry.coordinates];
    else if (geometry?.type === 'MultiPolygon') polygons = geometry.coordinates;
    else return fail('Select a building footprint with a polygon outline.');
    if (!Array.isArray(polygons) || !polygons.length) return fail('The building footprint is empty.');
    const top = mercator(north), span = top - mercator(south);
    let minX = width, minY = height, maxX = 0, maxY = 0;
    const pixelPolygons = [];
    for (const polygon of polygons) {
      if (!Array.isArray(polygon) || !polygon.length) return fail('The building footprint is invalid.');
      const rings = [];
      for (const ring of polygon) {
        if (!Array.isArray(ring) || ring.length < 3) return fail('The building footprint is invalid.');
        const pixels = [];
        for (const coordinate of ring) {
          if (!Array.isArray(coordinate) || !Number.isFinite(coordinate[0]) || !Number.isFinite(coordinate[1])) return fail('The building footprint has invalid coordinates.');
          const [lon, lat] = coordinate;
          if (lon <= west || lon >= east || lat <= south || lat >= north) return fail('The whole building footprint must lie inside the overhead image, away from its edge.');
          const x = (lon - west) / (east - west) * width;
          const y = (top - mercator(lat)) / span * height;
          if (x < 2 || y < 2 || x > width - 2 || y > height - 2) return fail('This building is too close to the image edge. Use an image covering its complete roof.');
          minX = Math.min(minX, x); minY = Math.min(minY, y);
          maxX = Math.max(maxX, x); maxY = Math.max(maxY, y);
          pixels.push([x, y]);
        }
        rings.push(pixels);
      }
      pixelPolygons.push(rings);
    }
    // Inset at least two pixels and roughly 0.75 m to avoid walls, edges and alignment noise.
    const inset = Math.max(2, 0.75 / resolutionM), insetSquared = inset * inset;
    const step = Math.max(1, Math.ceil(Math.sqrt((maxX - minX) * (maxY - minY) / 200000)));
    const channels = [[], [], []];
    let interiorSamples = 0;
    for (let y = Math.floor(minY); y < Math.ceil(maxY); y += step) {
      for (let x = Math.floor(minX); x < Math.ceil(maxX); x += step) {
        const point = [x + 0.5, y + 0.5];
        const polygon = pixelPolygons.find(rings => inside(point, rings[0]) && !rings.slice(1).some(ring => inside(point, ring)));
        if (!polygon || polygon.some(ring => edgeDistanceSquared(point, ring) < insetSquared)) continue;
        interiorSamples++;
        const offset = (y * width + x) * 4;
        const red = data[offset], green = data[offset + 1], blue = data[offset + 2];
        const luminance = 0.2126 * red + 0.7152 * green + 0.0722 * blue;
        // Green and other strong colours remain eligible; vegetation cannot be reliably identified by colour alone.
        if (data[offset + 3] < 245 || luminance < 22 || Math.min(red, green, blue) > 241) continue;
        channels[0].push(red); channels[1].push(green); channels[2].push(blue);
      }
    }
    const acceptedSamples = channels[0].length;
    if (acceptedSamples < 25) return fail('Fewer than 25 usable interior pixels remain. Use a clearer, higher-resolution image or a larger visible roof.');
    const acceptedFraction = acceptedSamples / interiorSamples;
    if (acceptedFraction < 0.5) return fail('Most of this roof is transparent, very dark or overexposed. No reliable colour suggestion is available.');
    const rgb = channels.map(median);
    return { ok: true, hex: '#' + rgb.map(value => value.toString(16).padStart(2, '0')).join(''), rgb, acceptedSamples, interiorSamples, acceptedFraction, resolutionM };
  }

  return { suggest, boundsValid };
});
