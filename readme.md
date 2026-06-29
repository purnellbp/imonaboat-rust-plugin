# Imonaboat

*Beautifully simple boat controls with a nautical theme.*

A simple Rust plugin that lets players control their modular boat with the helm keys, with an on-screen panel that shows the live status of the engine, throttle, sails, and anchor.

## What it does

When you sit at the boat's steering wheel (helm), a small panel appears at the bottom of your screen. It shows the live status of your engine, throttle, sails, and anchor. You control everything with the normal movement keys, so there's nothing to set up or bind.

## Controls (while sitting at the helm)

- **E** - turn the engine on/off
- **W** - go forward
- **S** - go in reverse
- **R** - raise/lower the sails
- **CTRL** - drop/raise the anchor

The panel updates automatically so you always see the current state:

- **Engine:** ON / OFF
- **Throttle:** FORWARD / REVERSE / IDLE
- **Sails:** UNFURLED / FURLED
- **Anchor:** ANCHORED / HOUSED

## Commands

- `/boatui` - show or hide the control panel
- `/boatedit` - (admin) move and resize the panel, then save
- `/boatreloadimages` - (admin) re-download the panel images after you update them

## Installation

1. Copy `Imonaboat.cs` into your server's `carbon/plugins/` folder.
2. The plugin loads automatically and downloads its images on first start.

## Permissions

- `imonaboat.use` - allowed to use the boat controls (only needed if you turn on "Require permission")
- `imonaboat.admin` - allowed to use the admin commands (`/boatedit`, `/boatreloadimages`)

## Settings

The config file is at `carbon/configs/Imonaboat.json`:

- **Require permission** - if true, players need the `imonaboat.use` permission. Default: off (everyone can use it).
- **Require boat authorization** - if true, only players authorized on the boat can control it. Default: on.
- **Auto-show UI when taking the helm** - shows the panel automatically at the steering wheel. Default: on.
- **Enable keyboard controls while mounted** - turns the E/R/Ctrl/W/S controls on or off. Default: on.
- **UI refresh interval (seconds)** - how often the panel updates. Default: 3.
- **Command cooldown (seconds)** - small delay between control actions. Default: 0.5.
- **UI anchor min / max** - the panel's position and size on screen. Easiest to set with `/boatedit`.

## Images

The panel artwork is downloaded from the plugin's repo and cached by Carbon. If you change the images in the repo, run `/boatreloadimages` in-game to pull the new versions without restarting.
