using ILGPU;
using ILGPU.Runtime;

namespace MiniRover.Video;

/// <summary>What <see cref="VideoEnhancer"/> does to each frame. Every stage is optional.</summary>
public sealed class VideoEnhanceSettings
{
    /// <summary>0..1: temporal (motion-adaptive) plus edge-preserving spatial noise reduction. 0 = off.</summary>
    public float Denoise { get; set; } = 0.5f;

    /// <summary>0..1: unsharp mask after the noise reduction. 0 = off.</summary>
    public float Sharpen { get; set; } = 0.3f;

    /// <summary>Stretch the levels and lift the shadows toward a normal brightness (measured per frame).</summary>
    public bool AutoLevels { get; set; } = true;

    /// <summary>Grey-world white balance: removes the colour cast of indoor light (measured per frame).</summary>
    public bool WhiteBalance { get; set; } = true;

    /// <summary>The camera's top pixel row is a wrong-coloured line (measured on the car, every frame, QVGA and
    /// VGA): replace it with the row below.</summary>
    public bool FixTopRow { get; set; } = true;
}

/// <summary>
/// Cleans up the car's camera frames on the GPU. Frames are packed RGBA8 (one int per pixel, R in the low byte, row
/// major), the layout SpawnDev.ILGPU's ExternalImageCopier writes and its canvas renderer presents, so in the browser a
/// frame goes decoded bitmap -> these kernels -> canvas without leaving the GPU.
///
/// Per frame: a luminance histogram and channel sums (sampled on a 2x2 grid), a one-thread solve for the white
/// balance gains, black/white points and shadow lift (smoothed over frames so the picture does not pump), then noise
/// reduction (temporal + spatial) and the tone/colour/sharpen pass. Nothing is read back to the CPU.
/// </summary>
public sealed class VideoEnhancer : IDisposable
{
    const int HistogramBins = 256;
    const int StatsLength = HistogramBins + 5;     // histogram, mid-tone sums R, G, B, mid-tone count, sample count
    const int ParamsLength = 8;                    // gainR, gainG, gainB, black, white, lift, (unused), initialised

    readonly Accelerator _accelerator;
    readonly Action<Index1D, ArrayView1D<int, Stride1D.Dense>> _clear;
    readonly Action<Index1D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, int, int> _stats;
    readonly Action<Index1D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int> _solve;
    readonly Action<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, int, int, float, float, float, int, int> _denoise;
    readonly Action<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, int, int, int, float> _deblock;
    readonly Action<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView2D<int, Stride2D.DenseX>, ArrayView1D<float, Stride1D.Dense>, int, int, float, float> _tone;
    readonly Action<Index2D, ArrayView2D<int, Stride2D.DenseX>, ArrayView2D<int, Stride2D.DenseX>, int, int, int, int> _upscale;

    MemoryBuffer1D<int, Stride1D.Dense>? _input, _deblockH, _deblockHV, _history, _clean, _statsBuf;
    MemoryBuffer1D<float, Stride1D.Dense>? _params;
    MemoryBuffer2D<int, Stride2D.DenseX>? _output, _upscaled, _shown;
    int _width, _height;
    bool _historyValid;

    public VideoEnhancer(Accelerator accelerator)
    {
        _accelerator = accelerator;
        _clear = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<int, Stride1D.Dense>>(ClearKernel);
        _stats = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, int, int>(StatsKernel);
        _solve = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>(SolveKernel);
        _deblock = accelerator.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, int, int, int, float>(DeblockKernel);
        _denoise = accelerator.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, int, int, float, float, float, int, int>(DenoiseKernel);
        _tone = accelerator.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView2D<int, Stride2D.DenseX>, ArrayView1D<float, Stride1D.Dense>, int, int, float, float>(ToneKernel);
        _upscale = accelerator.LoadAutoGroupedStreamKernel<Index2D, ArrayView2D<int, Stride2D.DenseX>, ArrayView2D<int, Stride2D.DenseX>, int, int, int, int>(UpscaleKernel);
    }

    public Accelerator Accelerator => _accelerator;

    /// <summary>The processed frame after <see cref="Process"/>: the frame's size, or the requested output size.</summary>
    public MemoryBuffer2D<int, Stride2D.DenseX> Output => _shown ?? throw new InvalidOperationException("Process first");

