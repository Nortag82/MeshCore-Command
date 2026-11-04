import unittest
from unittest.mock import MagicMock, patch
import installer


class InstallerUpdateTests(unittest.TestCase):
    def test_waits_then_installs_and_restarts(self):
        kernel=MagicMock();kernel.OpenProcess.return_value=123;kernel.WaitForSingleObject.return_value=0
        tasklist=MagicMock(stdout='INFO: No tasks are running which match the specified criteria.\n')
        with patch.object(installer.ctypes,'WinDLL',return_value=kernel),patch.object(installer.subprocess,'run',return_value=tasklist),patch.object(installer,'install_payload') as install,patch.object(installer.os,'startfile') as start:
            installer.update_after_exit(42)
            kernel.WaitForSingleObject.assert_called_once_with(123,60000)
            install.assert_called_once()
            start.assert_called_once()

    def test_other_app_instance_prevents_install(self):
        kernel=MagicMock();kernel.OpenProcess.return_value=123;kernel.WaitForSingleObject.return_value=0
        tasklist=MagicMock(stdout='"MeshCore Command.exe","99","Console","1","10 K"\n')
        with patch.object(installer.ctypes,'WinDLL',return_value=kernel),patch.object(installer.subprocess,'run',return_value=tasklist),patch.object(installer,'install_payload') as install,patch.object(installer.time,'sleep'):
            with self.assertRaisesRegex(RuntimeError,'another|Another'):installer.update_after_exit(42)
            install.assert_not_called()


if __name__=='__main__':unittest.main()
