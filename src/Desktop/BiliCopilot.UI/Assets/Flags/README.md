# Flags

Flags shown next to the dialing codes on the sign-in page.

- Source: [flag-icons](https://github.com/lipis/flag-icons) 7.6.0, the 4x3 set. MIT License, see `LICENSE.txt`.
- Each file is the SVG of the same name rendered to 80 x 60 pixels (four times the size it is shown at) and
  losslessly recompressed. The drawings are unchanged.
- Only the 235 flags that the sign-in country list can ask for are here. The list of entries and the flag each
  one uses is in `ViewModels/View/StartupPageViewModel/StartupCountryFlags.cs`.

To add a flag, render its SVG the same way, name it after the SVG, and add a row to that list:

```sh
resvg -w 80 -h 60 flags/4x3/xx.svg xx.png
oxipng -o 4 --strip safe xx.png
```
