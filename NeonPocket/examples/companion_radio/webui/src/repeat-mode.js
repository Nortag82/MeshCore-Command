// MeshCore companion repeat uses CMD_SET_RADIO_PARAMS with an optional flag.
// Frequencies and bandwidth are in kHz and Hz respectively, as reported by the companion protocol.
export function parseRepeatRanges(frame){
 if(!(frame instanceof Uint8Array)||frame[0]!==26||frame.length<9||(frame.length-1)%8!==0)throw Error('The radio returned an invalid allowed-frequency list.');
 const view=new DataView(frame.buffer,frame.byteOffset,frame.byteLength),ranges=[];
 for(let i=1;i<frame.length;i+=8){const low=view.getUint32(i,true),high=view.getUint32(i+4,true);if(low>high||low<150000||high>2500000)throw Error('The radio returned an invalid allowed-frequency range.');ranges.push({low,high})}return ranges;
}
export function buildRepeatParams(self,enabled){
 if(!self||![self.radioFreq,self.radioBw,self.radioSf,self.radioCr].every(Number.isInteger)||self.radioFreq<150000||self.radioFreq>2500000||self.radioBw<7000||self.radioBw>500000||self.radioSf<5||self.radioSf>12||self.radioCr<5||self.radioCr>8)throw Error('The radio did not report valid current radio settings.');
 const bytes=new Uint8Array(12),view=new DataView(bytes.buffer);bytes[0]=11;view.setUint32(1,self.radioFreq,true);view.setUint32(5,self.radioBw,true);bytes[9]=self.radioSf;bytes[10]=self.radioCr;bytes[11]=enabled?1:0;return bytes;
}
export function repeatFrame(radio,frame,response,timeout=8000){
 return new Promise((resolve,reject)=>{let done=false,timer;const finish=(error,value)=>{if(done)return;done=true;clearTimeout(timer);radio.off('rx',onFrame);error?reject(error):resolve(value)};const onFrame=data=>{if(data[0]===1)return finish(Error('The radio rejected the companion-repeat request.'));if(data[0]===response)finish(null,data)};radio.on('rx',onFrame);timer=setTimeout(()=>finish(Error('The radio did not respond to the companion-repeat request.')),timeout);Promise.resolve().then(()=>radio.sendToRadioFrame(frame)).catch(finish)});
}
export async function readRepeatEligibility(radio,protocolVersion){
 if(!Number.isInteger(protocolVersion)||protocolVersion<0||protocolVersion>255)throw Error('Invalid companion protocol version.');const device=await repeatFrame(radio,new Uint8Array([22,protocolVersion]),13);if(device.length<2||device[1]<9)throw Error('This radio does not report the companion-repeat protocol (version 9 or later required).');
 const self=await radio.getSelfInfo(6000);buildRepeatParams(self,false);const ranges=parseRepeatRanges(await repeatFrame(radio,new Uint8Array([60]),26));return {self,ranges,allowed:ranges.some(x=>self.radioFreq>=x.low&&self.radioFreq<=x.high)};
}
export async function setCompanionRepeat(radio,enabled,protocolVersion){
 const info=await readRepeatEligibility(radio,protocolVersion);if(enabled&&!info.allowed)throw Error(`Current frequency ${(info.self.radioFreq/1000).toFixed(3)} MHz is not permitted for companion repeat by this radio.`);
 await repeatFrame(radio,buildRepeatParams(info.self,enabled),0);return info;
}
