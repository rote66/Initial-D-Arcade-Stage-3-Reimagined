# Chinese Arcade Typography

The user-supplied patched Street Stage ISO is a reference for wording,
typography and translation coverage. Its whole title, input-hint, mode,
course-card and name textures are not production assets.

All 1152 dialogue records are now independently translated from the arcade
English scripts, not carried over from the patch. Every dialogue glyph is
freshly rasterized from local fonts. The old import pipeline and its 1632
private-use glyph slots have been removed. This does not confer rights to
the underlying game or retroactively update earlier distributed builds.
See `provenance.md` for the current inputs and historical reuse.

`Native/tools/chinese_ui_text.py` holds the UI text and typography roles.
`ui.json` is its generated chunk allowlist. Headings use Source Han Serif Heavy,
brush captions use the locally installed Xingkai face, and small text uses
the project's Noto Sans SC. Source Han's license is retained in `fonts/`;
the system brush font is not copied into the repository or IPA. Only generated
game resources are distributed. `typography.json` records the actual font hashes,
using repository-relative paths or `$IDAS3_BRUSH_FONT`, never host-specific paths.

Font family alone does not reproduce the reference. Headings have independently
calibrated character spacing, horizontal proportions and a drop shadow. Mode
labels use a wider/flatter aspect, black outlines and red outlines around white
accent glyphs, not red-filled glyphs. Small mode explanations use serif lettering.
The font candidate comparison and read-only reference measurements live under
`Builds/iOS/Audits/Typography-35`.

The arcade's title bars, separator lines, course photos, A/B/C/D letter shapes,
stickers and vehicle names are preserved. AT/MT stays Latin, with the reference's
small automatic/manual captions underneath. Glow layers are
generated from the normal rendered label in world space.

Hints follow D3's original arcade styling, not PSP typography: small sans-serif
control names inside white 70x16 frames, #ffbb00 actions and vertical separators.
They occupy the original arcade hint batch's ink bounds. Structured control/action
pairs name actual joystick and START operations. Only rival selection advertises
holding VIEW for music. PSP directional-pad/circle graphics and the unavailable
Project D website feature are not imported.

## Regenerate

The tools need Pillow and NumPy. Font tests also need fontTools. Regenerate
the independent UI and complete story set from the checked-in authoring inputs:

```sh
python3 Native/tools/make_chinese_arcade.py build
python3 Native/tools/test_chinese_ui_text.py
```

Authoring resolves the installed `Xingkai.ttc` on macOS. On another authoring
host, set `IDAS3_BRUSH_FONT` to the corresponding locally installed face.

Normal builds require neither the ISO nor reference PNGs nor an old font/dialog
binary. `font.bin` shares freshly rasterized Unicode glyphs between screens.
Story text is laid out one glyph
at a time, not pasted as dialogue screenshots. HUD/VS names are newly typeset
from verified names.

`make_chinese_arcade.py build` also runs `build_chinese_story.py`. It compiles
all 744 rival and 408 Bunta records from the original arcade token stream, preserving every
non-text source token, speaker, portrait, branch, wait and address. All story
text uses real Unicode and Source Han Serif / Noto Sans SC, without reading
the ISO, decoded images, old binaries or patch text. Unicode name/vehicle glyphs
and the separate HUD/VS name labels are regenerated too. There are no retained
legacy rows or extracted glyph slots. `story-build.json` records counts and hashes.

`story-source.json` locks the original English lists and source hashes.
`stories/00.txt` through `30.txt` contain one translation per listed passage;
character 17 instead uses `miki.json`. `stories/bunta.txt` covers the Bunta pool.
Never insert, remove or reorder lines without checking the matching source list.
The `<empty>` entry intentionally omits a whitespace-only source display token.
The original `NO WORDS` placeholder is shown as an ellipsis, not invented dialogue.
Use `build_chinese_story.py catalog` only after reviewing any upstream source change.

`IDASZH2` contains 1152 localized display scripts. Generator, tests and runtime verify
every native non-display opcode and source address. Native branches, portraits,
dynamic car/player values and physics remain unchanged.

The formerly missing Miki story no longer falls back to English. At the user's
request, the arcade-only snow-course artwork reads
秋名 with a separate white 雪 inside its original-style black badge.
The race-intro version independently imitates the D3 Japanese title style:
navy/cyan brush strokes, a white keyline and blue glow, with a black 雪
outlined in white and no white rectangular badge. Conditions retain D3's
projected word height and baseline, with measured Chinese advances.
GO, FINISH, win/lose and
English result headings also stay original. The inconsistent reference name
"三木" is not used; the verified spelling is 御木.

Dialogue uses a 20-pixel cell and line pitch in the bottom black bar. Day/night
fields share fixed widths. `chinese_arcade_tests` checks original texture
fallback, vehicle/network names, script control flow, all dialogue pages and
the Unity texture contract. Simulator captures verify actual composition.

Extension course display names are supplied by the user, not inferred from a
glossary: ENNA=塩那, MOMIJI=枫树线, TSUBAKI=筑波, GUNSAI=群赛,
MYOGI_SPECIAL=真・妙义 and USUI_SPECIAL=真・碓冰. Their stable IDs, folders,
handling and leaderboard identities do not change. Localized menu banks and
race-intro titles are generated separately; original English/Japanese banks
remain intact. Old extension exports have flattened English titles; the
Chinese cards reframe an unlettered part of the same photo and preserve the
timed road map from the original markers, retaining English route labels.
The full background uses one photo crop without a vertical splice.
Original and extension main titles share the same x=199 center and y=48
ink-top anchor, with black brush strokes, a soft white halo, and 41-pixel
stroke height. Thumbnails share a centered 22-pixel brush label.
Mode explanations share one left anchor, a 12px font em, a 14px line pitch
and identical baselines. They are drawn on a fixed canvas, not fitted to each
line's ink bounds or length. HUD names use independently typeset, white
emboldened sans-serif letters with a black border, matching the patch's
separate Chinese rival-name face. They are baked once at the player's 12px
cell height; long names fit horizontally without reducing that height.
The shared dialogue atlas includes the name-caption colon and all dynamic
vehicle/player/number glyphs explicitly. All Unicode glyphs come from the
independent font pass above, with no legacy private-use story slots.
Coverage checks include native JIS values and every localized playback
string, not just static Chinese dialogue. The original minus and apostrophe
share the independent ASCII equivalents.
Latin glyphs are left-bearing normalized and use proportional
advances from their visible ink plus one pixel, not a fixed half-cell.
Dialogue layout reads those same atlas advances, including the dynamic
EIGHT−SIX, player-name and number insertions. Tests inspect actual ink and
spacing, as well as the existence of the Unicode records.

The old PSP reference reader and importer have been removed. Do not restore
extracted glyphs to fill a missing character; extend the independent font coverage instead.
