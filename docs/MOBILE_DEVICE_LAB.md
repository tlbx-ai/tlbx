# Mobile Device Lab

MidTerm has two intentionally different mobile preview modes:

- **Responsive frame** constrains the embedded preview to a Pixel-width layout. It is fast, but it remains a desktop iframe and does not claim to emulate a mobile browser.
- **Chrome device** opens a top-level Pixel 8 target in the user's local Chrome. A small MidTerm MV3 extension applies CDP device metrics, DPR, touch input, Android User-Agent and Client Hints, orientation, dynamic viewport sizes, focus, lifecycle, and screenshots.

The Chrome device runs on the computer where the user has opened MidTerm. The MidTerm server and the application under test may be remote. No Chrome process, Node runtime, Playwright runtime, or debugging port is required on the MidTerm host.

## Connect Chrome

1. Click the visible install button beside **Responsive frame** in the Dev Browser URL bar, or open [MidTerm Mobile Device Bridge in the Chrome Web Store](https://chromewebstore.google.com/detail/mipkpmmedaoighaadeedfedimiaaekcn).
2. Install the extension. **Download Chrome bridge** remains available in the overflow menu as a manual unpacked-extension fallback.
3. Click the **MidTerm Mobile Device Bridge** extension icon in the MidTerm tab, or press `Ctrl+Shift+Y` (`Cmd+Shift+Y` on macOS).
4. The same button now shows the device icon; click it to open the Pixel 8 target.

Chrome displays its normal debugger notice while the device target is attached.

## Controls

The Dev Browser menu exposes rotate, keyboard viewport, background/foreground lifecycle, and close actions. Existing DOM automation and screenshot commands continue to use MidTerm's browser bridge because the device is registered as a normal top-level preview client.

```text
mt_mobile open
mt_mobile rotate
mt_mobile keyboard
mt_mobile background
mt_mobile foreground
mt_mobile reload
mt_mobile screenshot
mt_mobile close
```

The keyboard control approximates the viewport reduction caused by a software keyboard; desktop Chrome cannot summon a mobile operating-system keyboard. The device mode uses Chromium, so it does not reproduce WebKit/Safari rendering or iOS-only browser behavior. Final Safari-specific checks still require Safari or a real iOS device.

## Security Model

The extension has no broad host permission and no always-on content script. The user explicitly grants `activeTab` access by clicking the extension in a MidTerm tab. CDP attaches only to the device tab created by the extension. Chrome still describes the powerful `debugger` permission broadly in its extension UI; MidTerm limits its use in code to that extension-created tab.
