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
#include "esp_netif.h"
#include "lwip/netif.h"
#include "esp_system.h"
#include "esp_sleep.h"
#include "esp_adc/adc_cali.h"
#include "esp_adc/adc_cali_scheme.h"
#include "rtc_wdt.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

using namespace MiniRover_Native::MiniRover_Native;

signed int Board::WifiRssi(HRESULT &hr)
{
    (void)hr;
    wifi_ap_record_t ap;
    // The signal of the WiFi link the car talks over. On a home network: the access point it joined.
    if (esp_wifi_sta_get_ap_info(&ap) == ESP_OK)
    {
        return ap.rssi;
    }
    // Play mode (the car is the access point): the strongest device on the car's own network. The driver's phone is
    // normally the only one; with several, the strongest is an upper bound for the driver's link.
    wifi_sta_list_t list = {};
    if (esp_wifi_ap_get_sta_list(&list) == ESP_OK && list.num > 0)
    {
        int best = -127;
        for (int i = 0; i < list.num; i++)
        {
            if (list.sta[i].rssi > best)
            {
                best = list.sta[i].rssi;
            }
        }
        return best;
    }
    // Not associated and nobody on the car's network: 0 = unknown, never a stale value.
    return 0;
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
    // Network diagnostics on the same getter (new kinds are constants only: no interop checksum change).
    // 100 stations on the car's access point, 101 / 102 the AP interface's IPv4 as esp-netif / lwIP see it (network
    // order), 103 the AP's DHCP server state (esp_netif_dhcp_status_t), 104 the station's IPv4, 105 the WiFi mode.
    esp_netif_t *ap = esp_netif_get_handle_from_ifkey("WIFI_AP_DEF");
    esp_netif_t *sta = esp_netif_get_handle_from_ifkey("WIFI_STA_DEF");
    esp_netif_ip_info_t ip = {};
    switch (param0)
    {
        case 100:
        {
            wifi_sta_list_t list = {};
            return esp_wifi_ap_get_sta_list(&list) == ESP_OK ? list.num : -1;
        }
        case 101:
            return (ap != NULL && esp_netif_get_ip_info(ap, &ip) == ESP_OK) ? (signed int)ip.ip.addr : -1;
        case 102:
        {
            struct netif *n = ap != NULL ? netif_get_by_index((u8_t)esp_netif_get_netif_impl_index(ap)) : NULL;
            return n != NULL ? (signed int)ip_2_ip4(&n->ip_addr)->addr : -1;
        }
        case 103:
        {
            esp_netif_dhcp_status_t st = ESP_NETIF_DHCP_INIT;
            return (ap != NULL && esp_netif_dhcps_get_status(ap, &st) == ESP_OK) ? (signed int)st : -1;
        }
        case 104:
            return (sta != NULL && esp_netif_get_ip_info(sta, &ip) == ESP_OK) ? (signed int)ip.ip.addr : -1;
        case 105:
        {
            wifi_mode_t mode = WIFI_MODE_NULL;
            return esp_wifi_get_mode(&mode) == ESP_OK ? (signed int)mode : -1;
        }
    }
    return -1;
}

// ADC1, 12 dB, 12-bit (nanoFramework's AdcController uses exactly these, sys_dev_adc_native_..._AdcController.cpp).
// Created on first use and kept: the scheme only holds the eFuse-derived line coefficients.
static adc_cali_handle_t s_adcCali = NULL;
static int s_adcCaliState = 0; // 0 not tried, 1 ready, -1 no calibration data on this chip

signed int Board::AdcMillivolts(signed int param0, HRESULT &hr)
{
    (void)hr;
    if (s_adcCaliState == 0)
    {
        adc_cali_line_fitting_config_t config = {};
        config.unit_id = ADC_UNIT_1;
        config.atten = ADC_ATTEN_DB_12;
        config.bitwidth = ADC_BITWIDTH_12;
        s_adcCaliState = adc_cali_create_scheme_line_fitting(&config, &s_adcCali) == ESP_OK ? 1 : -1;
    }
    int mv = 0;
    if (s_adcCaliState < 0 || adc_cali_raw_to_voltage(s_adcCali, param0, &mv) != ESP_OK)
    {
        return -1;
    }
    return mv;
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
