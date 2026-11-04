import http.client, socket, struct, tempfile, time, unittest
from pathlib import Path
from unittest.mock import patch
import bridge

def wait_for(predicate, timeout=8):
    end=time.monotonic()+timeout
    while time.monotonic()<end:
        if predicate(): return
        time.sleep(.05)
    raise AssertionError('Timed out waiting for reconnect')

class RecoveryTests(unittest.TestCase):
    def test_reconnect_retains_frames_without_replaying_send(self):
        server=socket.socket();server.bind(('127.0.0.1',0));server.listen();server.settimeout(8)
        original=socket.create_connection
        def connect(address,timeout=5,*args,**kwargs):
            return original(server.getsockname() if address==('test-radio',5000) else address,timeout,*args,**kwargs)
        with tempfile.TemporaryDirectory() as tmp, patch.object(bridge.socket,'create_connection',side_effect=connect):
            html=Path(tmp)/'index.html';html.write_text('test')
            b=bridge.CompanionBridge('test-radio',html,port=0)
            try:
                first,_=server.accept();first.settimeout(2)
                def get(ack=None):
                    conn=http.client.HTTPConnection('127.0.0.1',b.server.server_address[1],timeout=3)
                    conn.request('GET','/api/frame',headers={'X-RCC6-Ack':str(ack)} if ack else {})
                    res=conn.getresponse();data=res.read();result=(res.status,res.getheader('X-RCC6-Seq'),data);conn.close();return result
                first.sendall(b'>'+struct.pack('<H',3)+b'one')
                wait_for(lambda:len(b.frames)==1)
                status,seq,data=get();self.assertEqual((status,data),(200,b'one'))
                first.shutdown(socket.SHUT_RDWR);first.close()
                wait_for(lambda:not b.alive)
                self.assertEqual(get()[0],503)
                second,_=server.accept();second.settimeout(.4);wait_for(lambda:b.alive)
                self.assertEqual(get(),(200,seq,b'one'))
                self.assertEqual(get(seq)[0],204)
                second.sendall(b'>'+struct.pack('<H',3)+b'two')
                wait_for(lambda:len(b.frames)==1)
                self.assertEqual(get()[2],b'two')
                with self.assertRaises(socket.timeout):second.recv(1)
                second.close()
            finally:
                b.close();server.close()

    def test_close_stops_retry(self):
        server=socket.socket();server.bind(('127.0.0.1',0));server.listen();server.settimeout(3)
        original=socket.create_connection;calls=[]
        def connect(address,timeout=5,*args,**kwargs):
            if address==('test-radio',5000):calls.append(1);return original(server.getsockname(),timeout)
            return original(address,timeout,*args,**kwargs)
        with tempfile.TemporaryDirectory() as tmp, patch.object(bridge.socket,'create_connection',side_effect=connect):
            html=Path(tmp)/'index.html';html.write_text('test');b=bridge.CompanionBridge('test-radio',html,port=0)
            peer,_=server.accept();peer.shutdown(socket.SHUT_RDWR);peer.close();wait_for(lambda:not b.alive)
            b.close();time.sleep(1.3);self.assertEqual(len(calls),1);server.close()

if __name__=='__main__':unittest.main()
