import test from 'node:test';
import assert from 'node:assert/strict';
import {secureMessageCount,showSecureMessageCount} from './secure-message-count.js';

test('shows the number of excess UTF-8 bytes and clears the warning when shortened',()=>{
 const attrs={},el={textContent:'',setAttribute:(name,value)=>attrs[name]=value};
 assert.equal(showSecureMessageCount(el,'a'.repeat(78)),78);
 assert.match(el.textContent,/remove 4 bytes/); assert.equal(attrs['data-over-limit'],'true');
 assert.equal(showSecureMessageCount(el,'a'.repeat(74)),74);
 assert.equal(el.textContent,'74 / 74 UTF-8 bytes'); assert.equal(attrs['data-over-limit'],'false');
 assert.equal(secureMessageCount('é'.repeat(38)).over,2);
 assert.match(secureMessageCount('a'.repeat(75)).text,/remove 1 byte ·/);
});
