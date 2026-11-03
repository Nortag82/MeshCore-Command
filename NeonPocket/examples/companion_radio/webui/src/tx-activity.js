// Packet counters and received frames indicate recent activity, not live RF keying.
export function createTxActivity({document, now=()=>Date.now()}) {
  const root=document.createElement('aside');root.className='traffic-widget';root.setAttribute('aria-label','Radio activity');
  root.innerHTML='<span class="traffic-chip" id="traffic-tx" title="Radio packet counter; not a live transmitter indicator">TX —</span><span class="traffic-chip" id="traffic-rx" title="Recently observed reception; not a continuous receiver indicator">RX —</span>';
  document.querySelector('.top-actions').prepend(root);
  const tx=root.querySelector('#traffic-tx'),rx=root.querySelector('#traffic-rx');
  let online=false,counts=null,txAt=0,rxAt=0;
  function render(){const t=now();tx.textContent=!online||!counts?'TX —':txAt&&t-txAt<12000?'TX activity':'TX quiet';rx.textContent=!online||(!counts&&!rxAt)?'RX —':rxAt&&t-rxAt<12000?'RX activity':'RX quiet';tx.classList.toggle('active',online&&!!txAt&&t-txAt<12000);rx.classList.toggle('active',online&&!!rxAt&&t-rxAt<12000)}
  function stats(data){if(!Number.isFinite(data?.sent)||!Number.isFinite(data?.recv))return;if(counts){if(data.sent>counts.sent)txAt=now();if(data.recv>counts.recv)rxAt=now()}counts={sent:data.sent,recv:data.recv};render()}
  function received(){rxAt=now();render()}
  function connected(value){online=value;if(!value){counts=null;txAt=0;rxAt=0}render()}
  setInterval(render,3000);render();return {stats,received,connected};
}
