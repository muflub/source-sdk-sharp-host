# Steamworks redistributables

`redistributable_bin/` from the Steamworks SDK **165** (`steamworks_sdk_165.zip`), unchanged, for
the x64 platforms only: `linux64`, `osx` (a universal x86_64 + arm64 dylib, as Valve ships it) and
`win64`. The gateway image takes `linux64/libsteam_api.so` through the `steamworks` build context
(`make image-gateway`, CI); `STEAMWORKS_SDK=<sdk root>` builds against another SDK instead. Replace
these files when the SDK is updated, and update this table.

| file | sha256 |
|---|---|
| `redistributable_bin/linux64/libsteam_api.so` | `eb2dd015b84177cf4f4326fe578aab375fd8931bbbd719c7492420d9777007fe` |
| `redistributable_bin/osx/libsteam_api.dylib` | `a37faf19675b943a3c9717d25d9a881f5addb7f0ee728e03de7aac143ba759d2` |
| `redistributable_bin/win64/steam_api64.dll` | `e6d9bafb9a41e42fba7b21553db49f8719027af3f0deb23ff86cfc44d60e776d` |
| `redistributable_bin/win64/steam_api64.lib` | `7b68c738457211a088bdcf583ca08a294b48345dbb59a8aa3c8c63f3646c894c` |
