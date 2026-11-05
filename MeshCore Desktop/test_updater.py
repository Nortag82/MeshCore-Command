import hashlib
import io
import json
import unittest
from unittest.mock import patch
import updater
import app


def release(version='2.0.21', content=b'installer'):
    name=f'MeshCore-Command-Windows-{version}-Setup.exe'
    return {'tag_name':'v'+version,'draft':False,'published_at':'2026-01-01T00:00:00Z','prerelease':True,'assets':[{'name':name,'state':'uploaded','digest':'sha256:'+hashlib.sha256(content).hexdigest(),'size':len(content),'browser_download_url':f'https://github.com/Nortag82/MeshCore-Command/releases/download/v{version}/{name}'}]}


class Response(io.BytesIO):
    def __enter__(self): return self
    def __exit__(self, *args): self.close()


class UpdaterTests(unittest.TestCase):
    def test_highest_release_and_no_downgrade(self):
        self.assertEqual(updater.select_release([release('2.0.21'),release('2.1.0')])['version'],'2.1.0')
        self.assertIsNone(updater.select_release([release('2.0.19'),release('2.0.20')]))

    def test_untrusted_url_missing_digest_and_duplicate_are_rejected(self):
        for mutate in ('url','digest','duplicate'):
            r=release();asset=r['assets'][0]
            if mutate=='url': asset['browser_download_url']='https://example.com/installer.exe'
            if mutate=='digest': asset['digest']=''
            if mutate=='duplicate': r['assets'].append(dict(asset))
            with self.subTest(mutate=mutate),self.assertRaises(ValueError):updater.select_release([r])

    def test_download_checks_bytes_and_revalidates_version(self):
        data=b'installer';r=release(content=data)
        class Opener:
            def open(self,req,*args,**kwargs):return Response(json.dumps([r]).encode() if req.full_url==updater.API else data)
        with patch.object(updater.urllib.request,'build_opener',return_value=Opener()):
            path=updater.Path(updater.download('2.0.21'))
            try:self.assertEqual(path.read_bytes(),data)
            finally:path.unlink();path.parent.rmdir()
            with self.assertRaises(ValueError):updater.download('2.0.20')
        r['assets'][0]['digest']='sha256:'+'0'*64
        with patch.object(updater.urllib.request,'build_opener',return_value=Opener()):
            with self.assertRaisesRegex(ValueError,'SHA-256'):updater.download('2.0.21')

    def test_redirect_rejects_untrusted_host(self):
        h=updater.SafeRedirect()
        with self.assertRaises(ValueError):h.redirect_request(None,None,302,'',{},'https://example.com/setup.exe')

    def test_startup_prompt_waits_for_radio_or_busy_operations(self):
        api=app.API()
        self.assertEqual(api.can_prompt_update(),{'allowed':True})
        api._state['busy']=True;self.assertFalse(api.can_prompt_update()['allowed'])
        api._state['busy']=False;api._radio_window=object();self.assertFalse(api.can_prompt_update()['allowed'])
        api._radio_window=None
        with api._radio_open_lock:self.assertFalse(api.can_prompt_update()['allowed'])
        api._update_started=True;self.assertFalse(api.can_prompt_update()['allowed'])

    def test_update_handoff_blocks_busy_operations_and_duplicates(self):
        api=app.API()
        with patch.object(app.sys,'frozen',True,create=True),patch.object(app.updater,'download',return_value='C:\\temp\\installer.exe') as download,patch.object(app.subprocess,'Popen') as launch,patch.object(app.threading,'Timer') as timer:
            api._state['busy']=True
            self.assertIn('error',api.install_app_update('2.0.21'))
            download.assert_not_called();launch.assert_not_called()
            api._state['busy']=False
            self.assertEqual(api.install_app_update('2.0.21'),{'ok':True})
            launch.assert_called_once();timer.assert_called_once()
            self.assertIn('error',api.install_app_update('2.0.21'))


if __name__=='__main__':unittest.main()
