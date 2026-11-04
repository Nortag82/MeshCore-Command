export async function setRadioLocation({getState,getConnection,exclusive,hex},latitude,longitude){
 const state=getState();if(!state.connected||!state.self?.publicKey)throw Error('Connect your radio before saving a location.');
 if(!Number.isFinite(latitude)||!Number.isFinite(longitude)||Math.abs(latitude)>90||Math.abs(longitude)>180)throw Error('Choose a valid latitude and longitude.');
 const lat=Math.round(latitude*1000000),lon=Math.round(longitude*1000000);if(!lat&&!lon)throw Error('Choose a location other than 0, 0; the radio treats that as unset.');
 const radio=getConnection(),identity=hex(state.self.publicKey);
 await exclusive(async()=>{if(!state.connected||getConnection()!==radio||hex(state.self?.publicKey||[])!==identity)throw Error('Radio connection changed.');await radio.setAdvertLatLong(lat,lon);const current=await radio.getSelfInfo(6000);if(getConnection()!==radio||!state.connected||hex(current.publicKey)!==identity)throw Error('Radio connection changed; location could not be confirmed.');state.self=current;if(!Number.isFinite(current.advLat)||!Number.isFinite(current.advLon)||Math.abs(current.advLat-lat)>1||Math.abs(current.advLon-lon)>1)throw Error('Radio reported a different location. Check the radio settings.');});
}