    /// <summary>The cleaned frame at the camera's own size, packed RGBA as one row-major view: what the ML modes read
    /// (face detection), so they see the same cleaned picture the driver does, without leaving the GPU.</summary>
    public ArrayView1D<int, Stride1D.Dense> FrameView => (_output ?? throw new InvalidOperationException("Process first")).View.AsContiguous();

    public int Width => _width;
    public int Height => _height;

    /// <summary>Sizes the buffers for a frame and returns the view the decoded frame must be copied into.</summary>
    public ArrayView1D<int, Stride1D.Dense> Prepare(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (width != _width || height != _height)
        {
            DisposeFrameBuffers();
            long n = (long)width * height;
            _input = _accelerator.Allocate1D<int>(n);
            _deblockH = _accelerator.Allocate1D<int>(n);
            _deblockHV = _accelerator.Allocate1D<int>(n);
            _history = _accelerator.Allocate1D<int>(n);
            _clean = _accelerator.Allocate1D<int>(n);
            _output = _accelerator.Allocate2DDenseX<int>(new Index2D(width, height));
            _statsBuf ??= _accelerator.Allocate1D<int>(StatsLength);
            _params ??= _accelerator.Allocate1D<float>(ParamsLength);
            _width = width;
            _height = height;
            ResetHistory();
        }
        return _input!.View;
    }

    /// <summary>Forget the previous frames (a cut, a new car, a camera change): the next frame starts fresh.</summary>
    public void ResetHistory()
    {
        _historyValid = false;
        _params?.MemSetToZero();
    }

    /// <summary>Runs every stage on the frame in the <see cref="Prepare"/> view; the result is in <see cref="Output"/>,
    /// at <paramref name="outWidth"/> x <paramref name="outHeight"/> when given (Catmull-Rom upscaling for a big screen:
    /// sharper than the browser's bilinear scaling of a small frame). Only enqueues the work: synchronise (or present).</summary>
    public void Process(VideoEnhanceSettings s, int outWidth = 0, int outHeight = 0)
    {
        if (_input == null) throw new InvalidOperationException("Prepare first");
        int w = _width, h = _height;
        float d = Clamp01(s.Denoise);
        int fixTop = s.FixTopRow && h > 1 ? 1 : 0;

        if (s.AutoLevels || s.WhiteBalance)
        {
            _clear(StatsLength, _statsBuf!.View);
            _stats(((w + 1) / 2) * ((h + 1) / 2), _input.View, _statsBuf.View, w, h);
            _solve(1, _statsBuf.View, _params!.View, s.AutoLevels ? 1 : 0, s.WhiteBalance ? 1 : 0);
        }
        else
        {
            _params!.MemSetToZero(); // gains 0 mean "identity" to the tone kernel
        }

        // Spatial: neighbours count while their brightness is within sigma of the centre pixel (edges stay sharp).
        // Temporal: a pixel that barely changed keeps most of the previous result; a moving one is taken as it is.
        float spatialSigma = d <= 0 ? 0 : 6f + 26f * d;
        float minKeep = 1f - 0.75f * d;          // weight of the new frame where nothing moved
        float motionSigma = 8f + 24f * d;

        // JPEG block edges first: the car's QVGA frames show their 8x8 blocks once the shadows are lifted (measured in
        // Chrome: stair-steps all over the dark areas). Horizontal pass, then vertical.
        var denoiseSource = _input.View;
        if (d > 0)
        {
            float blockStep = 6f + 22f * d;
            _deblock(new Index2D(w, h), _input.View, _deblockH!.View, w, h, 0, blockStep);
            _deblock(new Index2D(w, h), _deblockH.View, _deblockHV!.View, w, h, 1, blockStep);
            denoiseSource = _deblockHV.View;
        }
        _denoise(new Index2D(w, h), denoiseSource, _history!.View, _clean!.View, w, h, spatialSigma, minKeep, motionSigma,
            _historyValid && d > 0 ? 1 : 0, fixTop);
        _historyValid = true;

        // Shadow colour: part of the noise reduction (full at 50% and up, none at 0).
        float shadowColour = 1f - 0.65f * (d * 2 > 1 ? 1 : d * 2);
        _tone(new Index2D(w, h), _clean.View, _output!.View, _params!.View, w, h, Clamp01(s.Sharpen), shadowColour);
        _shown = _output;

        if (outWidth > 0 && outHeight > 0 && (outWidth != w || outHeight != h))
        {
            if (_upscaled == null || _upscaled.IntExtent.X != outWidth || _upscaled.IntExtent.Y != outHeight)
            {
                _upscaled?.Dispose();
                _upscaled = _accelerator.Allocate2DDenseX<int>(new Index2D(outWidth, outHeight));
            }
            _upscale(new Index2D(outWidth, outHeight), _output.View, _upscaled.View, w, h, outWidth, outHeight);
            _shown = _upscaled;
        }
    }

