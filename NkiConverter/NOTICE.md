# NkiConverter – notices

`NkiConverter` converts Native Instruments Kontakt instruments (`.nki`) into Ableton Live
Sampler presets (`.adv`).

## Third-party work

The knowledge of the Kontakt file formats (NI container / "hsin" items, preset chunks,
program / group / zone / loop layouts, file lists, Kontakt 2–4.2 headers and XML, NCW
sample compression) and the Ableton Sampler device templates in `Ableton/Templates/`
are derived from **ConvertWithMoss** by Jürgen Moßgraber:

- https://github.com/git-moss/ConvertWithMoss
- Copyright (c) 2019–2026 Jürgen Moßgraber
- Licensed under the GNU Lesser General Public License v3.0

Because of this, `NkiConverter` is distributed under the **GNU LGPL v3.0** (see `LICENSE`)
as a separate assembly (`NkiConverter.dll`). The NKXTool UI application only links to it,
so the library can be replaced with a modified version as the LGPL requires.

"Kontakt" and "Native Instruments" are trademarks of Native Instruments GmbH.
"Ableton" and "Live" are trademarks of Ableton AG. This project is not affiliated with
either company.
