# Workspace preferences

- The user launches the app through `C:\Users\Thang\Desktop\Accre Rotate IP.lnk`, which targets `C:\toolrotateip\artifacts\publish\NetworkProfileManager.exe`.
- After app fixes, publish the updated Release build to `artifacts\publish` so reopening this existing shortcut runs the new version. A build in a separate diagnostic directory is not sufficient delivery.
- Preserve the shortcut and user configuration. If the app is running, avoid partially overwriting its files; close it gracefully before updating and verify the published output.
- Do not change the machine MAC/IP or call IP rotation APIs as part of diagnostics or verification.
