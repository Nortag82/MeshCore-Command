// Reuse chart backing buffers where supported while restoring default drawing state.
export function prepareCanvas(canvas, width, height, ratio) {
  const pixelWidth = Math.round(width * ratio), pixelHeight = Math.round(height * ratio);
  const resized = canvas.width !== pixelWidth || canvas.height !== pixelHeight;
  if (canvas.width !== pixelWidth) canvas.width = pixelWidth;
  if (canvas.height !== pixelHeight) canvas.height = pixelHeight;
  let context = canvas.getContext('2d');
  if (!context) return null;
  if (!resized) {
    if (typeof context.reset === 'function') context.reset();
    else { canvas.width = pixelWidth; context = canvas.getContext('2d'); }
  }
  if (!context) return null;
  context.setTransform(ratio, 0, 0, ratio, 0, 0);
  context.clearRect(0, 0, width, height);
  return context;
}
