const encoder = new TextEncoder();

export function secureMessageCount(value, limit = 74) {
 const length = encoder.encode(value).length;
 const over = Math.max(0, length - limit);
 return {length, over, text: over ? `Message too long: remove ${over} byte${over === 1 ? '' : 's'} · ${length} / ${limit} UTF-8 bytes` : `${length} / ${limit} UTF-8 bytes`};
}

export function showSecureMessageCount(element, value, limit = 74) {
 const result = secureMessageCount(value, limit);
 element.textContent = result.text;
 element.setAttribute('data-over-limit', String(result.over > 0));
 return result.length;
}
