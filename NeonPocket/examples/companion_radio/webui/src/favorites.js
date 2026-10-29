const STORAGE_KEY = 'meshcore-favourites-v1';
export function createFavorites(storage) {
 let keys = new Set();
 try { const saved = JSON.parse(storage.getItem(STORAGE_KEY) || '[]'); if (Array.isArray(saved)) keys = new Set(saved.filter(k => typeof k === 'string')); } catch (_) {}
 const listeners = new Set();
 return {
  has: key => keys.has(key),
  toggle(key) { const next = new Set(keys); if (next.has(key)) next.delete(key); else next.add(key); try { storage.setItem(STORAGE_KEY, JSON.stringify([...next])); } catch (_) { throw Error('Could not save favourites on this PC.'); } keys = next; listeners.forEach(fn => fn()); },
  subscribe: fn => listeners.add(fn)
 };
}
