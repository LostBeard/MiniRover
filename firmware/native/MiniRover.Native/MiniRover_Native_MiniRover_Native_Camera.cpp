//
// Copyright (c) 2026 Todd Tanner (LostBeard) and MiniRover contributors. MIT License.
//
// MiniRover.Native.Camera: the camera head on the Freenove FNK0053 (ESP32-WROVER), pins from Docs/hardware.md.
//
// One FreeRTOS task captures frames and hands each one to SpawnDev.nanoFramework.WebRTC's "latest frame wins" slot
// (sdnf_webrtc_offer_frame), so frame bytes never enter the nanoFramework managed heap and video never queues up
// delay. The OV2640 produces JPEG itself; the GC0308 has no JPEG encoder, so its YUV frames are JPEG-encoded here
// (esp32-camera's frame2jpg), which costs CPU and caps its frame rate.
//
// Resource choices that keep clear of nanoFramework:
// - XCLK on LEDC low-speed timer 3 / channel 7: nanoFramework PWM (the buzzer) uses the high-speed timers by default.
// - SCCB on I2C port 1 with the legacy driver (sdkconfig): nanoFramework's I2C bus 1 is port 0, also legacy, and
//   IDF aborts at startup if the legacy and new I2C drivers are mixed.
//

#include "MiniRover_Native.h"
#include "MiniRover_Native_MiniRover_Native_Camera.h"

#include "esp_camera.h"
#include "img_converters.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_heap_caps.h"

// From SpawnDev.nanoFramework.WebRTC (spawndev_nf_webrtc.h): declared here so this assembly does not need that
// repository's include path. C ABI, stable.
extern "C" int sdnf_webrtc_offer_frame(int handle, uint16_t sid, const uint8_t *data, size_t len);

using namespace MiniRover_Native::MiniRover_Native;

// ---- pins (Freenove ESP32-WROVER CAM, Docs/hardware.md) ----
#define CAM_PIN_PWDN -1
#define CAM_PIN_RESET -1
#define CAM_PIN_XCLK 21
#define CAM_PIN_SIOD 26
#define CAM_PIN_SIOC 27
#define CAM_PIN_D7 35
#define CAM_PIN_D6 34
#define CAM_PIN_D5 39
#define CAM_PIN_D4 36
#define CAM_PIN_D3 19
#define CAM_PIN_D2 18
#define CAM_PIN_D1 5
#define CAM_PIN_D0 4
#define CAM_PIN_VSYNC 25
#define CAM_PIN_HREF 23
#define CAM_PIN_PCLK 22

static volatile bool s_ready = false;
static volatile bool s_softJpeg = false;    // GC0308: encode in software
static volatile int s_quality = 12;         // 0..63 sensor scale (lower = better)
static volatile int s_sensor = 0;
static framesize_t s_maxSize = FRAMESIZE_SVGA; // the frame buffers were allocated for this size (see Init)

static volatile int s_handle = -1;
static volatile int s_sid = -1;
static volatile int s_maxFps = 15;

static volatile int s_framesSent = 0;
static volatile int s_lastBytes = 0;
static volatile int s_errors = 0;
static volatile int s_fpsTenths = 0;
static volatile int s_encodeMs = 0;

static TaskHandle_t s_task = NULL;

// A CLR soft reboot (deploy, debugger restart) restarts managed code but not this task: stop streaming so the new
// program's first connection (which may get the same handle number) never receives frames it did not ask for.
// The camera itself stays initialised; Init reconfigures it.
static void cam_soft_reboot()
{
    s_handle = -1;
    s_sid = -1;
}

// esp32-camera quality is 0..63 (lower = better); frame2jpg wants 1..100 (higher = better).
static int soft_quality(int q)
{
    int v = 100 - q * 100 / 63;
    return v < 10 ? 10 : (v > 95 ? 95 : v);
}

