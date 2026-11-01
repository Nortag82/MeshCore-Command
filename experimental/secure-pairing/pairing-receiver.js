// Prototype only: adapter.get/put must be durable and scoped to the group.
// A sender's invitation must be independently verified and explicitly approved.
import {validateInvitation,openGroup} from './pairing-crypto.js';
export class VerifiedGroupReceiver {
 constructor({group,key,member,ledger}){if(!/^[0-9a-f]{32}$/.test(group)||typeof member!=='function'||!ledger?.get||!ledger?.put)throw Error('Invalid receiver configuration');this.group=group;this.key=key;this.member=member;this.ledger=ledger;this.tail=Promise.resolve()}
 receive(envelope){const run=async()=>{
  if(!envelope||envelope.group!==this.group)throw Error('Wrong group');
  const entry=await this.member(envelope.sender);if(!entry?.approved)throw Error('Sender is not approved');
  const invite=await validateInvitation(entry.invitation);if(invite.node!==envelope.sender)throw Error('Sender identity mismatch');
  const plaintext=await openGroup(this.key,invite.sign,envelope);
  const id=`${this.group}:${invite.node}`,record=await this.ledger.get(id);
  const state=record===null?{highest:0,bits:'0'}:record;
  if(!state||!Number.isSafeInteger(state.highest)||state.highest<0||typeof state.bits!=='string'||!/^[0-9a-f]{1,16}$/.test(state.bits))throw Error('Invalid replay ledger');
  let highest=state.highest,bits=BigInt('0x'+state.bits),offset=highest-envelope.sequence;
  if(offset>=64)throw Error('Message is outside the replay window');
  if(offset>=0){const mask=1n<<BigInt(offset);if(bits&mask)throw Error('Replayed message');bits|=mask}
  else{const advance=envelope.sequence-highest;bits=advance>=64?1n:((bits<<BigInt(advance))|1n)&((1n<<64n)-1n);highest=envelope.sequence}
  await this.ledger.put(id,{highest,bits:bits.toString(16)});return plaintext;
 };const result=this.tail.then(run);this.tail=result.catch(()=>{});return result}
}
