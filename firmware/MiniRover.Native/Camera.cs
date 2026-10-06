using System.Runtime.CompilerServices;

namespace MiniRover.Native
{
    /// <summary>
    /// The camera head (OV2640 or GC0308, see Docs/hardware.md), run by a native task so frame bytes never enter the
    /// managed heap: each frame goes straight to the WebRTC "video" channel's latest-frame slot. The OV2640 encodes
    /// JPEG in hardware; the GC0308 cannot, so the task JPEG-encodes its frames in software (slower, lower fps).
    /// </summary>
    public static class Camera
    {
        // Sensor product ids returned by Init (esp32-camera's *_PID).
        public const int SensorNone = 0;
        public const int SensorOV2640 = 0x26;
        public const int SensorGC0308 = 0x9B;

        // Frame sizes (esp32-camera framesize_t).
        public const int SizeQQVGA = 1;   // 160x120
        public const int SizeQVGA = 6;    // 320x240
        public const int SizeCIF = 8;     // 400x296
        public const int SizeHVGA = 9;    // 480x320
        public const int SizeVGA = 10;    // 640x480
        public const int SizeSVGA = 11;   // 800x600 (OV2640 only)

        // GetStat ids
        public const int StatFpsTenths = 0;     // frames handed to the link per second x 10, over the last second
        public const int StatFramesSent = 1;    // total frames handed to the link
        public const int StatLastFrameBytes = 2;
        public const int StatCaptureErrors = 3;
        public const int StatSensor = 4;        // sensor product id, SensorNone before Init
        public const int StatEncodeMs = 5;      // last software JPEG encode time (GC0308), 0 for hardware JPEG

        /// <summary>
        /// Powers up the camera and detects the sensor. Returns the sensor id (SensorOV2640 / SensorGC0308 / another
        /// esp32-camera id) or a negative ESP-IDF error when no camera answers (the ultrasonic head is fitted, or the
        /// cable is loose). Safe to call again: a running camera is reconfigured.
        /// </summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int Init(int frameSize, int jpegQuality);

        /// <summary>
        /// Streams frames to a WebRTC connection's channel (handles from SpawnDev.nanoFramework.WebRTC), at most
        /// <paramref name="maxFps"/> per second. A handle below 0 stops streaming (the camera stays powered).
        /// </summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void Stream(int peerHandle, int streamId, int maxFps);

        /// <summary>Changes resolution / JPEG quality (0..63, lower = better) while running. False if refused.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool Configure(int frameSize, int jpegQuality);

        /// <summary>Mirror / flip, for a camera head mounted the other way up.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void SetOrientation(bool hMirror, bool vFlip);

        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int GetStat(int stat);
    }
}
