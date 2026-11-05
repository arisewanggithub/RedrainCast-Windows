# Microsoft Store

The new product is **RedrainCast**, Store ID `9P1153MP6R32`.
[Store link](https://apps.microsoft.com/detail/9P1153MP6R32)
(the public page may not exist until publication).

[StoreIdentity.json](StoreIdentity.json) records the package name, publisher
subject and display names from this product's Partner Center identity page.
These are public package identifiers, not credentials. Do not reuse either
older Windows app's identity. Review the values against Partner Center before
the first submission.

1. Prepare native dependencies and publish each target architecture using the
   commands in [README.md](README.md).
2. Package each publication with `tools\Package.ps1`. The fourth MSIX version
   component must be zero and the package version must match the assembly.
3. Keep signing keys/certificates and Store API secrets outside this repository.
   The generated MSIX files are unsigned submission artifacts; installation
   outside the Store requires a trusted signature and separate signing setup.
4. Upload to the new product, complete pricing/availability, age rating,
   privacy policy URL, description, screenshots, support details and notes.
   This repository does not submit or publish automatically.
5. Certification notes should describe the two-device trusted-LAN setup,
   automatic incoming acceptance, OS-assigned TCP listener ports, fixed UDP 27183
   discovery and application-based Private-network firewall permissions,
   hardware codec requirements, and automatic receive/send role switching.
   Do not claim compatibility with generic Miracast/Google Cast receivers.

Use RedrainCast consistently in the listing, package and screenshots. Scrcpy
is not the app title. Any required attribution belongs in notices/About text
without implying endorsement.

This new package will not upgrade installations of ScrcpySend-Windows or
ScrcpyReceive-Windows. No migration or replacement of their Store listings is
performed.