static void cam_task(void *arg)
{
    (void)arg;
    int64_t windowStart = esp_timer_get_time();
    int windowFrames = 0;
    int64_t nextFrame = 0;

    for (;;)
    {
        int handle = s_handle;
        int sid = s_sid;
        if (!s_ready || handle < 0 || sid < 0)
        {
            s_fpsTenths = 0;
            vTaskDelay(pdMS_TO_TICKS(50));
            continue;
        }

        // Frame pacing: never faster than maxFps (the link and the browser gain nothing from more).
        int64_t now = esp_timer_get_time();
        if (now < nextFrame)
        {
            vTaskDelay(pdMS_TO_TICKS((nextFrame - now) / 1000 + 1));
            continue;
        }
        int fps = s_maxFps > 0 ? s_maxFps : 1;
        nextFrame = now + 1000000 / fps;

        camera_fb_t *fb = esp_camera_fb_get();
        if (fb == NULL)
        {
            s_errors++;
            vTaskDelay(pdMS_TO_TICKS(20));
            continue;
        }

        int r;
        if (fb->format == PIXFORMAT_JPEG)
        {
            r = sdnf_webrtc_offer_frame(handle, (uint16_t)sid, fb->buf, fb->len);
            if (r == 1) s_lastBytes = (int)fb->len;
            esp_camera_fb_return(fb);
        }
        else
        {
            uint8_t *jpg = NULL;
            size_t jpgLen = 0;
            int64_t t0 = esp_timer_get_time();
            bool ok = frame2jpg(fb, soft_quality(s_quality), &jpg, &jpgLen);
            s_encodeMs = (int)((esp_timer_get_time() - t0) / 1000);
            esp_camera_fb_return(fb); // the sensor can fill it again while this frame goes out
            r = ok ? sdnf_webrtc_offer_frame(handle, (uint16_t)sid, jpg, jpgLen) : -1;
            if (r == 1) s_lastBytes = (int)jpgLen;
            if (!ok) s_errors++;
            heap_caps_free(jpg); // frame2jpg allocates with malloc; nanoFramework bans the free() name
        }

        if (r == 1)
        {
            s_framesSent++;
            windowFrames++;
        }
        now = esp_timer_get_time();
        if (now - windowStart >= 1000000)
        {
            s_fpsTenths = (int)(windowFrames * 10000000LL / (now - windowStart));
            windowFrames = 0;
            windowStart = now;
        }
    }
}

static esp_err_t cam_start(pixformat_t format, framesize_t size, int quality)
{
    camera_config_t c = {};
    c.pin_pwdn = CAM_PIN_PWDN;
    c.pin_reset = CAM_PIN_RESET;
    c.pin_xclk = CAM_PIN_XCLK;
    c.pin_sccb_sda = CAM_PIN_SIOD;
    c.pin_sccb_scl = CAM_PIN_SIOC;
    c.pin_d7 = CAM_PIN_D7;
    c.pin_d6 = CAM_PIN_D6;
    c.pin_d5 = CAM_PIN_D5;
    c.pin_d4 = CAM_PIN_D4;
    c.pin_d3 = CAM_PIN_D3;
    c.pin_d2 = CAM_PIN_D2;
    c.pin_d1 = CAM_PIN_D1;
    c.pin_d0 = CAM_PIN_D0;
    c.pin_vsync = CAM_PIN_VSYNC;
    c.pin_href = CAM_PIN_HREF;
    c.pin_pclk = CAM_PIN_PCLK;
    c.xclk_freq_hz = 20000000;
    c.ledc_timer = LEDC_TIMER_3;
    c.ledc_channel = LEDC_CHANNEL_7;
    c.pixel_format = format;
    c.frame_size = size;
    c.jpeg_quality = quality;
    c.fb_count = 2;                         // capture the next frame while this one is sent
    c.fb_location = CAMERA_FB_IN_PSRAM;     // internal RAM is the scarce resource on this board
    c.grab_mode = CAMERA_GRAB_LATEST;       // always the newest frame: no built-up delay
    c.sccb_i2c_port = 1;
    return esp_camera_init(&c);
}

