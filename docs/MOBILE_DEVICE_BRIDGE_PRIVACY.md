# MidTerm Mobile Device Bridge Privacy Policy

Last updated: July 10, 2026

MidTerm Mobile Device Bridge is a local companion extension for the MidTerm Dev Browser. It has no developer-operated backend, analytics, advertising, telemetry, or tracking.

## Data handled

The extension handles data only on the user's device and only after the user explicitly activates it in a MidTerm tab:

- The selected MidTerm preview URL is used to navigate an extension-created Chrome device window. It is not retained after navigation.
- Controller tab identifiers and transient device state are stored in `chrome.storage.session` so the bridge can recover while the current Chrome session remains open. This state is removed when the Chrome session ends.
- When the user explicitly requests a screenshot, pixels from the extension-created device tab are captured locally and returned to the activated MidTerm tab. The extension does not send screenshots to the MidTerm developers or any third party.

For Chrome Web Store disclosure purposes, this local processing is classified as:

- **Web history:** the single preview URL explicitly supplied by the activated MidTerm tab so the extension can open that preview in its device window.
- **Website content:** pixels from the extension-created device tab when the user explicitly requests a screenshot.

Neither data type is transmitted to the developer, retained after its immediate function or Chrome session, sold, shared, or used for advertising, analytics, profiling, creditworthiness, or any purpose unrelated to the extension's single mobile-device-preview function.

The extension's use of information received through Chrome APIs adheres to the Chrome Web Store User Data Policy, including the Limited Use requirements.

## Permissions

- `activeTab`: grants access only to the MidTerm tab where the user clicks the extension.
- `scripting`: injects the packaged MidTerm page bridge into that explicitly activated tab.
- `debugger`: attaches only to the device tab created by the extension to apply Chrome DevTools Protocol device emulation, lifecycle controls, and user-requested screenshots.
- `storage`: keeps approved tab identifiers and transient device state for the current Chrome session.
- `tabs`: creates, validates, focuses, and closes the controller and device tabs used by the bridge.

The extension declares no broad host permissions and no always-on content script. It executes no remote code.

## Data control

Closing the device window, closing the activated MidTerm tab, or ending the Chrome session removes the corresponding transient bridge state. Uninstalling the extension removes all extension-owned data.

## Contact

Questions and support requests can be filed at <https://github.com/tlbx-ai/MidTerm/issues>.
