export function requestTelemetry(conn, publicKey, Constants, extraTimeout=10000, onRoute=()=>{}) {
  return new Promise((resolve,reject)=>{
    let timer, settled=false;
    const clean=()=>{clearTimeout(timer);conn.off(Constants.ResponseCodes.Sent,onSent);conn.off(Constants.ResponseCodes.Err,onError);conn.off(Constants.PushCodes.TelemetryResponse,onResponse)};
    const finish=(err,value)=>{if(settled)return;settled=true;clean();err?reject(err):resolve(value)};
    const onSent=r=>{conn.off(Constants.ResponseCodes.Sent,onSent);try{onRoute(r?.result)}catch{}clearTimeout(timer);timer=setTimeout(()=>finish(Error('Telemetry response timed out')),Math.max(10000,Number(r?.estTimeout)||0)+extraTimeout)};
    const onError=()=>finish(Error('Radio rejected the telemetry request'));
    const onResponse=r=>{const p=r?.pubKeyPrefix;if(!p||p.length!==6||!Array.from(p).every((v,i)=>v===publicKey[i]))return;finish(null,r)};
    conn.on(Constants.ResponseCodes.Sent,onSent);conn.on(Constants.ResponseCodes.Err,onError);conn.on(Constants.PushCodes.TelemetryResponse,onResponse);
    timer=setTimeout(()=>finish(Error('Radio did not acknowledge the telemetry request')),15000);
    Promise.resolve().then(()=>conn.sendCommandSendTelemetryReq(publicKey)).catch(e=>finish(e));
  });
}

// Call within one exclusive radio operation. A successful reset changes the
// saved route; the radio may learn a route again while login is in progress.
export async function requestFloodTelemetry(conn,publicKey,Constants,{password='',canSend=()=>true,onRoute=()=>{},extraTimeout=10000}={}){
 const check=()=>{if(!canSend())throw Error('Radio connection or monitored node changed; telemetry was not sent.')};
 const reset=async()=>{check();let timer;try{await Promise.race([conn.resetPath(publicKey),new Promise((_,reject)=>{timer=setTimeout(()=>reject(Error('Path reset timed out')),15000)})])}catch(error){throw Error('Could not clear the saved path; telemetry was not sent.',{cause:error})}finally{clearTimeout(timer)}check()};
 if(password){await reset();try{await conn.login(publicKey,password,10000)}catch(error){throw Error(`Repeater login failed or timed out (${error?.message||String(error)}). Check the password and radio path.`)}check()}
 await reset();return requestTelemetry(conn,publicKey,Constants,extraTimeout,onRoute);
}
