// Must run inside the same exclusive radio operation as the send.
// Resetting the route changes the contact's saved path on the radio.
export async function sendFloodMessage(radio,publicKey,message,{type,canSend=()=>true,onRouteWarning=()=>{}}={}){
 if(!radio||typeof radio.resetPath!=='function'||typeof radio.sendTextMessage!=='function'||!(publicKey instanceof Uint8Array)||publicKey.length!==32)throw Error('Cannot prepare flood routing for this contact.');
 if(!canSend())throw Error('Radio or recipient changed; message was not sent.');
 try{await radio.resetPath(publicKey)}catch(error){throw Error('Could not clear the saved route; message was not sent.',{cause:error})}
 if(!canSend())throw Error('Radio or recipient changed; message was not sent.');
 const response=await radio.sendTextMessage(publicKey,message,type);
 if(response?.result!==1)onRouteWarning(response?.result===0?'The radio sent using a direct route, not flood.':'The radio did not report whether this message used flood routing.');
 return response;
}
