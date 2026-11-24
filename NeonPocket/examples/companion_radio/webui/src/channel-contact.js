import {contactArguments} from './contact-cleanup.js';
import {rawRadioCommand} from './radio-backup.js';

export function channelSender(message) {
 if (message?.direction !== 'in' || !String(message.key || '').startsWith('ch:')) return '';
 const match = /^([^\r\n:]{1,80}):\s/.exec(String(message.text || ''));
 return match ? match[1].trim() : '';
}

// MeshCore business cards contain a signed advert packet, not just a name or public key.
export function parseContactCard(value) {
 const match = /^meshcore:\/\/([0-9a-f]+)$/i.exec(String(value || '').trim());
 if (!match || match[1].length % 2 || match[1].length > 350) throw Error('Enter a MeshCore contact link, not a channel invite.');
 const card = Uint8Array.from(match[1].match(/../g), pair => parseInt(pair, 16));
 if (card.length < 103 || (card[0] >> 2 & 15) !== 4) throw Error('This is not a complete MeshCore contact advert.');
 const transport = (card[0] & 3) === 0 || (card[0] & 3) === 3 ? 4 : 0;
 const pathOffset = 1 + transport;
 if (pathOffset >= card.length) throw Error('The contact advert is incomplete.');
 const encoded = card[pathOffset], size = (encoded >> 6) + 1, pathBytes = (encoded & 63) * size;
 const payload = pathOffset + 1 + pathBytes;
 if (size > 3 || pathBytes > 64 || payload + 101 > card.length) throw Error('The contact advert has an invalid path or payload.');
 return {card, publicKey: card.slice(payload, payload + 32)};
}

const sameKey = (a,b) => a instanceof Uint8Array && a.length === b.length && a.every((v,i) => v === b[i]);

export async function importContactCard(radio, parsed, {canContinue, newAdvertCode, wait = ms => new Promise(resolve => setTimeout(resolve,ms))}) {
 if (!canContinue()) throw Error('Connect the radio first.');
 let discovered = null;
 const onAdvert = contact => { if (sameKey(contact?.publicKey,parsed.publicKey)) discovered = contact; };
 radio.on(newAdvertCode,onAdvert);
 try {
  const frame = new Uint8Array(parsed.card.length + 1); frame[0] = 18; frame.set(parsed.card,1);
  await rawRadioCommand(radio,frame,0);
  for (let attempt=0; attempt<12; attempt++) {
   if (!canContinue()) throw Error('Radio connection changed; check the contact list before retrying.');
   await wait(500);
   const contacts = await radio.getContacts();
   const found = contacts.find(c => sameKey(c.publicKey,parsed.publicKey));
   if (found) return found;
   if (discovered) {
    const args = contactArguments(discovered); discovered = null;
    if (!canContinue()) throw Error('Radio connection changed; check the contact list before retrying.');
    await radio.addOrUpdateContact(...args);
   }
  }
  throw Error('The radio accepted the contact card but did not confirm the contact. Check the card, contact capacity and radio settings; no contact was removed.');
 } finally { radio.off(newAdvertCode,onAdvert); }
}
