import http.client,tempfile,time,unittest
from pathlib import Path
from ble_transport import BLETransport
from bridge import CompanionBridge
from multi_board_hub import BoardHub
from test_ble_transport import FakeClient
class MultiBoardHubTests(unittest.TestCase):
 def test_frame_isolation_same_origin_and_token(self):
  with tempfile.TemporaryDirectory() as tmp:
   html=Path(tmp)/'radio.html';html.write_text('<html><head></head><body></body></html>');a=BLETransport('a',client_factory=FakeClient,timeout=1);b=BLETransport('b',client_factory=FakeClient,timeout=1);one=CompanionBridge(None,html,port=0,transport=a);two=CompanionBridge(None,html,port=0,transport=b);hub=BoardHub(html.read_bytes());ida='a'*32;idb='b'*32;hub.add(ida,one);hub.add(idb,two)
   def call(method,path,body=None,headers=None):
    c=http.client.HTTPConnection('127.0.0.1',hub.server.server_address[1],timeout=4);c.request(method,path,body=body,headers=headers or {});r=c.getresponse();data=r.read();code=r.status;c.close();return code,data
   try:
    code,page=call('GET',f'/board/{ida}/');self.assertEqual(code,200);self.assertIn(ida.encode(),page);self.assertIn(hub.token.encode(),page)
    a._client.callback(None,b'one');b._client.callback(None,b'two');end=time.monotonic()+3
    while (not one.frames or not two.frames) and time.monotonic()<end:time.sleep(.01)
    self.assertEqual(call('GET',f'/board/{ida}/api/frame')[0],403);headers={'X-MCC-Board-Token':hub.token};self.assertEqual(call('GET',f'/board/{ida}/api/frame',headers=headers),(200,b'one'));self.assertEqual(call('GET',f'/board/{idb}/api/frame',headers=headers),(200,b'two'))
    self.assertEqual(call('POST',f'/board/{ida}/api/frame',b'cmd',headers)[0],403);headers['Origin']=hub.origin;self.assertEqual(call('POST',f'/board/{ida}/api/frame',b'cmd',headers)[0],200);self.assertEqual(a._client.writes[0][1],b'cmd');self.assertEqual(b._client.writes,[])
   finally:hub.close();one.close();two.close()
if __name__=='__main__':unittest.main()
