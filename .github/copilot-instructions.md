# Build validation

- Use the host-native architecture for local app builds. On an ARM64 Windows host, use `-p:Platform=ARM64 -r win-arm64`; do not default to `win-x64`.
- Leave x64 compatibility builds to CI unless explicitly requested or needed to validate an x64-specific change.
- Run only the builds and tests needed for the change; do not produce additional architecture builds without a concrete validation need.
