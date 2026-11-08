"""Read a fresh, checksum-verified position from a serial NMEA GPS receiver."""
import math
import time
import serial

def parse_sentence(raw):
    try:
        text = raw.decode('ascii').strip()
        body, checksum = text[1:].rsplit('*', 1)
        if not text.startswith('$') or len(checksum) != 2: return None
        check = 0
        for char in body: check ^= ord(char)
        if check != int(checksum, 16): return None
        f = body.split(','); kind = f[0][-3:]
        if kind == 'RMC' and f[2] == 'A': lat, ns, lon, ew = f[3:7]
        elif kind == 'GGA' and int(f[6]) > 0: lat, ns, lon, ew = f[2:6]
        else: return None
        def coord(value, hemi, width):
            deg, minutes = int(value[:width]), float(value[width:])
            if not math.isfinite(minutes) or not 0 <= minutes < 60: raise ValueError()
            return (deg + minutes / 60) * (-1 if hemi in ('S','W') else 1)
        if ns not in ('N','S') or ew not in ('E','W'): return None
        latitude, longitude = coord(lat,ns,2),coord(lon,ew,3)
        if abs(latitude)>85 or abs(longitude)>180: return None
        return {'latitude':latitude,'longitude':longitude}
    except (ValueError,IndexError,UnicodeDecodeError): return None

def read_position(port, baud, timeout=8):
    deadline=time.monotonic()+timeout
    with serial.Serial(port, baud, timeout=0.5, dsrdtr=False, rtscts=False) as receiver:
        receiver.reset_input_buffer()
        while time.monotonic()<deadline:
            result=parse_sentence(receiver.readline(256))
            if result is not None: return result
    raise TimeoutError('No valid GPS fix received. Check the receiver, baud rate and sky view.')
