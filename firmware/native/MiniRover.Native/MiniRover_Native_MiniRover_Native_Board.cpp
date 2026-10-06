//
// Copyright (c) 2026 Todd Tanner (LostBeard) and MiniRover contributors. MIT License.
//
// MiniRover.Native.Board: ESP32 functions nanoFramework does not expose. Started from the MetadataProcessor stub
// (firmware/MiniRover.Native/Stubs); the method table and checksum live in MiniRover_Native.cpp.
//

#include "MiniRover_Native.h"
#include "MiniRover_Native_MiniRover_Native_Board.h"

#include "esp_wifi.h"

using namespace MiniRover_Native::MiniRover_Native;

signed int Board::WifiRssi(HRESULT &hr)
{
    (void)hr;
    wifi_ap_record_t ap;
    // Fails (not ESP_OK) when the station is not associated: report 0 = unknown, never a stale value.
    if (esp_wifi_sta_get_ap_info(&ap) != ESP_OK)
    {
        return 0;
    }
    return ap.rssi;
}

bool Board::SetWifiPowerSave(bool param0, HRESULT &hr)
{
    (void)hr;
    // ESP-IDF refuses WIFI_PS_NONE while Bluetooth is enabled (WiFi/BT coexistence needs modem sleep), so this returns
    // false during the car's BLE setup window and the caller retries after it.
    return esp_wifi_set_ps(param0 ? WIFI_PS_MIN_MODEM : WIFI_PS_NONE) == ESP_OK;
}
