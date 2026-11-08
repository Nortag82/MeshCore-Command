export async function rebootLocalRadio({state,getConnection,run,confirmAction}) {
  const radio=getConnection(),key=state.self?.publicKey;
  if(!state.connected||!radio||!key?.length)throw Error('Connect the local radio first.');
  if(typeof radio.sendCommandReboot!=='function')throw Error('This connection does not support radio reboot.');
  const identity=Array.from(key).map(x=>x.toString(16).padStart(2,'0')).join('');
  const name=state.self.name||'selected radio';
  if(!confirmAction(`Reboot ${name} (${identity.slice(0,8)})?\n\nThe radio will temporarily disconnect. Unsent radio messages may be interrupted.`))return false;
  await run(async()=>{
    const current=state.self?.publicKey;
    if(!state.connected||getConnection()!==radio||!current||Array.from(current).map(x=>x.toString(16).padStart(2,'0')).join('')!==identity)throw Error('The selected radio changed; reboot was cancelled.');
    await radio.sendCommandReboot();
  });
  return true;
}
