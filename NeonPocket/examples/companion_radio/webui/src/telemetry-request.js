export function requestTelemetry(conn, publicKey, Constants, extraTimeout=10000) {
  return new Promise((resolve,reject)=>{
    let timer, settled=false;
    const clean=()=>{clearTimeout(timer);conn.off(Constants.ResponseCodes.Sent,onSent);conn.off(Constants.ResponseCodes.Err,onError);conn.off(Constants.PushCodes.TelemetryResponse,onResponse)};
    const finish=(err,value)=>{if(settled)return;settled=true;clean();err?reject(err):resolve(value)};
    const onSent=r=>{conn.off(Constants.ResponseCodes.Sent,onSent);clearTimeout(timer);timer=setTimeout(()=>finish(Error('Telemetry response timed out')),Math.max(10000,Number(r?.estTimeout)||0)+extraTimeout)};
    const onError=()=>finish(Error('Radio rejected the telemetry request'));
    const onResponse=r=>{const p=r?.pubKeyPrefix;if(!p||p.length!==6||!Array.from(p).every((v,i)=>v===publicKey[i]))return;finish(null,r)};
    conn.on(Constants.ResponseCodes.Sent,onSent);conn.on(Constants.ResponseCodes.Err,onError);conn.on(Constants.PushCodes.TelemetryResponse,onResponse);
    timer=setTimeout(()=>finish(Error('Radio did not acknowledge the telemetry request')),15000);
    Promise.resolve().then(()=>conn.sendCommandSendTelemetryReq(publicKey)).catch(e=>finish(e));
  });
}