    static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

    // ---- kernels ----

    static void ClearKernel(Index1D i, ArrayView1D<int, Stride1D.Dense> stats) => stats[i] = 0;

    static int Luma(int r, int g, int b) => (r * 77 + g * 150 + b * 29) >> 8;

    /// <summary>One thread per 2x2 block: samples its top-left pixel (row 0 skipped: it is the broken line).</summary>
    static void StatsKernel(Index1D i, ArrayView1D<int, Stride1D.Dense> src, ArrayView1D<int, Stride1D.Dense> stats, int w, int h)
    {
        int bw = (w + 1) / 2;
        int x = (i % bw) * 2, y = (i / bw) * 2 + 1;
        if (y >= h) return;
        int p = src[y * w + x];
        int r = p & 0xFF, g = (p >> 8) & 0xFF, b = (p >> 16) & 0xFF;
        int l = Luma(r, g, b);
        Atomic.Add(ref stats[l], 1);
        Atomic.Add(ref stats[HistogramBins + 4], 1);
        // White balance is measured on mid-tones only: near black a pixel is mostly noise and black-level offset
        // (measured on the car: whole-frame grey world turned the shadows purple), near white it is clipped.
        if (l < 24 || l > 235) return;
        Atomic.Add(ref stats[HistogramBins], r);
        Atomic.Add(ref stats[HistogramBins + 1], g);
        Atomic.Add(ref stats[HistogramBins + 2], b);
        Atomic.Add(ref stats[HistogramBins + 3], 1);
    }

    /// <summary>
    /// One thread: white-balance gains (grey world), black/white points (0.5% / 99.5% of the histogram) and the shadow
    /// lift k of y = x(1+k)/(1+kx) that brings the mean to a normal brightness. Blended into the previous values so
    /// the picture eases into a change of light instead of jumping.
    /// </summary>
    static void SolveKernel(Index1D i, ArrayView1D<int, Stride1D.Dense> stats, ArrayView1D<float, Stride1D.Dense> prm, int autoLevels, int whiteBalance)
    {
        float count = stats[HistogramBins + 4];
        if (count < 1) return;

        float gr = 1, gg = 1, gb = 1;
        float wbCount = stats[HistogramBins + 3];
        if (whiteBalance != 0 && wbCount >= count * 0.02f)
        {
            float mr = stats[HistogramBins] / wbCount, mg = stats[HistogramBins + 1] / wbCount, mb = stats[HistogramBins + 2] / wbCount;
            float grey = (mr + mg + mb) / 3f;
            // 80% of the full grey-world correction: a fully neutral room under warm light looks wrong too.
            const float strength = 0.8f;
            gr = 1 + (ClampF(grey / mr, 0.7f, 1.5f) - 1) * strength;
            gg = 1 + (ClampF(grey / mg, 0.7f, 1.5f) - 1) * strength;
            gb = 1 + (ClampF(grey / mb, 0.7f, 1.5f) - 1) * strength;
        }

        float black = 0, white = 1, lift = 0;
        if (autoLevels != 0)
        {
            int lowTarget = (int)(count * 0.005f), highTarget = (int)(count * 0.995f);
            int cum = 0, lo = 0, hi = 255;
            bool loFound = false, hiFound = false;
            float sum = 0;
            for (int b = 0; b < HistogramBins; b++)
            {
                int n = stats[b];
                sum += n * (float)b;
                cum += n;
                if (!loFound && cum > lowTarget) { lo = b; loFound = true; }
                if (!hiFound && cum >= highTarget) { hi = b; hiFound = true; }
            }
            black = lo / 255f;
            white = hi / 255f;
            if (white - black < 0.25f) white = black + 0.25f;   // a flat picture is not stretched into noise
            if (white > 1) { white = 1; black = 0.75f; }
            float mean = (sum / count / 255f - black) / (white - black);
            mean = ClampF(mean, 0.02f, 0.98f);
            const float target = 0.40f;
            lift = mean < target ? ClampF((target - mean) / (mean * (1 - target)), 0, 4f) : 0;
        }

        bool first = prm[7] == 0;
        float a = first ? 1f : 0.12f;
        prm[0] = Mix(prm[0], gr, a);
        prm[1] = Mix(prm[1], gg, a);
        prm[2] = Mix(prm[2], gb, a);
        prm[3] = Mix(prm[3], black, a);
        prm[4] = Mix(prm[4], white, a);
        prm[5] = Mix(prm[5], lift, a);
        prm[7] = 1;
    }

