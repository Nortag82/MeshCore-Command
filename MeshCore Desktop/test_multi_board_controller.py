import tempfile,unittest
from pathlib import Path
from multi_board_controller import MultiBoardController
class FakeBridge:
 def __init__(self,*args,**kwargs):self.alive=True;self.closed=False;self.server=type('Server',(),{'server_address':('127.0.0.1',1111)})()
 def close(self):self.closed=True;self.alive=False
class ControllerTests(unittest.TestCase):
 def test_duplicate_radio_identity_rejected_without_stopping_first(self):
  with tempfile.TemporaryDirectory() as d:
   root=Path(d);html=root/'radio';html.write_text('<head></head>');shell=root/'shell';shell.write_text('shell');c=MultiBoardController(html,shell,root/'profiles',bridge_class=FakeBridge,identity_reader=lambda _:b'x'*32)
   try:
    first=c.connect_wifi('192.168.1.1')['id']
    with self.assertRaisesRegex(ValueError,'already connected'):c.connect_wifi('192.168.1.2')
    self.assertTrue(c.registry.active(first).alive);self.assertEqual(sum(x['open'] for x in c.list_boards()['boards']),1)
   finally:c.close()
if __name__=='__main__':unittest.main()
