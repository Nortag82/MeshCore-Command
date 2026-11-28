# JS8-R preview runtime sources

The headless worker is built from the C++ wrapper in this directory and the pinned JS8Call-improved source identified in [runtime-sources.json](runtime-sources.json). It runs in its own process. The C++ wrapper uses GPL-3.0-or-later. The pinned JS8Call source includes the original copyright notices, its licence and the bundled Eigen and CRCCpp code.

The Windows preview uses Qt Core, FFTW, GCC runtime libraries, winpthreads, ICU, PCRE2, Zstandard, zlib, libb2 and double-conversion from MSYS2 UCRT64. It builds with Boost headers. The manifest identifies the exact package versions and source archives. The GitHub prerelease provides the wrapper, upstream source and MSYS2 source packages in a corresponding-source archive alongside the installer. Each MSYS2 source package includes the build recipe and downstream patches. The installer includes licence files from the binary packages where provided. The matching source archives contain their upstream notices and licence texts. [MSYS2 source archive index](https://repo.msys2.org/mingw/sources/)

Qt Core is available under LGPL-3.0 and GPL terms. FFTW uses GPL terms. The GCC runtime files include the GCC Runtime Library Exception where applicable. Each component retains its original licence. [Qt licensing](https://doc.qt.io/qt-6/licensing.html) · [FFTW licensing](https://www.fftw.org/faq/section1.html)
