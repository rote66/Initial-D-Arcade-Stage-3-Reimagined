# dr_mp3

Vendored from the official `mackron/dr_libs` repository:
https://github.com/mackron/dr_libs/blob/master/dr_mp3.h

Header version 0.7.4, retrieved 2026-10-09. The exact source is kept in this
repository so builds never download or follow a moving upstream revision.
The unmodified header includes its public-domain / MIT-0 license and upstream
minimp3 attribution. This project uses the MIT-0 alternative.

`original_audio.cpp` compiles the decoder with file I/O disabled and uses bounded
memory input. LAME/Xing encoder delay and padding are removed before applying
the exported original music loop points. No JIT or platform audio device is
needed to decode MP3.