    static float Mix(float from, float to, float a) => from + (to - from) * a;
    static float ClampF(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    static int ClampI(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    /// <summary>
    /// One axis of JPEG deblocking. Across each 8-pixel block boundary (pixels L1 L0 | R0 R1) a small step is turned
    /// into a ramp: L0 and R0 move 3/8 of the step toward each other, L1 and R1 1/8. Only where the step is small and
    /// both sides are flat, so a real edge (a big step, or detail beside it) is left alone.
    /// </summary>
    static void DeblockKernel(Index2D idx, ArrayView1D<int, Stride1D.Dense> src, ArrayView1D<int, Stride1D.Dense> dst,
        int w, int h, int vertical, float maxStep)
    {
        int x = idx.X, y = idx.Y;
        int i = y * w + x;
        int c = src[i];
        int pos = vertical != 0 ? y : x, len = vertical != 0 ? h : w;
        int inBlock = pos & 7;
        int l0;
        float wt;
        if (inBlock == 7) { l0 = pos; wt = 0.375f; }
        else if (inBlock == 6) { l0 = pos + 1; wt = 0.125f; }
        else if (inBlock == 0) { l0 = pos - 1; wt = -0.375f; }
        else if (inBlock == 1) { l0 = pos - 2; wt = -0.125f; }
        else { dst[i] = c; return; }
        if (l0 - 1 < 0 || l0 + 2 >= len) { dst[i] = c; return; }

        int stride = vertical != 0 ? w : 1;
        int baseIndex = vertical != 0 ? x : y * w;
        int pl1 = src[baseIndex + (l0 - 1) * stride], pl0 = src[baseIndex + l0 * stride];
        int pr0 = src[baseIndex + (l0 + 1) * stride], pr1 = src[baseIndex + (l0 + 2) * stride];
        float yl1 = Luma(pl1 & 0xFF, (pl1 >> 8) & 0xFF, (pl1 >> 16) & 0xFF), yl0 = Luma(pl0 & 0xFF, (pl0 >> 8) & 0xFF, (pl0 >> 16) & 0xFF);
        float yr0 = Luma(pr0 & 0xFF, (pr0 >> 8) & 0xFF, (pr0 >> 16) & 0xFF), yr1 = Luma(pr1 & 0xFF, (pr1 >> 8) & 0xFF, (pr1 >> 16) & 0xFF);
        float step = yr0 - yl0, left = yl0 - yl1, right = yr1 - yr0;
        if (step < 0) step = -step;
        if (left < 0) left = -left;
        if (right < 0) right = -right;
        if (step >= maxStep || left >= maxStep * 0.5f || right >= maxStep * 0.5f) { dst[i] = c; return; }

        float r = (c & 0xFF) + wt * ((pr0 & 0xFF) - (pl0 & 0xFF));
        float g = ((c >> 8) & 0xFF) + wt * (((pr0 >> 8) & 0xFF) - ((pl0 >> 8) & 0xFF));
        float b = ((c >> 16) & 0xFF) + wt * (((pr0 >> 16) & 0xFF) - ((pl0 >> 16) & 0xFF));
        dst[i] = Pack(r, g, b);
    }

    static void DenoiseKernel(Index2D idx, ArrayView1D<int, Stride1D.Dense> src, ArrayView1D<int, Stride1D.Dense> history,
        ArrayView1D<int, Stride1D.Dense> clean, int w, int h, float spatialSigma, float minKeep, float motionSigma,
        int useHistory, int fixTop)
    {
        int x = idx.X, y = idx.Y;
        int sy = fixTop != 0 && y == 0 ? 1 : y;   // the broken top row reads the row below
        int c = src[sy * w + x];
        float cr = c & 0xFF, cg = (c >> 8) & 0xFF, cb = (c >> 16) & 0xFF;
        float cl = (cr * 77 + cg * 150 + cb * 29) / 256f;

        float r = cr, g = cg, b = cb;
        if (spatialSigma > 0)
        {
            float wsum = 1, rs = cr, gs = cg, bs = cb;
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = ClampI(sy + dy, fixTop != 0 ? 1 : 0, h - 1);
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = ClampI(x + dx, 0, w - 1);
                    int p = src[ny * w + nx];
                    float pr = p & 0xFF, pg = (p >> 8) & 0xFF, pb = (p >> 16) & 0xFF;
                    float diff = (pr * 77 + pg * 150 + pb * 29) / 256f - cl;
                    if (diff < 0) diff = -diff;
                    float wt = 1f - diff / spatialSigma;
                    if (wt <= 0) continue;
                    if (dx != 0 && dy != 0) wt *= 0.7f; // diagonals are further away
                    wsum += wt;
                    rs += pr * wt;
                    gs += pg * wt;
                    bs += pb * wt;
                }
            }
            r = rs / wsum;
            g = gs / wsum;
            b = bs / wsum;
        }

        int i = y * w + x;
        if (useHistory != 0)
        {
            int q = history[i];
            float qr = q & 0xFF, qg = (q >> 8) & 0xFF, qb = (q >> 16) & 0xFF;
            float motion = (r - qr) * 77 + (g - qg) * 150 + (b - qb) * 29;
            motion = (motion < 0 ? -motion : motion) / 256f;
            float k = ClampF(motion / motionSigma, minKeep, 1f);
            r = qr + (r - qr) * k;
            g = qg + (g - qg) * k;
            b = qb + (b - qb) * k;
        }
        int packed = Pack(r, g, b);
        clean[i] = packed;
        history[i] = packed;
    }

