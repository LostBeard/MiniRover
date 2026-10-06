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
- `esp32-camera/driver/cam_hal.c`, `ll_cam_send_event` (runs in the camera ISR): the overflow warning's `%s`
  arguments wrapped in `DRAM_STR`. Upstream passes plain literals, which live in flash. The event queue overflows
  exactly when a flash write (cache off) stalls the camera task, so the ISR then reads flash with the cache off and the
  chip freezes (interrupt watchdog reset). Measured on the car: streaming VGA and saving a setting crashed it within
  a few saves; with the fix, ten saves in a row pass and video recovers. Still present in upstream master and v2.1.8
  (checked 2026-10-06). Marked with a `MiniRover:` comment.
- The `examples` and `test` folders were left out.
