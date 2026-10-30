# PSP Voice Transcript Preparation

`Transcribe-PspStoryVoices.py` recovers Japanese ASR candidates from the user's
PSP media. It does not translate the existing Chinese subtitles. No audio is
uploaded, and no PSP executable is executed.

## Prepare

Requires Python 3.11+, FFmpeg, bsdtar, and a local ISO. Extract a supplied CHD
with `chdman extractraw` first; keep the original image unchanged.

```sh
python3 Tools/Transcribe-PspStoryVoices.py prepare \
  --iso /path/to/source.iso \
  --output Builds/iOS/Audits/IndependentVoice \
  --work /private/tmp/idas3-independent-voice-work
```

This audits voice-bank entries, event tables, text-bearing XBB containers,
loose text files, and the main executable files. The audit is evidence of the
inspected locations, not proof that no other Japanese script exists anywhere.
Only the original encoded voice bytes feed the PCM decoder. Chinese text,
patched glyph maps, subtitle timings, and fonts are excluded from ASR inputs.

## Transcribe

Use a local whisper.cpp CLI and a multilingual model. The first recovery run
used whisper.cpp revision `e4acdfdf214836d6ce5c7f8ac6b43b9912f361a1` and
`ggml-large-v3-turbo.bin`, SHA-256
`1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69`.
The runtime and model are authoring dependencies, not game dependencies.

```sh
python3 Tools/Transcribe-PspStoryVoices.py transcribe \
  --whisper /path/to/whisper-cli \
  --model /path/to/ggml-large-v3-turbo.bin \
  --output Builds/iOS/Audits/IndependentVoice \
  --work /private/tmp/idas3-independent-voice-work
```

`--limit 30` makes a representative trial. Completed raw results are reused
when resuming with the same model and executable. Use a different `--run`
name for another model/configuration.

## Review

`sources.json` records source hashes and all voice-ID/opponent/event aliases.
`turbo/raw/` retains the decoder output, tokens, and timestamps.
`turbo/japanese-candidates.json` collects the candidates with review flags.
Token scores are not calibrated accuracy; even a high-score sentence can
contain wrong names, omitted words, or hallucinated phrases.

Every raw record remains `unreviewed_asr`, and `chineseTranslation` remains
null. These candidate files are not loaded by the game. The independently
edited Chinese/English subtitle pairs are stored separately under
`Native/localization/story-voices`, with their own provenance and remaining
ASR limitations. `Build-StoryVoiceSubtitles.py` validates their coverage and
generates a licensed Noto atlas, independent timing and language-specific
wrapping. Obtaining a transcript does not itself authorize distribution of
the original game media.

The complete second recognition pass used `ggml-large-v3.bin`, SHA-256
`64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2`.
Both models covered all 1190 unique recordings (1331 aliases). Neither run
is labelled a human-verified transcript.

```sh
python3 -m unittest discover -s Tests/Input -p test_psp_voice_transcripts.py -v
```
