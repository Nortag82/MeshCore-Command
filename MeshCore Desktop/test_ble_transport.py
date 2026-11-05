import struct
import threading
import unittest
import http.client
import socket
import tempfile
import time
from pathlib import Path
from unittest.mock import patch
import bridge
from ble_transport import BLETransport, SERVICE_UUID, WRITE_UUID, NOTIFY_UUID

class Services:
    def get_service(self, uuid): return object() if uuid == SERVICE_UUID else None
    def get_characteristic(self, uuid): return object() if uuid in (WRITE_UUID, NOTIFY_UUID) else None
class FakeClient:
    def __init__(self, device, disconnected_callback, pair, timeout):
        self.device=device;self.disconnected_callback=disconnected_callback;self.pair=pair;self.services=Services();self.mtu_size=185;self.is_connected=False;self.writes=[];self.callback=None
    async def connect(self):self.is_connected=True
    async def start_notify(self, uuid, callback):
        if uuid!=NOTIFY_UUID:raise AssertionError('Wrong notify UUID')
        self.callback=callback
    async def write_gatt_char(self,uuid,data,response):self.writes.append((uuid,bytes(data),response))
    async def disconnect(self):self.is_connected=False
class BLETransportTests(unittest.TestCase):
    def setUp(self):self.transport=BLETransport('test',client_factory=FakeClient,timeout=1)
    def tearDown(self):self.transport.close()
    def test_pairing_and_bidirectional_packet_framing(self):
        t=self.transport;self.assertFalse(t._client.pair);t._client.callback(None,bytearray(b'\x07hello'))
        self.assertEqual(t.recv(1),b'>');self.assertEqual(t.recv(2),struct.pack('<H',6));self.assertEqual(t.recv(6),b'\x07hello')
        t.sendall(b'<\x03\x00abc');self.assertEqual(t._client.writes,[(WRITE_UUID,b'abc',True)])
    def test_invalid_and_oversized_frames_are_rejected_without_write(self):
        t=self.transport
        for payload in (b'',b'<\x04\x00abc',b'>\x03\x00abc',b'<\x00\x00'):
            with self.subTest(payload=payload),self.assertRaises(OSError):t.sendall(payload)
        t._client.mtu_size=100
        with self.assertRaisesRegex(OSError,'MTU'):t.sendall(b'<'+struct.pack('<H',98)+b'x'*98)
        self.assertEqual(t._client.writes,[])
    def test_disconnect_releases_blocked_reader(self):
        out=[];reader=threading.Thread(target=lambda:out.append(self.transport.recv(1)));reader.start();self.transport._client.disconnected_callback(self.transport._client);reader.join(2)
        self.assertFalse(reader.is_alive());self.assertEqual(out,[b''])
    def test_queue_overflow_closes_instead_of_silently_losing_packets(self):
        t=BLETransport('other',client_factory=FakeClient,timeout=1,queue_size=1)
        try:
            t._client.callback(None,b'a');t._client.callback(None,b'b');self.assertEqual(t.recv(1),b'')
            with self.assertRaises(OSError):t.sendall(b'<\x01\x00a')
        finally:t.close()
class TimeoutClient(FakeClient):
    async def write_gatt_char(self,uuid,data,response):
        import asyncio
        self.writes.append((uuid,bytes(data),response))
        await asyncio.sleep(2)

class TimeoutTests(unittest.TestCase):
    def test_uncertain_write_is_not_retried_and_closes_transport(self):
        t=BLETransport('slow',client_factory=TimeoutClient,timeout=.05)
        try:
            with self.assertRaisesRegex(OSError,'uncertain'):t.sendall(b'<\x03\x00abc')
            self.assertEqual(t._client.writes,[(WRITE_UUID,b'abc',True)])
            with self.assertRaises(OSError):t.sendall(b'<\x03\x00abc')
            self.assertEqual(t.recv(1),b'')
        finally:t.close()

class ConcurrentBridgeTests(unittest.TestCase):
    def test_wifi_and_bluetooth_frames_remain_isolated(self):
        server=socket.socket();server.bind(('127.0.0.1',0));server.listen();server.settimeout(4)
        original=socket.create_connection
        def connect(address,timeout=5,*args,**kwargs):return original(server.getsockname() if address==('test-wifi',5000) else address,timeout,*args,**kwargs)
        with tempfile.TemporaryDirectory() as tmp,patch.object(bridge.socket,'create_connection',side_effect=connect):
            html=Path(tmp)/'index.html';html.write_text('test');ble=BLETransport('fake',client_factory=FakeClient,timeout=1)
            wifi=bridge.CompanionBridge('test-wifi',html,port=0);peer,_=server.accept();bluetooth=bridge.CompanionBridge(None,html,port=0,transport=ble)
            try:
                peer.sendall(b'>\x04\x00wifi');ble._client.callback(None,b'ble')
                end=time.monotonic()+3
                while (not wifi.frames or not bluetooth.frames) and time.monotonic()<end:time.sleep(.01)
                self.assertEqual(wifi.frames[0][1],b'wifi');self.assertEqual(bluetooth.frames[0][1],b'ble')
                for target,payload in ((wifi,b'one'),(bluetooth,b'two')):
                    conn=http.client.HTTPConnection('127.0.0.1',target.server.server_address[1],timeout=3);conn.request('POST','/api/frame',body=payload);response=conn.getresponse();self.assertEqual(response.status,200);response.read();conn.close()
                self.assertEqual(peer.recv(6),b'<\x03\x00one');self.assertEqual(ble._client.writes,[(WRITE_UUID,b'two',True)])
            finally:bluetooth.close();wifi.close();peer.close();server.close()

if __name__=='__main__':unittest.main()
