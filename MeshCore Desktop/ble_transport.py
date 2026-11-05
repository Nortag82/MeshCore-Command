"""Experimental MeshCore BLE transport; not connected to the desktop UI yet.

BLE carries one raw companion packet per GATT operation. This adapter presents
those packets as the framed byte stream expected by CompanionBridge. It never
retries a write whose outcome is uncertain.
"""
import asyncio
import concurrent.futures
import queue
import struct
import threading

SERVICE_UUID = '6e400001-b5a3-f393-e0a9-e50e24dcca9e'
WRITE_UUID = '6e400002-b5a3-f393-e0a9-e50e24dcca9e'
NOTIFY_UUID = '6e400003-b5a3-f393-e0a9-e50e24dcca9e'
MAX_FRAME = 176


class BLETransport:
    """A synchronous framed-stream facade over one asynchronous GATT client."""
    def __init__(self, device, *, client_factory=None, timeout=45, queue_size=512):
        if client_factory is None:
            try:
                from bleak import BleakClient
            except ImportError as error:
                raise RuntimeError('Bluetooth support requires the bleak package.') from error
            client_factory = BleakClient
        self._device = device
        self._factory = client_factory
        self._timeout = timeout
        self._frames = queue.Queue(maxsize=queue_size)
        self._buffer = bytearray()
        self._closed = threading.Event()
        self._thread_ready = threading.Event()
        self._loop = asyncio.new_event_loop()
        self._client = None
        self._write_lock = threading.Lock()
        self._thread = threading.Thread(target=self._run, name='meshcore-ble', daemon=True)
        self._thread.start()
        self._thread_ready.wait()
        try:
            self._submit(self._connect(), timeout)
        except Exception:
            self.close()
            raise

    def _run(self):
        asyncio.set_event_loop(self._loop)
        self._thread_ready.set()
        try:
            self._loop.run_forever()
        finally:
            pending = asyncio.all_tasks(self._loop)
            for task in pending: task.cancel()
            if pending: self._loop.run_until_complete(asyncio.gather(*pending, return_exceptions=True))
            self._loop.close()

    def _submit(self, coro, timeout):
        if self._closed.is_set():
            coro.close()
            raise OSError('Bluetooth connection is closed.')
        future = asyncio.run_coroutine_threadsafe(coro, self._loop)
        try:
            return future.result(timeout)
        except concurrent.futures.TimeoutError as error:
            future.cancel()
            self._signal_closed()
            raise OSError('Bluetooth operation timed out; delivery is uncertain.') from error
        except Exception as error:
            raise OSError(str(error) or 'Bluetooth operation failed.') from error

    async def _connect(self):
        self._client = self._factory(self._device, disconnected_callback=self._disconnected, pair=False, timeout=self._timeout)
        await self._client.connect()
        services = self._client.services
        if services.get_service(SERVICE_UUID) is None or services.get_characteristic(WRITE_UUID) is None or services.get_characteristic(NOTIFY_UUID) is None:
            raise OSError('This device does not expose the MeshCore companion Bluetooth service.')
        await self._client.start_notify(NOTIFY_UUID, self._notification)
        # Windows can report the default MTU briefly while the negotiated size
        # is still arriving. Give the session time to update before rejecting it.
        deadline=asyncio.get_running_loop().time()+min(8,self._timeout)
        while self._client.mtu_size < 100 and self._client.is_connected and asyncio.get_running_loop().time()<deadline:
            await asyncio.sleep(.2)
        if self._client.mtu_size < 100:
            raise OSError('Bluetooth negotiated an MTU too small for MeshCore companion frames.')
        if self._closed.is_set() or not self._client.is_connected:
            raise OSError('Bluetooth disconnected during setup.')

    def _signal_closed(self):
        self._closed.set()
        # A blocked reader must always be released, even after a queue overflow.
        try: self._frames.put_nowait(None)
        except queue.Full:
            try: self._frames.get_nowait()
            except queue.Empty: pass
            try: self._frames.put_nowait(None)
            except queue.Full: pass

    def _disconnected(self, _client):
        self._signal_closed()

    def _notification(self, _characteristic, data):
        frame = bytes(data)
        if self._closed.is_set(): return
        if not 1 <= len(frame) <= MAX_FRAME:
            self._signal_closed()
            return
        try: self._frames.put_nowait(b'>' + struct.pack('<H', len(frame)) + frame)
        except queue.Full: self._signal_closed()

    def settimeout(self, _timeout):
        pass

    def recv(self, count):
        if count <= 0 or self._closed.is_set(): return b''
        while not self._buffer:
            if self._closed.is_set() and self._frames.empty(): return b''
            item = self._frames.get()
            if item is None: return b''
            self._buffer.extend(item)
        result = bytes(self._buffer[:count])
        del self._buffer[:count]
        return result

    def sendall(self, data):
        packet = bytes(data)
        if len(packet) < 4 or packet[0] != ord('<'):
            raise OSError('Invalid companion stream frame.')
        length = struct.unpack('<H', packet[1:3])[0]
        if not 1 <= length <= MAX_FRAME or len(packet) != length + 3:
            raise OSError('Invalid companion stream frame length.')
        with self._write_lock:
            if self._closed.is_set() or not self._client or not self._client.is_connected:
                raise OSError('Bluetooth connection is closed.')
            if length > self._client.mtu_size - 3:
                raise OSError('Companion frame exceeds the negotiated Bluetooth MTU.')
            self._submit(self._client.write_gatt_char(WRITE_UUID, packet[3:], response=True), self._timeout)

    def shutdown(self, _how=None):
        self.close()

    def close(self):
        self._signal_closed()
        if self._loop.is_running():
            if self._client is not None and self._client.is_connected:
                future = asyncio.run_coroutine_threadsafe(self._client.disconnect(), self._loop)
                try: future.result(min(self._timeout, 5))
                except Exception: future.cancel()
            self._loop.call_soon_threadsafe(self._loop.stop)
        if threading.current_thread() is not self._thread:
            self._thread.join(timeout=6)