    static void ToneKernel(Index2D idx, ArrayView1D<int, Stride1D.Dense> clean, ArrayView2D<int, Stride2D.DenseX> output,
        ArrayView1D<float, Stride1D.Dense> prm, int w, int h, float sharpen, float shadowColour)
    {
        int x = idx.X, y = idx.Y;
        int c = clean[y * w + x];
        float r = c & 0xFF, g = (c >> 8) & 0xFF, b = (c >> 16) & 0xFF;

        if (sharpen > 0)
        {
            // Unsharp mask on a plus-shaped blur: c + amount * (c - blur).
            int up = clean[ClampI(y - 1, 0, h - 1) * w + x], down = clean[ClampI(y + 1, 0, h - 1) * w + x];
            int left = clean[y * w + ClampI(x - 1, 0, w - 1)], right = clean[y * w + ClampI(x + 1, 0, w - 1)];
            float br = ((up & 0xFF) + (down & 0xFF) + (left & 0xFF) + (right & 0xFF)) / 4f;
            float bg = (((up >> 8) & 0xFF) + ((down >> 8) & 0xFF) + ((left >> 8) & 0xFF) + ((right >> 8) & 0xFF)) / 4f;
            float bb = (((up >> 16) & 0xFF) + ((down >> 16) & 0xFF) + ((left >> 16) & 0xFF) + ((right >> 16) & 0xFF)) / 4f;
            float amount = sharpen * 1.2f;
            r += (r - br) * amount;
            g += (g - bg) * amount;
            b += (b - bb) * amount;
        }

        float gr = prm[0], gg = prm[1], gb = prm[2];
        if (gr <= 0) { gr = 1; gg = 1; gb = 1; }           // no measurement: identity
        // The cast belongs to the lit parts only. Measured on the car's camera under warm room light: mid-tones are
        // strongly yellow (luma 64-99: R 86, G 93, B 49) while the shadows are neutral to bluish (luma 8-15: R 10,
        // G 12, B 14), so one blue gain for the whole frame turned the shadows purple. Full correction from luma 64,
        // fading smoothly to none at 16 and below.
        float t = ClampF(((r * 77 + g * 150 + b * 29) / 256f - 16f) / 48f, 0, 1);
        t = t * t * (3 - 2 * t);
        gr = 1 + (gr - 1) * t;
        gg = 1 + (gg - 1) * t;
        gb = 1 + (gb - 1) * t;
        float black = prm[3], white = prm[4], lift = prm[5];
        if (white <= black) { black = 0; white = 1; }
        float range = white - black;

        r = Curve((r * gr / 255f - black) / range, lift);
        g = Curve((g * gg / 255f - black) / range, lift);
        b = Curve((b * gb / 255f - black) / range, lift);
        // Deep shadows lose most of their colour: there the colour is sensor noise (magenta speckle once the gain and
        // the shadow lift raise it - seen in Chrome on the car), not the scene. Same luma ramp as the white balance:
        // shadowColour (0.35 at full noise reduction) at luma 16 and below, full colour from 64.
        float sat = shadowColour + (1 - shadowColour) * t;
        float l = r * 0.299f + g * 0.587f + b * 0.114f;
        r = l + (r - l) * sat;
        g = l + (g - l) * sat;
        b = l + (b - l) * sat;
        output[idx] = Pack(r * 255f, g * 255f, b * 255f);
    }

