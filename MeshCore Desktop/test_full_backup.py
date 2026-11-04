import json,os,tempfile,unittest
from pathlib import Path
from unittest.mock import patch
import full_backup as b
class Tests(unittest.TestCase):
 def test_round_trip_and_authentication(self):
  with tempfile.TemporaryDirectory() as tmp,patch.dict(os.environ,{'LOCALAPPDATA':tmp}),patch.object(b.credential_store,'list_saved',return_value={'secure/'+'a'*64:'secret'}):
   root=b.data_dir();root.mkdir();(root/'devices.json').write_text('[]');m=Path(tmp)/'map.pmtiles';m.write_bytes(b'map');(root/'offline-map.json').write_text(json.dumps({'path':str(m)}))
   e=b.encrypt(b.snapshot({'messages':'private','rcc6-radio-owner':'lease'}),'long password 123');self.assertNotIn(b'private',e)
   with self.assertRaises(ValueError):b.decode(e,'wrong password 12')
   broken=bytearray(e);broken[-1]^=1
   with self.assertRaises(ValueError):b.decode(broken,'long password 123')
   meta,files=b.decode(e,'long password 123');self.assertEqual(meta['storage'],{'messages':'private'});self.assertEqual(files[meta['selected']],b'map');self.assertEqual(len(meta['credentials']),1)
   with patch.object(b.credential_store,'get',return_value=None),patch.object(b.credential_store,'put'),patch.object(b.credential_store,'delete'):b.apply(meta,files)
   self.assertTrue(Path(json.loads((root/'offline-map.json').read_text())['path']).exists())
 def test_radio_identity_is_encrypted_and_optional_in_older_backups(self):
  with tempfile.TemporaryDirectory() as tmp,patch.dict(os.environ,{'LOCALAPPDATA':tmp}),patch.object(b.credential_store,'list_saved',return_value={}):
   radio={'version':1,'privateKey':['sensitive-test-identity'],'channels':[]}
   encrypted=b.encrypt(b.snapshot({'test':'data'},radio),'long password 123');self.assertNotIn(b'sensitive-test-identity',encrypted)
   meta,_=b.decode(encrypted,'long password 123');self.assertEqual(meta['radio'],radio)
   old,_=b.decode(b.encrypt(b.snapshot({}),'long password 123'),'long password 123');self.assertIsNone(old['radio'])
   with self.assertRaises(ValueError):b.snapshot({}, {'version':9})
 def test_radio_backup_dialogs_preserve_identity_in_encrypted_file(self):
  import app
  with tempfile.TemporaryDirectory() as tmp,patch.dict(os.environ,{'LOCALAPPDATA':tmp}),patch.object(b.credential_store,'list_saved',return_value={}):
   path=Path(tmp)/'backup.mccfull'
   class Window:
    def create_file_dialog(self,*args,**kwargs):return [str(path)]
   api=app.API();export,prepare,commit,cancel,open_radio,recovery=api._full_backup_dialogs(Window());radio={'version':1,'privateKey':['identity-secret']};password='long password 123'
   self.assertEqual(export({'key':'value'},password,radio),{'ok':True});self.assertNotIn(b'identity-secret',path.read_bytes());self.assertEqual(open_radio(password)['radio'],radio)
   self.assertIn('error',open_radio('incorrect password'))
   result=recovery({'key':'value'},password,radio);self.assertTrue(Path(result['recovery']).is_file());self.assertNotIn(b'identity-secret',Path(result['recovery']).read_bytes())
 def test_rollback(self):
  with tempfile.TemporaryDirectory() as tmp,patch.dict(os.environ,{'LOCALAPPDATA':tmp}),patch.object(b.credential_store,'get',return_value=None),patch.object(b.credential_store,'put',side_effect=OSError('failure')),patch.object(b.credential_store,'delete'):
   root=b.data_dir();root.mkdir();(root/'devices.json').write_text('old')
   with self.assertRaises(OSError):b.apply({'credentials':{'secure/'+'a'*64:'secret'},'selected':None},{'devices.json':b'new'})
   self.assertEqual((root/'devices.json').read_text(),'old')
 def test_size_limit(self):
  with tempfile.TemporaryDirectory() as tmp,patch.dict(os.environ,{'LOCALAPPDATA':tmp}),patch.object(b.credential_store,'list_saved',return_value={}),patch.object(b,'LIMIT',4):
   root=b.data_dir()/'offline-maps';root.mkdir(parents=True);(root/'map.pmtiles').write_bytes(b'12345')
   with self.assertRaisesRegex(ValueError,'256 MB'):b.snapshot({})
if __name__=='__main__':unittest.main()
