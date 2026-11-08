import unittest
from unittest.mock import patch
import gps_nmea

def sentence(body):
    check=0
    for char in body: check ^= ord(char)
    return f'${body}*{check:02X}\r\n'.encode()

class GPSParsingTests(unittest.TestCase):
    def test_rmc_and_gga_position(self):
        for body in ('GPRMC,123519,A,5601.200,N,00445.600,W,0,0,010101,,,A', 'GNGGA,123519,5601.200,N,00445.600,W,1,08,1,10,M,0,M,,'):
            result=gps_nmea.parse_sentence(sentence(body))
            self.assertAlmostEqual(result['latitude'],56.02)
            self.assertAlmostEqual(result['longitude'],-4.76)
    def test_reject_bad_data(self):
        for raw in (b'$GPRMC,123519,A,5601.200,N,00445.600,W*00', sentence('GPRMC,123519,V,5601.200,N,00445.600,W'), sentence('GPGGA,1,5601.2,N,00445.6,W,0'), sentence('GPRMC,1,A,5661.0,N,00445.6,W')):
            self.assertIsNone(gps_nmea.parse_sentence(raw))
    def test_reads_fresh_input(self):
        class Device:
            def __enter__(self): return self
            def __exit__(self,*args): pass
            def reset_input_buffer(self): self.reset=True
            def readline(self,n): return sentence('GPRMC,123519,A,5601.200,N,00445.600,W')
        device=Device()
        with patch.object(gps_nmea.serial,'Serial',return_value=device):
            self.assertAlmostEqual(gps_nmea.read_position('COM4',9600)['latitude'],56.02)
        self.assertTrue(device.reset)

if __name__=='__main__': unittest.main()
