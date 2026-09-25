# Installer Artwork

The installer follows the ElkaSoft VBAN Plug installer's `modern dark polar`
theme, dark title bar, 120% wizard size, and branded welcome/finish pages.
Inno Setup 6.6 or newer is required.

- `../docs/images/fx-host-vertical.png`: the approved FX Host vertical artwork,
  reused directly as the wizard sidebar.
- `../src/app-wpf/Assets/VoicemeeterDelayIconPreview.png`: the existing FX Host
  icon used in the page header.
- `../src/app-wpf/Assets/VoicemeeterDelay.ico`: the existing installer file icon.
- `Assets/ElkaSoft.png`: the user's original ElkaSoft logo, copied unchanged
  from the VBAN Plug installer. It is extracted temporarily for the welcome
  and finish pages, not installed as an application payload file.

This visual update does not change installation paths, shortcuts,
power-throttling options, or the signing workflow.
