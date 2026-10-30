# Original D3 Race Music

Sound Room retains Automatic (-1) and adds Restore Default (-3) immediately
below it. Positive song IDs and custom PCM (-2) are unchanged. The V1 preference
file keeps `default` for existing Automatic saves and uses `original` for the
new policy. Choosing either policy also clears the custom/packaged selection,
without deleting songs. On startup, `original` takes precedence over stale
library selections if the earlier library cleanup could not be saved.

`original_race_music.h` shares the existing Legend return owner's 31-rival
last-song table. This preserves the distinct Akina and Tsuchisaka story songs,
including Kyoichi's Akina battle and the final Project D rivals.

Outside Legend, the original nine courses use their D3 course music; Akina and
Tsuchisaka choose the night variant at night, and Akina Snow uses Stop Your
Self Control. Card BGM OFF remains enforced by the mixer. FIX uses the card's
valid original song ID; an invalid ID falls back to the race's default.

Track identity is checked against the archived Hitmaker/SEGA official D3 BGM
page: https://backup.segakore.fr/hitmaker/game/INITIALD/ac_ver03/bgm.html .
The existing Legend return table supplies the rival-specific assignment.
This is not an instruction-by-instruction replay of an original BGM selector.
Imported courses have no D3 assignment and fall back to the existing Automatic
selector instead of accidentally using their internal Akina placeholder.

Native tests cover all 31 rivals, nine courses, time/direction/weather, card
settings, policy persistence and the independent random selector. Simulator
cases `music-default` and `music-default-restore` use private saves and check
the real menu, custom/packaged reset, cold restore and native mixer timeline.
