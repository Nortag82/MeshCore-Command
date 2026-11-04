import time
import unittest
import app


class SplashTests(unittest.TestCase):
    def test_splash_is_separate_and_exits_on_parent_signal(self):
        process = app.start_splash_process()
        self.assertIsNotNone(process)
        try:
            time.sleep(2)
            self.assertIsNone(process.poll(), 'Splash exited before the parent signalled readiness')
            app.stop_splash_process(process)
            self.assertEqual(process.wait(timeout=10), 0)
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=10)


if __name__ == '__main__':
    unittest.main()