signed int Camera::Init(signed int param0, signed int param1, HRESULT &hr)
{
    (void)hr;
    HAL_AddSoftRebootHandler(cam_soft_reboot); // deduped by the HAL
    framesize_t size = (framesize_t)param0;
    int quality = param1 < 0 ? 0 : (param1 > 63 ? 63 : param1);

    if (s_ready)
    {
        // Already running: just apply the settings.
        return Configure(param0, param1, hr) ? s_sensor : -1;
    }

    // esp32-camera sizes its frame buffers for the frame size given at init, and set_framesize never grows them:
    // a larger size later overruns the buffers (the car restarted while a test switched 320x240 -> 400x296 while
    // streaming). So JPEG sensors start at the largest size we allow (JPEG buffers are width*height/5: 96 KB each at
    // 800x600, in PSRAM) and step down; the size is never raised above what the buffers were made for.
    s_softJpeg = false;
    s_maxSize = FRAMESIZE_SVGA;
    esp_err_t err = cam_start(PIXFORMAT_JPEG, FRAMESIZE_SVGA, quality);
    if (err == ESP_ERR_NOT_SUPPORTED)
    {
        // Sensor without a JPEG encoder (GC0308): capture YUV422 and encode in the task. Raw YUV buffers are
        // width*height*2 (600 KB at 640x480), too big to start large, so its size can only go down from here.
        esp_camera_deinit();
        if (size > FRAMESIZE_VGA) size = FRAMESIZE_VGA;
        s_maxSize = size;
        err = cam_start(PIXFORMAT_YUV422, size, quality);
        s_softJpeg = err == ESP_OK;
    }
    if (err != ESP_OK)
    {
        esp_camera_deinit();
        return err > 0 ? -(signed int)err : -1;
    }

    sensor_t *s = esp_camera_sensor_get();
    s_sensor = s != NULL ? s->id.PID : 0;
    s_quality = quality;
    if (!s_softJpeg && s != NULL && s->set_framesize != NULL && size < s_maxSize)
    {
        s->set_framesize(s, size); // step down to the requested size
    }

    if (s_task == NULL &&
        xTaskCreatePinnedToCore(cam_task, "mr_camera", 6144, NULL, 4, &s_task, tskNO_AFFINITY) != pdPASS)
    {
        s_task = NULL;
        return -1;
    }
    s_ready = true;
    return s_sensor;
}

void Camera::Stream(signed int param0, signed int param1, signed int param2, HRESULT &hr)
{
    (void)hr;
    s_maxFps = param2 < 1 ? 1 : (param2 > 30 ? 30 : param2);
    s_sid = param1;
    s_handle = param0; // last: the task reads handle first
}

bool Camera::Configure(signed int param0, signed int param1, HRESULT &hr)
{
    (void)hr;
    sensor_t *s = esp_camera_sensor_get();
    if (!s_ready || s == NULL)
    {
        return false;
    }
    int quality = param1 < 0 ? 0 : (param1 > 63 ? 63 : param1);
    framesize_t size = (framesize_t)param0;
    if (size > s_maxSize) size = s_maxSize; // never beyond the frame buffers (see Init)
    bool ok = true;
    if (!s_softJpeg && s->set_quality != NULL) ok = s->set_quality(s, quality) == 0;
    s_quality = quality;
    if (s->set_framesize != NULL) ok = s->set_framesize(s, size) == 0 && ok;
    return ok;
}

void Camera::SetOrientation(bool param0, bool param1, HRESULT &hr)
{
    (void)hr;
    sensor_t *s = esp_camera_sensor_get();
    if (s == NULL) return;
    if (s->set_hmirror != NULL) s->set_hmirror(s, param0 ? 1 : 0);
    if (s->set_vflip != NULL) s->set_vflip(s, param1 ? 1 : 0);
}

signed int Camera::GetStat(signed int param0, HRESULT &hr)
{
    (void)hr;
    switch (param0)
    {
        case 0: return s_fpsTenths;
        case 1: return s_framesSent;
        case 2: return s_lastBytes;
        case 3: return s_errors;
        case 4: return s_sensor;
        case 5: return s_softJpeg ? s_encodeMs : 0;
    }
    return 0;
}
