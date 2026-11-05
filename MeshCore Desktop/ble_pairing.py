"""Experimental authenticated Windows pairing for MeshCore BLE companions."""
import asyncio
import re


async def _pair_information(info, pin, *, kinds, protection, statuses, refresh):
    if info.pairing.is_paired:
        if info.pairing.protection_level != protection.ENCRYPTION_AND_AUTHENTICATION:
            raise RuntimeError('Windows has a pairing without PIN authentication. Remove it in Windows Bluetooth settings, then pair again.')
        return
    if not isinstance(pin,str) or not re.fullmatch(r'[0-9]{6}',pin):
        raise ValueError('Enter the six-digit PIN shown by the board.')
    if not info.pairing.can_pair:
        raise RuntimeError('Windows cannot pair this device. Make sure it is powered on and discoverable.')
    custom=info.pairing.custom
    def requested(_sender,args):
        # Only accept an explicit PIN request. Never silently downgrade to Just Works.
        if args.pairing_kind == kinds.PROVIDE_PIN:
            args.accept_with_pin(pin)
    token=custom.add_pairing_requested(requested)
    try:
        result=await custom.pair_with_protection_level_async(kinds.PROVIDE_PIN,protection.ENCRYPTION_AND_AUTHENTICATION)
    finally:
        custom.remove_pairing_requested(token)
    if result.status not in (statuses.PAIRED,statuses.ALREADY_PAIRED):
        raise RuntimeError(f'Windows Bluetooth pairing failed (status {result.status}).')
    # Windows may return a stale protection_level_used in PairingResult; refresh
    # the device information and verify the actual pairing protection instead.
    verified=await refresh(info.id)
    if not verified.pairing.is_paired or verified.pairing.protection_level!=protection.ENCRYPTION_AND_AUTHENTICATION:
        raise RuntimeError('Windows did not confirm an authenticated PIN pairing.')


async def pair_meshcore_ble(address, pin, timeout=60):
    if not isinstance(address,str) or not re.fullmatch(r'(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}',address):
        raise ValueError('Invalid Bluetooth device address.')
    if not isinstance(pin,str) or (pin and not re.fullmatch(r'[0-9]{6}',pin)):
        raise ValueError('Enter the six-digit PIN shown by the board.')
    try:
        from winrt.windows.devices.bluetooth import BluetoothLEDevice
        from winrt.windows.devices.enumeration import DeviceInformation,DevicePairingKinds,DevicePairingProtectionLevel,DevicePairingResultStatus
    except ImportError as error:
        raise RuntimeError('Windows Bluetooth support is not installed.') from error
    device=await BluetoothLEDevice.from_bluetooth_address_async(int(address.replace(':',''),16))
    if not device:raise RuntimeError('Bluetooth board is unavailable.')
    try:
        info=await DeviceInformation.create_from_id_async(device.device_information.id)
        await asyncio.wait_for(_pair_information(info,pin,kinds=DevicePairingKinds,protection=DevicePairingProtectionLevel,statuses=DevicePairingResultStatus,refresh=DeviceInformation.create_from_id_async),timeout)
    finally:
        device.close()