    /// <summary>Catmull-Rom (bicubic) resampling of the finished frame to the output size: pixel centres line up, edge
    /// pixels repeat, and the packing clamps the slight overshoot a cubic filter has at hard edges.</summary>
    static void UpscaleKernel(Index2D idx, ArrayView2D<int, Stride2D.DenseX> src, ArrayView2D<int, Stride2D.DenseX> dst,
        int sw, int sh, int dw, int dh)
    {
        float fx = (idx.X + 0.5f) * sw / dw - 0.5f;
        float fy = (idx.Y + 0.5f) * sh / dh - 0.5f;
        int x0 = fx < 0 ? -1 : (int)fx, y0 = fy < 0 ? -1 : (int)fy;
        float tx = fx - x0, ty = fy - y0;
        float r = 0, g = 0, b = 0;
        for (int j = -1; j <= 2; j++)
        {
            float wy = CatmullRom(j - ty);
            int sy = ClampI(y0 + j, 0, sh - 1);
            for (int i = -1; i <= 2; i++)
            {
                float wgt = CatmullRom(i - tx) * wy;
                int p = src[new Index2D(ClampI(x0 + i, 0, sw - 1), sy)];
                r += (p & 0xFF) * wgt;
                g += ((p >> 8) & 0xFF) * wgt;
                b += ((p >> 16) & 0xFF) * wgt;
            }
        }
        dst[idx] = Pack(r, g, b);
    }

    /// <summary>Catmull-Rom kernel weight at distance d (|d| &lt; 2).</summary>
    static float CatmullRom(float d)
    {
        if (d < 0) d = -d;
        if (d < 1) return (1.5f * d - 2.5f) * d * d + 1f;
        if (d < 2) return ((-0.5f * d + 2.5f) * d - 4f) * d + 2f;
        return 0;
    }

    /// <summary>Shadow lift without pow: y = x(1+k)/(1+kx) keeps 0 and 1 fixed and raises the darks for k &gt; 0.</summary>
    static float Curve(float x, float k)
    {
        x = ClampF(x, 0, 1);
        return x * (1 + k) / (1 + k * x);
    }

    static int Pack(float r, float g, float b)
    {
        int ri = ClampI((int)(r + 0.5f), 0, 255), gi = ClampI((int)(g + 0.5f), 0, 255), bi = ClampI((int)(b + 0.5f), 0, 255);
        return unchecked((int)0xFF000000) | (bi << 16) | (gi << 8) | ri;
    }

    void DisposeFrameBuffers()
    {
        _input?.Dispose();
        _deblockH?.Dispose();
        _deblockHV?.Dispose();
        _history?.Dispose();
        _clean?.Dispose();
        _output?.Dispose();
        _upscaled?.Dispose();
        _input = _deblockH = _deblockHV = _history = _clean = null;
        _output = _upscaled = _shown = null;
    }

    public void Dispose()
    {
        DisposeFrameBuffers();
        _statsBuf?.Dispose();
        _params?.Dispose();
    }
}
