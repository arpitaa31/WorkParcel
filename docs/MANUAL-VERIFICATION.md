# Part 4 manual verification status

This checklist is intentionally split between deterministic local checks and
live browser acceptance. The live section must use disposable tabs only; it
must not be marked complete from fake-browser tests.

## Verified locally

- [x] Complete solution restored, built, and launched with the unpackaged profile.
- [x] 56 .NET tests pass, including protocol framing, migration, persistence,
  duplicate rules, restore planning, close planning, multi-browser routing,
  stale identities, cancellation, disconnects, and request timeouts.
- [x] MV3 manifest contract passes: exact `tabs`, `tabGroups`,
  `nativeMessaging`, and `storage` permissions; no host permissions or content
  scripts; incognito is disallowed; required icons exist.
- [x] Fake browser smoke covers window/tab grouping, private and internal URL
  exclusion, duplicate restore behavior, stale close rejection, safe close,
  opening, tab-group metadata, timeout state, and storage-write failure.
- [x] Native host smoke verifies real length-prefixed framing, app-pipe relay,
  and rejects a production launch without an extension origin.
- [x] Setup script parses successfully and performs current-user-only,
  exact-origin registration/removal.
- [x] Parcels page hierarchy and the compact `+ NEW PARCEL` menu were visually
  inspected at the default 1280×720 launch size.

## Live Chrome/Edge acceptance to run on a supported browser session

- [ ] Load `browser-extension` unpacked in Chrome and copy its exact ID.
- [ ] Register the Chrome host with `tools/Setup-BrowserHost.ps1`.
- [ ] Connect Chrome and verify popup status, window count, and tab count.
- [ ] Capture disposable HTTP/HTTPS tabs from two Chrome windows, including a
  tab group; verify selection, ordering, pinning, and grouping.
- [ ] Save, restart WorkParcel, and verify the saved browser-tab records remain.
- [ ] Close the disposable tabs, open the parcel, and verify grouped restore,
  ordering, pinning, active-tab behavior, and already-open handling.
- [ ] Verify unsupported internal URLs and private tabs are excluded honestly.
- [ ] Update the parcel, remove a saved tab, and confirm removal does not close
  a live tab.
- [ ] Verify `SAVE SELECTION` never closes tabs.
- [ ] Verify `SAVE AND CLOSE SELECTED` saves first, confirms twice, closes only
  checked current tabs, and reports partial failures.
- [ ] Restart the browser after selecting tabs and confirm stale identities are
  rejected rather than closing a replacement tab.
- [ ] Repeat the workflow in Edge using the same extension source and its own
  exact host registration.
- [ ] Connect Chrome and Edge together; confirm browser ownership and command
  routing remain independent when one disconnects.
- [ ] Exercise wrong extension ID, host repair/removal, extension reload,
  WorkParcel restart, theme changes, and resizing.

If the supported browser connector is unavailable, leave the live boxes
unchecked and report that live interaction was not observed. Do not substitute
browser-profile database inspection, a public HTTP endpoint, Playwright, or
other unsupported automation.
