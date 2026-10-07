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
#include "esp_bt.h"
#include "esp_adc/adc_cali.h"
#include "esp_adc/adc_cali_scheme.h"
#include "rtc_wdt.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "lwip/sockets.h"
#include <errno.h>
#include "esp_rom_sys.h"

using namespace MiniRover_Native::MiniRover_Native;

// UdpTest progress (read through FreeMemory 108-110).
static volatile int s_udpBytes = 0;
static volatile int s_udpFull = 0;
static volatile int s_udpRunning = 0;

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
        case 106:
        {
            wifi_ps_type_t ps = WIFI_PS_NONE;
            return esp_wifi_get_ps(&ps) == ESP_OK ? (signed int)ps : -1;
        }
        case 107:
            return (signed int)esp_bt_controller_get_status();
        case 108:
            return s_udpBytes;
        case 109:
            return s_udpFull;
        case 110:
            return s_udpRunning;
        case 111:
        case 112:
        case 113:
        {
            // The station's link: primary channel, secondary channel (0 none, 1 above, 2 below = HT40), and the
            // access point's advertised PHY modes (bits: 0 11b, 1 11g, 2 11n, 3 low rate, 4 11a, 5 11ac, 6 11ax).
            wifi_ap_record_t joined;
            if (esp_wifi_sta_get_ap_info(&joined) != ESP_OK)
            {
                return -1;
            }
            if (param0 == 111)
            {
                return joined.primary;
            }
            if (param0 == 112)
            {
                return (signed int)joined.second;
            }
            return (joined.phy_11b ? 1 : 0) | (joined.phy_11g ? 2 : 0) | (joined.phy_11n ? 4 : 0) | (joined.phy_lr ? 8 : 0) |
                   (joined.phy_11a ? 16 : 0) | (joined.phy_11ac ? 32 : 0) | (joined.phy_11ax ? 64 : 0);
        }
        case 114:
        {
            wifi_phy_mode_t mode;
            return esp_wifi_sta_get_negotiated_phymode(&mode) == ESP_OK ? (signed int)mode : -1;
        }
        case 115:
        {
            wifi_bandwidth_t bw;
            return esp_wifi_get_bandwidth(WIFI_IF_STA, &bw) == ESP_OK ? (signed int)bw : -1;
        }
        case 116:
        {
            uint8_t protocols = 0;
            return esp_wifi_get_protocol(WIFI_IF_STA, &protocols) == ESP_OK ? protocols : -1;
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

// nanoFramework's BLE module (targets/ESP32/_nanoCLR/nanoFramework.Device.Bluetooth/esp32_nimble.cpp): its teardown
// stops the NimBLE host and disables + deinitialises the controller (nimble_port_deinit), and clears ble_initialized
// so the soft-reboot handler does not tear down twice.
extern void Device_ble_dispose();
extern bool ble_initialized;

bool Board::BluetoothOff(HRESULT &hr)
{
    (void)hr;
    if (ble_initialized)
    {
        Device_ble_dispose();
    }
    return esp_bt_controller_get_status() == ESP_BT_CONTROLLER_STATUS_IDLE;
}

// A WiFi throughput measurement without WebRTC, DTLS or SCTP: plain UDP from a native task, so the result is the
// radio link plus lwIP, and comparing it with the video stream's rate says which of the two limits the video.
struct UdpTestArgs
{
    struct sockaddr_in to;
    int durationMs;
};
// One test at a time (s_udpRunning), so the arguments can live here (nanoFramework bans plain malloc).
static UdpTestArgs s_udpArgs;

static void UdpTestTask(void *arg)
{
    UdpTestArgs args = *(UdpTestArgs *)arg;
    int sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock >= 0)
    {
        static uint8_t payload[1200]; // the video's packet size (libpeer's SCTP chunks fit one 1200-byte datagram)
        memset(payload, 0x5A, sizeof(payload));
        TickType_t start = xTaskGetTickCount();
        TickType_t length = pdMS_TO_TICKS(args.durationMs);
        uint32_t seq = 0;
        while (xTaskGetTickCount() - start < length)
        {
            memcpy(payload, &seq, sizeof(seq)); // lets the receiver count losses
            int sent = sendto(sock, payload, sizeof(payload), 0, (struct sockaddr *)&args.to, sizeof(args.to));
            if (sent > 0)
            {
                s_udpBytes += sent;
                seq++;
            }
            else
            {
                // ENOMEM: the WiFi driver's TX queue is full ("Not enough space" in the video log). Back off 0.5 ms:
                // a whole tick (10 ms here) would cap the measurement at one queue-full per tick.
                s_udpFull++;
                esp_rom_delay_us(500);
                taskYIELD();
            }
        }
        closesocket(sock);
    }
    s_udpRunning = 0;
    vTaskDelete(NULL);
}

bool Board::UdpTest(const char *param0, signed int param1, signed int param2, HRESULT &hr)
{
    (void)hr;
    if (s_udpRunning || param0 == NULL || param1 <= 0 || param1 > 65535 || param2 <= 0)
    {
        return false;
    }
    UdpTestArgs *args = &s_udpArgs;
    memset(args, 0, sizeof(*args));
    args->to.sin_family = AF_INET;
    args->to.sin_port = htons((uint16_t)param1);
    if (inet_aton(param0, &args->to.sin_addr) == 0)
    {
        return false;
    }
    args->durationMs = param2 > 30000 ? 30000 : param2;
    s_udpBytes = 0;
    s_udpFull = 0;
    s_udpRunning = 1;
    // Priority and core like the WebRTC pump (5, either core) so the comparison is fair.
    if (xTaskCreatePinnedToCore(UdpTestTask, "udptest", 3072, args, 5, NULL, tskNO_AFFINITY) != pdPASS)
    {
        s_udpRunning = 0;
        return false;
    }
    return true;
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
