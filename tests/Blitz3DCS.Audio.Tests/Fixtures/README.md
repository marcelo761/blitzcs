`generated-tone.ogg` is an original, synthetic 440 Hz sine tone generated with FFmpeg's
`sine` audio source and libvorbis encoder. It contains no sampled or copyrighted
recording and is dedicated to the public domain (CC0-1.0) for decoder tests.

The test WAV is generated directly from PCM samples in `AudioDecoderTests`.

The decoder uses the system libsndfile shared library (`libsndfile.so.1`). On Debian
and Ubuntu install the `libsndfile1` runtime package; on Fedora install `libsndfile`.
The libsndfile core is LGPL licensed; consult its [official licensing information](https://libsndfile.github.io/libsndfile/#licensing)
and the exact package notices when redistributing the dynamic library and its dependencies.
Package-wide license expressions also cover bundled tools/files and are not alternative
licenses for the libsndfile core. No libsndfile binary is committed here.
