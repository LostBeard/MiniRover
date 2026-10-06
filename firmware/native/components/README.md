# Third-party ESP-IDF components (vendored)

These are copies of upstream ESP-IDF components, added to the firmware build through `NF_EXTRA_IDF_COMPONENT_DIRS`
(see `firmware/build-firmware.bat`). The firmware is built by calling ESP-IDF as a CMake library without the IDF
component manager, so registry dependencies are not resolved automatically: that is why they are vendored here.

| Component | Version | Source | License |
|---|---|---|---|
| `esp32-camera` | v2.1.8 | https://github.com/espressif/esp32-camera | Apache-2.0 (`esp32-camera/LICENSE`) |
| `esp_jpeg` | 1.3.1 | https://components.espressif.com/components/espressif/esp_jpeg | Apache-2.0 (`esp_jpeg/license.txt`) |

Changes from upstream:

- `esp32-camera/CMakeLists.txt`: `esp_jpeg` added to `REQUIRES` (upstream declares it only in `idf_component.yml`,
  which the component manager would have resolved). Marked with a `MiniRover:` comment.
- The `examples` and `test` folders were left out.
