import json,tempfile,unittest
from pathlib import Path
from unittest.mock import patch
from board_registry import BoardRegistry

class Bridge:
 def __init__(self,profile,device):self.profile=profile;self.device=device;self.alive=True;self.closed=False
 def close(self):self.closed=True;self.alive=False
class BoardRegistryTests(unittest.TestCase):
 def setUp(self):self.temp=tempfile.TemporaryDirectory();self.path=Path(self.temp.name)/'boards.json';self.hub=BoardRegistry(self.path,Bridge)
 def tearDown(self):self.hub.close();self.temp.cleanup()
 def test_profiles_have_stable_ids_and_names(self):
  a=self.hub.add('wifi','192.168.1.5');b=self.hub.add('ble','AA:BB:CC:DD:EE:FF');self.hub.rename(a['id'],'Home')
  self.assertEqual([x['name'] for x in BoardRegistry(self.path,Bridge).list()],['Home','Board 2']);self.assertEqual(BoardRegistry(self.path,Bridge).list()[0]['id'],a['id'])
  with self.assertRaises(ValueError):self.hub.add('wifi','192.168.1.5')
  with self.assertRaises(ValueError):self.hub.add('ble','aa:bb:cc:dd:ee:ff')
  with self.assertRaises(ValueError):self.hub.add('wifi','not-an-ip')
  with self.assertRaises(ValueError):self.hub.rename(b['id'],'')
  self.assertEqual(self.hub.list()[1]['name'],'Board 2')
 def test_connections_remain_independent(self):
  a=self.hub.add('wifi','192.168.1.5');b=self.hub.add('ble','AA:BB:CC:DD:EE:FF');x=self.hub.connect(a['id']);y=self.hub.connect(b['id'],device='scan-result')
  self.assertEqual([p['connected'] for p in self.hub.list()],[True,True]);self.hub.disconnect(a['id']);self.assertTrue(x.closed);self.assertFalse(y.closed);self.assertTrue(self.hub.list()[1]['connected'])
  with self.assertRaises(ValueError):self.hub.connect(b['id'])
 def test_failed_save_rolls_back_in_memory(self):
  self.hub.add('wifi','192.168.1.5')
  with patch('board_registry.os.replace',side_effect=OSError('disk full')):
   with self.assertRaises(OSError):self.hub.add('ble','AA:BB:CC:DD:EE:FF')
  self.assertEqual(len(self.hub.list()),1);self.assertEqual(len(json.loads(self.path.read_text())['boards']),1)
if __name__=='__main__':unittest.main()
