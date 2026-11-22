import test from 'node:test';
import assert from 'node:assert/strict';
import { prepareCanvas } from './canvas-context.js';

test('redraws reuse the backing size and reset drawing state', () => {
  let sizes = 0, resets = 0, transform, cleared;
  const ctx = { reset() { resets++; }, setTransform(...args) { transform = args; }, clearRect(...args) { cleared = args; } };
  const canvas = { _width: 0, _height: 0, get width() { return this._width; }, set width(v) { sizes++; this._width = v; },
    get height() { return this._height; }, set height(v) { sizes++; this._height = v; }, getContext() { return ctx; } };
  prepareCanvas(canvas, 300, 120, 2);
  assert.equal(sizes, 2);
  prepareCanvas(canvas, 300, 120, 2);
  assert.equal(sizes, 2);
  assert.equal(resets, 1);
  assert.deepEqual(transform, [2, 0, 0, 2, 0, 0]);
  assert.deepEqual(cleared, [0, 0, 300, 120]);
  prepareCanvas(canvas, 320, 120, 2);
  assert.equal(sizes, 3);
});

test('older browsers still reset the context on redraw', () => {
  let resets = 0;
  const ctx = { setTransform() {}, clearRect() {} };
  const canvas = { _width: 600, height: 240, get width() { return this._width; }, set width(v) { resets++; this._width = v; }, getContext() { return ctx; } };
  prepareCanvas(canvas, 300, 120, 2);
  assert.equal(resets, 1);
});
