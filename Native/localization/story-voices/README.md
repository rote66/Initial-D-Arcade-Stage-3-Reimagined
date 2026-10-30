# Independent Story Voice Subtitles

`00.tsv` through `29.tsv` contain 1190 independently authored Chinese and
English subtitle pairs, keyed by the first alias of each unique original
voice recording. Bank 25 shares its audio with other banks and needs no
duplicate authoring file. The compiler expands these pairs to all 1331
voice IDs in the 30 original opponent banks.

## Source And Editorial Rules

The supplied PSP image was inspected for an original Japanese voice-ID
script. No usable keyed Japanese race-voice script was recovered from the
inspected containers and executables. That finding is limited to the
audited locations. Original encoded speech, not the Chinese patch text,
was decoded and transcribed locally with whisper.cpp and both
large-v3-turbo and large-v3. No audio was uploaded and no subtitle prompt
was supplied. Source hashes, model hashes and both Japanese candidates
are retained in `source.json`; detailed raw runs are in
`Builds/iOS/Audits/IndependentVoice`.

The Japanese fields are ASR candidates, not verified verbatim transcripts.
The bilingual lines were individually composed and edited against those
candidates and racing context, including review of differences between
the two recognizers. The initial pairs were not translations of the patch
or a bulk machine translation export. This is editorial review, not a claim
of complete human listening verification.

At the user's request on 2026-10-30, the four explicitly documented ambiguous
recordings were matched to the exact PSP patch voice IDs and their glyphs
were rendered for comparison: `B_ituki_front_005`, `B_debu_rpass_006`,
`B_EVO5_rpass_017` and `B_takumi_ppass_016`. That reference supported three
editorial corrections (survival rather than asking for help, a slowpoke
taunt rather than a bare interjection, and a grunt rather than a curse);
the remaining translation already agreed in meaning. The revised wording
is independently phrased, but these selected corrections now have an
explicit PSP-reference provenance. See `reference-review.json` and the
audit evidence, not an assertion of a verified Japanese transcript.

Brief cries, grunts and laughter remain difficult to transcribe exactly.
Keep the raw ASR candidates unchanged. Further corrections should edit the
TSV and their review record, never patch the generated glyph indices.

Chinese names and terminology follow `../zh-Hans/names.json` and the
existing independently translated arcade stories: 小柏海、御木、馆智幸、
伊吕波坂、碓冰, original vehicle model names and uppercase team labels.
Use natural, concise spoken Chinese, retaining each character's tone.
English uses original Japanese character names rather than localized
English aliases, and is phrased independently as spoken dialogue.
Race direction, overtaking, throttle/braking and closing-battle meaning
must not be reversed by a literal translation.

## Generate

Requires Python 3.11+, Pillow and fontTools:

```sh
python3 Tools/Build-StoryVoiceSubtitles.py check
python3 Tools/Build-StoryVoiceSubtitles.py build
python3 -m unittest discover -s Tests/Input -p test_story_voice_subtitles.py
```

The compiler creates a new Noto Sans SC atlas using the project's licensed
font and includes its OFL notice. It derives proportional English spacing
and language-specific wrapping independently. Duration comes from the
original decoded recording, not the patch's subtitle timer. The importer
does not read patch subtitle tables or glyphs. Generated v2 manifests
record source, authoring and font hashes.

The recovered original scheduling rules and original-CPU differential
verification are documented separately in [scheduler.md](scheduler.md).

PSP-style lower-screen placement, white text, a small black drop shadow
and transparent background are presentation choices only. Original game
speech and other game assets retain their respective rights; independent
subtitle creation does not grant redistribution rights to those assets.
