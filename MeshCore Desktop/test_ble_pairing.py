import asyncio,unittest
from types import SimpleNamespace as Obj
from ble_pairing import _pair_information,pair_meshcore_ble
class Kinds:PROVIDE_PIN=4
class Protection:ENCRYPTION_AND_AUTHENTICATION=3
class Status:PAIRED=0;ALREADY_PAIRED=3
class Event:
 def __init__(self,kind):self.pairing_kind=kind;self.pin=None
 def accept_with_pin(self,pin):self.pin=pin
class Custom:
 def __init__(self):self.handler=None;self.event=Event(4);self.removed=False
 def add_pairing_requested(self,h):self.handler=h;return 42
 def remove_pairing_requested(self,token):self.removed=token==42
 async def pair_with_protection_level_async(self,kind,level):self.handler(None,self.event);return Obj(status=0)
class PairingTests(unittest.IsolatedAsyncioTestCase):
 async def test_pin_request_and_verified_readback(self):
  c=Custom();info=Obj(id='test',pairing=Obj(is_paired=False,can_pair=True,custom=c))
  async def refresh(_):return Obj(pairing=Obj(is_paired=True,protection_level=3))
  await _pair_information(info,'123456',kinds=Kinds,protection=Protection,statuses=Status,refresh=refresh)
  self.assertEqual(c.event.pin,'123456');self.assertTrue(c.removed)
 async def test_weak_existing_pairing_is_not_removed(self):
  info=Obj(id='test',pairing=Obj(is_paired=True,protection_level=2))
  with self.assertRaisesRegex(RuntimeError,'without PIN authentication'):await _pair_information(info,'123456',kinds=Kinds,protection=Protection,statuses=Status,refresh=None)
 async def test_invalid_input_rejected_before_windows_access(self):
  with self.assertRaises(ValueError):await pair_meshcore_ble('bad','123456')
  with self.assertRaises(ValueError):await pair_meshcore_ble('AA:BB:CC:DD:EE:FF','bad')
if __name__=='__main__':unittest.main()
