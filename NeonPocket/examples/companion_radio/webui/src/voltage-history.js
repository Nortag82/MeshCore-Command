export const WEEK_MS = 7 * 24 * 60 * 60 * 1000;
const BUCKET_MS = 10 * 60 * 1000;

// Keep the latest genuine device-voltage reading in each ten-minute bucket.
// Bounded storage matters because monitoring settings live in WebView storage.
export function pruneVoltageHistory(samples, now = Date.now()) {
  const buckets = new Map();
  for (const sample of Array.isArray(samples) ? samples : []) {
    if (!Array.isArray(sample) || sample.length !== 2) continue;
    const [time, voltage] = sample;
    if (!Number.isFinite(time) || !Number.isFinite(voltage) || time < now - WEEK_MS || time > now || voltage <= 0 || voltage > 1000) continue;
    const bucket = Math.floor(time / BUCKET_MS), previous = buckets.get(bucket);
    if (!previous || time >= previous[0]) buckets.set(bucket, [time, voltage]);
  }
  return [...buckets.values()].sort((a, b) => a[0] - b[0]);
}

export function deviceVoltage(decoded) {
  const sensor = Array.isArray(decoded?.values) && decoded.values.find(v => v && v.channel === 1 && v.type === 116 && Number.isFinite(v.number));
  return sensor && sensor.number > 0 && sensor.number <= 1000 ? sensor.number : null;
}

export function addVoltageReading(samples, decoded, time = Date.now()) {
  const voltage = deviceVoltage(decoded);
  return pruneVoltageHistory(voltage === null ? samples : [...(Array.isArray(samples) ? samples : []), [time, voltage]], time);
}

export function voltageGraphGeometry(samples, now = Date.now(), gapMs = 3600000) {
  const points = pruneVoltageHistory(samples, now);
  if (!points.length) return {points: [], segments: [], min: null, max: null};
  const values = points.map(p => p[1]), low = Math.min(...values), high = Math.max(...values);
  const padding = Math.max((high - low) * .15, .05);
  const min = Math.max(0, low - padding), max = high + padding;
  const coords = points.map(([time, volts]) => ({time, volts, x: 28 + 264 * (time - (now - WEEK_MS)) / WEEK_MS, y: 8 + 60 * (max - volts) / (max - min)}));
  const segments = [];
  for (let i = 1; i < coords.length; i++) if (coords[i].time - coords[i - 1].time <= gapMs) segments.push([coords[i - 1], coords[i]]);
  return {points: coords, segments, min, max};
}
