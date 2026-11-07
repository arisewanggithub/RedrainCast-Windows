---
description: "Use when preparing Microsoft Partner Center Store listings, uploading screenshot/icon assets, or validating Store publication automation."
applyTo: "tools/*Store*,.github/workflows/store-release.yml,docs/store/**"
---

# Store asset persistence

A hidden upload progressbar and a loaded image preview do not prove that an
asset reached the saved listing model; previews can appear before persistence.
After saving, request a fresh official Partner Center export and verify the
expected screenshot references, exact localized captions and `StoreLogo300x300`
references for every language, resolving an explicit shared default where
applicable. For example, a 300 x 300 preview with an empty exported icon field
and empty default is **not** a successfully saved Store icon.

Use the existing product's persisted asset URL when deliberately sharing its
approved icon across languages; never log signed URLs or credentials.
For icon-only CSV updates, omit unchanged screenshot rows; URL rehydration can
reorder existing screenshot/caption pairs, so verify their ordered references.
Follow [the Store preparation guide](../../docs/store/README.md) for CSV
encoding, the `ID` versus Indonesian `id` header collision, exact package
language keys and owner-controlled publication gates.
