# Street Stage Speech Scheduler

## Reference

The supplied image's unencrypted `PSP_GAME/SYSDIR/EBOOT.BIN` has SHA-256
`4c0c5865b5157a8b9764448ae68ff1faf9a28e0c2f6ed1f1e5215ce33224d4e5`.
Addresses below are ELF-relative, not relocated PSP RAM addresses.
The original module is an analysis input only, never an application asset.
The subtitle patch is not executed by the reference tool.

| Address | Purpose |
| --- | --- |
| `1AEC50` | Stable event sort and runtime rule indices |
| `1AEECC` | Reset counts, flags, timers and used-line masks |
| `1AEFA0` | Event conditions, selection, completion and interruption |
| `1AF9C0` | Remaining count and four-sector mask |
| `1AFA84` | Consume requested filename |
| `1AFA90` | Signed cooldown jitter |
| `1AFAC8` | Fisher-Yates permutation |
| `03C6C8` | 64-bit RNG, upper 31-bit result |
| `1AE550` | Production input assembly and audio handoff |
| `087C1C` | Race timer update, 200 units per PSP frame |
| `07D68C` / `07D950` | Countdown / running update boundaries |

The PSP clock is 30 Hz. Its 6000-units-per-second race timer advances
by 200; the speech input divides that timer by 200. Countdown does not
advance that timer. The event-1 flag is set at clock frame 90, about
three seconds after GO, not at the beginning of countdown.

Unity keeps the original arcade 60 Hz simulation. The native scene bridge
captures every second owner tick, including every captured tick within a
single render catch-up. It exports race elapsed time divided by 200,
extension counter clamped to 3, original battle-result lead state
(including the rival-finish latch), speed, path progress and signed gap.
The managed scheduler never advances from a repeated render sample or
while paused. No simulation RNG or physics clock is changed.

## Recovered Behavior

There are 1699 rules in the 30 banks. The first 28 bytes of each 60-byte
row are preserved by the importer:

| Byte | Meaning |
| --- | --- |
| 0 | Original row ID, used by explicit chains |
| 1 | Event |
| 2 | Priority |
| 3 | Signed remaining count: 1 or -1 (unlimited) |
| 4-7 | Enabled sectors |
| 8 | Speaker metadata |
| 9 | Voice-state override (zero in shipped rows) |
| 10 | Signed next row ID, -1 for none |
| 11 | Replaced with stable sorted runtime index |
| 12-27 | Optional conditions (all zero in shipped rows) |

The original optional-condition loop returns true even on a mismatch;
it must not acquire new behavior during this port.

Event 2/3 requires absolute gap strictly greater than 4.3 and strictly
less than 80, and a matching lead state. Leaving the range resets its
120-frame delay. Event 4 requires the player-ahead state and gap at
least 81; event 5 requires the opposite state and gap at least 90.
Leaving either far-gap condition resets the 150-frame delay.
These are not late-race percentage cues.

Event 6/7 uses the original 30-frame side-change stability counters and
handled flags. It is not a 45-frame debounce plus a 360-frame cooldown.
The initial order cannot become a passing cue before the opening ends.
Event 8/9 is enabled beyond progress 0.9 at gap below 50, but none of
the supplied banks has rows for those events. Event 10 has no shipped
rows either. Do not manufacture new pools or repurpose event 4/5.

Each eligible event shuffles its rows, filters remaining counts and sector
masks, avoids used lines and the previous filename, then follows the
original mask-reset and last-line fallback. The candidate events are also
shuffled, with strictly higher priority winning. Only strictly higher
priority can interrupt playing speech. Completed speech imposes a
30-frame same-or-lower-priority hold, decremented on the completion frame.
Per-event base cooldowns in this executable are zero, with signed random
jitter from -30 through +30. Signed values and the original reset paths
are deliberately retained, including negative cooldowns.

An explicit next-row ID bypasses the completion hold. This produces
Sayuki and Mako's authored exchange without a special-case event queue.
No speech is newly scheduled after finish; an already playing recording
can finish. Opponent changes, restart, exclusion and disabling voice stop
old playback or pending decode. The scheduler continues with inaudible
completion while the voice option is disabled, as in the PSP manager.

## Differential Verification

`Tools/Reference-PspStoryVoiceScheduler.py` uses temporary Unicorn 2.1.4
to execute only the bounded original constructor, reset, tick, predicate,
shuffle, jitter and RNG. Allocation, TLS and `strcmp` are isolated host
boundaries. No PSP OS, patch module, hardware or user save is loaded.
It rejects an executable hash mismatch and unexpected code addresses.
With `--voice-banks` pointing to the extracted original `sound/voice`
directory, it additionally checks all 30 bank hashes, exact 60-byte rule
tables and 1331 original recording identities against the imported catalog.
The recorded comparison used this verification.

It exports 90 traces (three per bank), each with 2200 input frames.
Coverage includes exact gap boundaries, persistent gaps, lead changes,
sector changes, repeated audio completion and interruption opportunities.
The production C# scheduler is compiled separately and compared against
all 198000 original frame snapshots, including:

- Requested sorted row and filename.
- Current event, priority, used-line and remaining-play counts.
- Ready/handled flags, last indices, signed cooldowns and priority hold.
- Explicit chain state, voice-state restore delay and RNG state.

Reports and vectors are in `Builds/iOS/Audits/PspVoiceScheduler`.
This proves scheduler equivalence for those inputs, not identical random
choices between live PSP and Unity races. The PSP shares its RNG with
other systems; Unity deliberately uses the same algorithm in a separate
presentation stream. Audio-device latency, MP3 decode padding and native
PSP versus Unity hardware output are not covered by a CPU comparison.
Simulator tests separately check production telemetry, catch-up sampling,
frame-90 start, playback, pause, exclusions and subtitle presentation.
