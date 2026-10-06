//
// Copyright (c) 2026 Todd Tanner (LostBeard) and MiniRover contributors. MIT License.
//
// MiniRover.Native.Board: ESP32 functions nanoFramework does not expose. Started from the MetadataProcessor stub
// (firmware/MiniRover.Native/Stubs); the method table and checksum live in MiniRover_Native.cpp.
//

#include "MiniRover_Native.h"
#include "MiniRover_Native_MiniRover_Native_Board.h"

#include "esp_wifi.h"
#include "esp_heap_caps.h"
#include "esp_system.h"
#include "esp_sleep.h"
#include "rtc_wdt.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

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

signed int Board::FreeMemory(signed int param0, HRESULT &hr)
{
    (void)hr;
    switch (param0)
    {
        case 0: return (signed int)heap_caps_get_free_size(MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
        case 1: return (signed int)heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
        case 2: return (signed int)heap_caps_get_free_size(MALLOC_CAP_SPIRAM);
        case 3: return (signed int)heap_caps_get_largest_free_block(MALLOC_CAP_SPIRAM);
        case 4: return (signed int)heap_caps_get_free_size(MALLOC_CAP_DMA);
        case 5: return (signed int)heap_caps_get_minimum_free_size(MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
    }
    return -1;
}

signed int Board::ResetReason(HRESULT &hr)
{
    (void)hr;
    return (signed int)esp_reset_reason();
}

void Board::FullReset(HRESULT &hr)
{
    (void)hr;
    // The RTC watchdog's "reset RTC" stage resets the RTC domain as well as the digital core: the nearest thing
    // to the EN pin that firmware can do. A deep-sleep wake (tried first) keeps the RTC domain, and after an
    // interrupt-watchdog crash the car's I2C devices and camera stayed silent through it; only EN brought them back.
    // The next boot reports ESP_RST_WDT.
    rtc_wdt_protect_off();
    rtc_wdt_disable();
    rtc_wdt_set_length_of_reset_signal(RTC_WDT_SYS_RESET_SIG, RTC_WDT_LENGTH_3_2us);
    rtc_wdt_set_stage(RTC_WDT_STAGE0, RTC_WDT_STAGE_ACTION_RESET_RTC);
    rtc_wdt_set_time(RTC_WDT_STAGE0, 10);
    rtc_wdt_enable();
    rtc_wdt_protect_on();
    for (;;)
    {
        vTaskDelay(1);
    }
}
