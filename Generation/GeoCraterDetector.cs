using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;

namespace GeoWorld
{
    // Craters on satellite imagery (Esri World Imagery class, about 0.5 m per pixel): for every crater found its centre, the
    // pit (the dark bowl, at its half-level edge), the disturbed patch where it shows (bright ejecta, or the ground darkened
    // around the pit), a confidence and fresh (bright bare ejecta) / water (dark green-cyan pit) flags. A port of the final
    // prototype of the crater-detector workflow (crater_final.py), which merged two detectors:
    //  * the ring detector finds the candidates (local minima of the opened, blurred luma with a centre-surround contrast),
    //    measures a 16 x 47 radial profile (scale from a disc/annulus contrast curve, pit radius from the per-direction
    //    half-level edge, recentred twice) and scores it: a logistic regression fitted on hand labels, physical vetoes
    //    (vegetation, cast shadows, concrete, size, contrast), a strong-pit mode and a bright-ejecta ring mode;
    //  * the blob detector's tests run at the ring centre, at a few radii around the ring's pit radius (24 x 33 polar samples
    //    of log luminance, greenness, linear colour and gradients: contrast, isotropy, open sides, lines through the blob,
    //    darkness, clutter around, vegetation, shadows, and its own fresh-ring path);
    //  a crater is kept where both agree, plus two narrow pathways (the blob tests very sure in leaf-off scenes; the ring mode
    //  alone on open, unvegetated ground in leaf-on scenes); then a dark patch holding several pits gives way to them and close
    //  duplicates are dropped. Tuned at 0.488 m per pixel on two captures near Bakhmut (March fields, September city):
    //  precision over recall, pits under about 2 m are left out and leaf-on cities give few detections. Deterministic: every
    //  candidate is measured on its own, the parallel loops only split the work.
    public static class GeoCraterDetector
    {
        [Serializable]
        public sealed class Settings
        {
            [Tooltip("Season of the capture: -1 = from the share of vivid green pixels, 0 = leaf-off (bare trees), 1 = leaf-on. " +
                     "Leaf-on scenes keep only clear pits: in summer a dark crown and a dark pit look alike.")]
            public int leafOn = -1;
            [Tooltip("Share of vivid green pixels ((G - max(R, B)) / G > 0.45 in linear colour) above which the scene is leaf-on.")]
            public double vividShareLeafOn = 0.03;
            [Tooltip("Compass azimuth of the sun, degrees (WorldView captures about 10:30 local time: ~155).")]
            public double sunAzimuth = 155;
            [Tooltip("Pixels at the image border where no crater centre is looked for.")]
            public int border = 10;
            [Tooltip("Candidate: box centre-surround contrast of the luma, grey levels (0-255).")]
            public double candidateContrast = 10;
            public int recenterIterations = 1;
            [Tooltip("Ring gate: contrast (grey levels), closed rays of 16, roundness, edge spread, pit radius range (px).")]
            public double gateContrast = 12;
            public int gateClosedRays = 14;
            public double gateRoundness = 0.45, gateEdgeSpread = 0.45, gateRadiusMin = 1.3, gateRadiusMax = 30;
            [Tooltip("Ring classifier (standardised logistic regression on 11 scale-free features: open rays, edge spread, roundness, " +
                     "weakest side, one-sidedness, edge width, wide clutter, side spread, texture, core texture, log radius).")]
            public double[] classifierMean = { 0.2938053097345133, 0.2013414533965135, 0.7849183768244314, 0.4826605834880457, 0.24151907341096798,
                0.975689150080404, 0.1414056155292816, 0.2786719032730967, 0.09600787705172836, 0.09464100674011638, 1.172521137258749 };
            public double[] classifierScale = { 0.5848061162931106, 0.13119624323317308, 0.1286924602419672, 0.2805500400505879, 0.18575046785812355,
                0.4260322289956446, 0.09695231482520733, 0.17376699189657577, 0.04105515887131107, 0.030832516615834422, 0.46746866267029097 };
            public double[] classifierWeights = { -1.0385129596738703, -0.008481791311919347, -0.17499989167936983, 0.05742037297797679,
                0.19693347581146245, 0.16963019731031848, -0.0818012843816627, -1.3814332670008866, -0.50862062789304, 0.21604786293636746,
                -0.10606839162629526, 0.19253891940481435 };
            [Tooltip("Logit shifts: cluttered surround (gardens, ruins), green surround in leaf-on scenes; leaf-on dark-pit penalty.")]
            public double clutterPrior = 2.3, leafPrior = 1.5, leafDarkPenalty = 0.4;
            [Tooltip("Ring pit radius (px) at which a pit starts and fully counts: pits under ~2 m are left out.")]
            public double pitRadiusMin = 1.7, pitRadiusFull = 2.2;
            [Tooltip("Blob tests run at these multiples of the ring pit radius, the best kept; the extra ones only for pits of " +
                     "blobMultiRadiusMin px or more.")]
            public double[] blobRadiusScales = { 1.18, 0.9, 1.45 };
            public double blobMultiRadiusMin = 5;
            // blob tests, log(Y + 0.02) units unless noted: each a ramp from its lo to its hi value
            public double conLo = 0.38, conHi = 0.65, conzLo = 1.3, conzHi = 2.5, annuLo = 1.3, annuHi = 2.6;
            public double brightLo = 0.08, brightHi = 0.25, rel2Lo = 0.35, rel2Hi = 0.70, relminVeto = -0.25;
            public double angLo = 0.70, angHi = 0.92, edgeLo = 0.70, edgeHi = 0.90, lineLo = 0.5, lineHi = 0.8;
            public double coherLo = 0.35, coherHi = 0.55, qcoreLo = 0.22, qcoreHi = 0.45, coffLo = 0.15, coffHi = 0.40;
            public double ctxLo = 0.08, ctxHi = 0.13, ctxLoLeafOn = 0.07, ctxHiLeafOn = 0.10;
            public double ringLo = 0.25, ringHi = 0.50, ringLoLeafOn = 0.45, ringHiLeafOn = 0.80;
            public double vivLoLeafOn = 0.01, vivHiLeafOn = 0.04, satLoLeafOn = 0.22, satHiLeafOn = 0.35;
            public double darkLoLeafOn = 0.015, darkHiLeafOn = 0.035, darkLoLeafOff = 0.028, darkHiLeafOff = 0.060;
            public double vegGreen = 0.12;
            [Tooltip("Ring confidence that counts as agreement (mapped to 0.5): a crater needs this and a blob confidence of 0.5.")]
            public double ringThreshold = 0.4;
            [Tooltip("Craters with a lower confidence are dropped.")]
            public double confidenceMin = 0.5;
            [Tooltip("Leaf-off: blob confidence from which a crater is kept when the ring confidence is at least blobStrongRingMin.")]
            public double blobStrong = 0.85, blobStrongRingMin = 0.2;
            [Tooltip("Leaf-on: the ring mode (bright ejecta) counts alone on open ground: ring mode at least, clutter (mean |grad log Y| " +
                     "within 25 m), green share around, sunlit-leaf share within 25 m at most.")]
            public double ringModeMin = 0.6, ringModeClutter = 0.085, ringModeGreen = 0.10, ringModeLeaves = 0.05;
            [Tooltip("Fresh: a bright bare ejecta ring (grey levels above the far ground, share of directions, greenness at most).")]
            public double freshRingContrast = 18, freshRingShare = 0.75, freshMaxGreen = 0.12;
            [Tooltip("A dark patch of clusterMinRadius px or more holding two separate pits (smaller than clusterInner of it) gives way to them.")]
            public double clusterMinConfidence = 0.35, clusterInner = 0.6, clusterMinRadius = 4;
            [Tooltip("Duplicates: a crater closer than nmsFactor x the larger pit radius + 1 px to a stronger one is dropped.")]
            public double nmsFactor = 0.8;
            [Tooltip("Worker threads, 0 = all cores (the result does not depend on it).")]
            public int maxThreads = 0;
        }

        public struct Detection { public float x, z, pitDiameter, patchDiameter, confidence; public bool fresh, water; }

        // pixels: sRGB in Unity texture order (row 0 = south edge), width x height, covering area (xMin = west, yMin = south).
        // exclude: optional, same size and order, true = no crater centre there (buildings, water, roads). Diameters in metres;
        // patchDiameter is 0 where no disturbed patch could be measured. Sorted by confidence, highest first.
        public static List<Detection> Detect(Color32[] pixels, int width, int height, Rect area, bool[] exclude, Settings settings)
        {
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            if (width <= 0 || height <= 0 || pixels.Length != width * height) throw new ArgumentException("pixels must hold width x height colours");
            if (exclude != null && exclude.Length != pixels.Length) throw new ArgumentException("exclude must match the pixels");
            var s = settings ?? new Settings();
            var result = new List<Detection>();
            int margin = Math.Max(s.border, 1);
            if (width <= 2 * margin + 4 || height <= 2 * margin + 4) return result;
            var po = new ParallelOptions { MaxDegreeOfParallelism = s.maxThreads > 0 ? s.maxThreads : -1 };
            var w = new Work(pixels, width, height, s, po);
            w.Prepare();
            w.FindCandidates(exclude);
            if (w.n == 0) return result;
            w.MeasureRings();
            w.ScoreBlobs();
            w.Select(area, result);
            return result;
        }

        // ----------------------------------------------------------------------------------------------- constants
        const int ND = 16, NS = 47, NF = 257, BNT = 33, BNA = 24;
        static readonly double[] RAD = new double[NS], COS = new double[ND], SIN = new double[ND];   // ring profile: 47 radii, 16 directions
        static readonly double[] WF = new double[NF], CWF = new double[NF], TFR = new double[NF];    // 0.25 px grid of the scale curve
        static readonly int[] TI = new int[NF];
        static readonly double[] RHO;                                                                // scales 1.1^k up to 31 px
        static readonly int[] IR1, IR2;
        static readonly int[] PreAc = { 1, 1, 2, 3, 4, 6, 10 }, PreAi = { 2, 4, 5, 8, 12, 17, 24 }, PreAo = { 4, 6, 9, 14, 21, 30, 43 };
        static readonly double[] BT = new double[BNT], BCOS = new double[BNA], BSIN = new double[BNA]; // blob polar grid: t R, 24 angles
        static readonly float[] Lut = new float[256];                                                // sRGB -> linear

        static GeoCraterDetector()
        {
            int k = 0;
            for (int i = 0; i <= 20; i++) RAD[k++] = i * 0.5;
            for (int i = 11; i <= 20; i++) RAD[k++] = i;
            for (int i = 22; i <= 40; i += 2) RAD[k++] = i;
            for (int i = 44; i <= 64; i += 4) RAD[k++] = i;
            for (int j = 0; j < ND; j++) { double a = j * (2 * Math.PI / ND); COS[j] = Math.Cos(a); SIN[j] = Math.Sin(a); }
            double acc = 0;
            for (int i = 0; i < NF; i++)
            {
                double tf = i * 0.25;
                WF[i] = i == 0 ? 0.25 * 0.25 / 8 : tf * 0.25;
                acc += WF[i]; CWF[i] = acc;
                int c = 0;
                while (c < NS && RAD[c] <= tf) c++;
                TI[i] = Math.Min(Math.Max(c - 1, 0), NS - 2);
                TFR[i] = (tf - RAD[TI[i]]) / (RAD[TI[i] + 1] - RAD[TI[i]]);
            }
            var rho = new List<double>();
            for (int i = 0; i < 60; i++) { double r = Math.Pow(1.1, i); if (r <= 31.0) rho.Add(r); }
            RHO = rho.ToArray();
            IR1 = new int[RHO.Length]; IR2 = new int[RHO.Length];
            for (int i = 0; i < RHO.Length; i++) { IR1[i] = (int)Math.Round(RHO[i] / 0.25); IR2[i] = (int)Math.Round(2 * RHO[i] / 0.25); }
            for (int i = 0; i < BNT; i++) BT[i] = i * 0.25;
            for (int a = 0; a < BNA; a++) { double t = (a + 0.5) * (2 * Math.PI / BNA); BCOS[a] = Math.Cos(t); BSIN[a] = Math.Sin(t); }
            for (int c = 0; c < 256; c++)
            {
                double v = c / 255.0;
                Lut[c] = (float)(v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4));
            }
        }

        // ----------------------------------------------------------------------------------------------- small helpers
        // 0 at a, 1 at b (falling when a > b); NaN counts as a
        static double Ramp(double v, double a, double b)
        {
            if (double.IsNaN(v)) v = a;
            double t = (v - a) / (b - a);
            return t < 0 ? 0 : (t > 1 ? 1 : t);
        }

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        // numpy's linear quantile of the first n values (sorted in place); NaN when n is 0
        static double Quantile(double[] v, int n, double q)
        {
            if (n <= 0) return double.NaN;
            if (n <= 48)
                for (int i = 1; i < n; i++)
                {
                    double x = v[i];
                    int j = i - 1;
                    while (j >= 0 && v[j] > x) { v[j + 1] = v[j]; j--; }
                    v[j + 1] = x;
                }
            else Array.Sort(v, 0, n);
            double pos = q * (n - 1);
            int lo = (int)Math.Floor(pos);
            int hi = Math.Min(lo + 1, n - 1);
            double a = v[lo], b = v[hi], t = pos - lo, d = b - a;
            return t >= 0.5 ? b - d * (1.0 - t) : a + d * t;
        }

        static readonly int[] Net16 = OddEvenMergeNetwork(16);

        static int[] OddEvenMergeNetwork(int n)
        {
            var pairs = new List<int>();
            for (int p = 1; p < n; p <<= 1)
                for (int k = p; k >= 1; k >>= 1)
                    for (int j = k % p; j <= n - 1 - k; j += 2 * k)
                        for (int i = 0; i <= Math.Min(k - 1, n - j - k - 1); i++)
                            if ((i + j) / (2 * p) == (i + j + k) / (2 * p)) { pairs.Add(i + j); pairs.Add(i + j + k); }
            return pairs.ToArray();
        }

        static void Sort16(double[] v)
        {
            var net = Net16;
            for (int c = 0; c < net.Length; c += 2)
            {
                int i = net[c], j = net[c + 1];
                double a = v[i], b = v[j];
                v[i] = Math.Min(a, b); v[j] = Math.Max(a, b);
            }
        }

        static double Mean(double[] v, int n) { double s = 0; for (int i = 0; i < n; i++) s += v[i]; return s / n; }

        static double Std(double[] v, int n)
        {
            double m = Mean(v, n), s = 0;
            for (int i = 0; i < n; i++) { double d = v[i] - m; s += d * d; }
            return Math.Sqrt(s / n);
        }

        static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

        // bilinear sample, clamped to [0, size - 1.001] (so the integer cast is the floor)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double Sample(float[] img, int W, int H, double x, double y)
        {
            if (x < 0) x = 0; else if (x > W - 1.001) x = W - 1.001;
            if (y < 0) y = 0; else if (y > H - 1.001) y = H - 1.001;
            int x0 = (int)x, y0 = (int)y;
            double fx = x - x0, fy = y - y0;
            int i = y0 * W + x0;
            double a = img[i], b = img[i + 1], c = img[i + W], d = img[i + W + 1];
            return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
        }

        // bilinear sample of an image of c interleaved channels, channel ch (same weights and formula as Sample)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Weights(int W, int H, double x, double y, out int i, out double fx, out double fy)
        {
            if (x < 0) x = 0; else if (x > W - 1.001) x = W - 1.001;
            if (y < 0) y = 0; else if (y > H - 1.001) y = H - 1.001;
            int x0 = (int)x, y0 = (int)y;
            fx = x - x0; fy = y - y0;
            i = y0 * W + x0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double Lerp2(float[] img, int i, int dx, int dy, double fx, double fy) =>
            (img[i] * (1 - fx) + img[i + dx] * fx) * (1 - fy) + (img[i + dy] * (1 - fx) + img[i + dy + dx] * fx) * fy;

        static long Key(int ix, int iy) => ((long)ix << 32) ^ (uint)iy;

        // the radii of the ring profile with lo <= RAD <= hi are RAD[k0..k1] (none when k1 < k0)
        static void KRange(double lo, double hi, out int k0, out int k1)
        {
            k0 = 0;
            while (k0 < NS && !(RAD[k0] >= lo)) k0++;
            k1 = NS - 1;
            while (k1 >= 0 && !(RAD[k1] <= hi)) k1--;
        }

        // ----------------------------------------------------------------------------------------------- image filters
        static float[] GaussKernel(double sigma)
        {
            int r = Math.Max(1, (int)Math.Ceiling(3 * sigma));
            var k = new double[2 * r + 1];
            double sum = 0;
            for (int i = 0; i < k.Length; i++) { double x = i - r; k[i] = Math.Exp(-x * x / (2 * sigma * sigma)); sum += k[i]; }
            var f = new float[k.Length];
            for (int i = 0; i < k.Length; i++) f[i] = (float)(k[i] / sum);
            return f;
        }

        static int Reflect(int i, int n) { if (i < 0) i = -i; if (i > n - 1) i = 2 * (n - 1) - i; return i; }

        // separable Gaussian, mirror padding (d c b | a b c d | c b a), float accumulation in tap order
        static void Blur(float[] src, float[] dst, float[] tmp, int W, int H, double sigma, ParallelOptions po, int stride = 1, int offset = 0)
        {
            var k = GaussKernel(sigma);
            int r = k.Length / 2, nk = k.Length;
            Parallel.For(0, H, po, y =>
            {
                int row = y * W, x = 0;
                for (; x < r; x++) tmp[row + x] = BlurAt(src, row, x, W, k, r);
                for (; x + 3 < W - r; x += 4)
                {
                    float a0 = 0, a1 = 0, a2 = 0, a3 = 0;
                    int b = row + x - r;
                    for (int i = 0; i < nk; i++)
                    {
                        float ki = k[i];
                        a0 += ki * src[b + i]; a1 += ki * src[b + i + 1]; a2 += ki * src[b + i + 2]; a3 += ki * src[b + i + 3];
                    }
                    tmp[row + x] = a0; tmp[row + x + 1] = a1; tmp[row + x + 2] = a2; tmp[row + x + 3] = a3;
                }
                for (; x < W; x++) tmp[row + x] = BlurAt(src, row, x, W, k, r);
            });
            Parallel.For(0, H, po, () => new int[nk], (y, state, off) =>
            {
                for (int i = 0; i < nk; i++) off[i] = Reflect(y - r + i, H) * W;
                int row = y * W, x = 0;
                for (; x + 3 < W; x += 4)
                {
                    float a0 = 0, a1 = 0, a2 = 0, a3 = 0;
                    for (int i = 0; i < nk; i++)
                    {
                        float ki = k[i];
                        int o = off[i] + x;
                        a0 += ki * tmp[o]; a1 += ki * tmp[o + 1]; a2 += ki * tmp[o + 2]; a3 += ki * tmp[o + 3];
                    }
                    int d = (row + x) * stride + offset;
                    dst[d] = a0; dst[d + stride] = a1; dst[d + 2 * stride] = a2; dst[d + 3 * stride] = a3;
                }
                for (; x < W; x++)
                {
                    float acc = 0;
                    for (int i = 0; i < nk; i++) acc += k[i] * tmp[off[i] + x];
                    dst[(row + x) * stride + offset] = acc;
                }
                return off;
            }, off => { });
        }

        static float BlurAt(float[] src, int row, int x, int W, float[] k, int r)
        {
            float acc = 0;
            for (int i = 0; i < k.Length; i++) acc += k[i] * src[row + Reflect(x - r + i, W)];
            return acc;
        }

        // (2k+1)^2 min or max filter, edge padding
        static void MinMax(float[] src, float[] dst, float[] tmp, int W, int H, int k, bool max, ParallelOptions po)
        {
            Parallel.For(0, H, po, y =>
            {
                int row = y * W;
                for (int x = 0; x < W; x++)
                {
                    int a = Math.Max(x - k, 0), b = Math.Min(x + k, W - 1);
                    float m = src[row + a];
                    if (max) { for (int i = row + a + 1; i <= row + b; i++) m = Math.Max(m, src[i]); }
                    else { for (int i = row + a + 1; i <= row + b; i++) m = Math.Min(m, src[i]); }
                    tmp[row + x] = m;
                }
            });
            Parallel.For(0, H, po, y =>
            {
                int a = Math.Max(y - k, 0), b = Math.Min(y + k, H - 1), row = y * W;
                for (int x = 0; x < W; x++) dst[row + x] = tmp[a * W + x];
                for (int t = a + 1; t <= b; t++)
                {
                    int o = t * W;
                    if (max) { for (int x = 0; x < W; x++) dst[row + x] = Math.Max(dst[row + x], tmp[o + x]); }
                    else { for (int x = 0; x < W; x++) dst[row + x] = Math.Min(dst[row + x], tmp[o + x]); }
                }
            });
        }

        // centred (2r+1)^2 box mean with edge replication: differences of running sums in double (rows, then column
        // blocks walked row by row with a ring of the last 2r+2 running sums)
        static void BoxMean(float[] src, float[] dst, int W, int H, int r, ParallelOptions po)
        {
            var h = new double[W * H];
            int n = 2 * r + 1;
            Parallel.For(0, H, po, () => new double[W + n], (y, state, c) =>
            {
                int row = y * W;
                double acc = 0;
                for (int j = 0; j < W + n; j++)
                {
                    acc += src[row + Math.Min(Math.Max(j - (r + 1), 0), W - 1)];
                    c[j] = acc;
                }
                for (int x = 0; x < W; x++) h[row + x] = c[x + n] - c[x];
                return c;
            }, c => { });
            double area = (double)n * n;
            const int block = 64;
            int blocks = (W + block - 1) / block;
            Parallel.For(0, blocks, po, bi =>
            {
                int x0 = bi * block, bw = Math.Min(W, x0 + block) - x0;
                var acc = new double[bw];
                var ring = new double[(n + 1) * bw];
                for (int j = 0; j < H + n; j++)
                {
                    int src0 = Math.Min(Math.Max(j - (r + 1), 0), H - 1) * W + x0, slot = (j % (n + 1)) * bw;
                    for (int x = 0; x < bw; x++) { acc[x] += h[src0 + x]; ring[slot + x] = acc[x]; }
                    if (j < n) continue;
                    int old = ((j - n) % (n + 1)) * bw, o = (j - n) * W + x0;
                    for (int x = 0; x < bw; x++) dst[o + x] = (float)((ring[slot + x] - ring[old + x]) / area);
                }
            });
        }

        // integral image (W + 1) x (H + 1): cumulative sums down the columns, then along the rows
        static double[] Integral(float[] img, int W, int H, ParallelOptions po)
        {
            var ii = new double[(W + 1) * (H + 1)];
            const int block = 64;
            Parallel.For(0, (W + block - 1) / block, po, bi =>
            {
                int x0 = bi * block, x1 = Math.Min(W, x0 + block);
                for (int y = 0; y < H; y++)
                {
                    int row = (y + 1) * (W + 1) + 1, prev = y * (W + 1) + 1, src = y * W;
                    for (int x = x0; x < x1; x++) ii[row + x] = ii[prev + x] + img[src + x];
                }
            });
            Parallel.For(1, H + 1, po, y =>
            {
                int row = y * (W + 1);
                double acc = 0;
                for (int x = 1; x <= W; x++) { acc += ii[row + x]; ii[row + x] = acc; }
            });
            return ii;
        }

        // sum and count of the (2a+1)^2 box at (y, x), clipped to the image
        static void Box(double[] ii, int W, int H, int y, int x, int a, out double sum, out int count)
        {
            int y0 = Math.Min(Math.Max(y - a, 0), H), y1 = Math.Min(Math.Max(y + a + 1, 0), H);
            int x0 = Math.Min(Math.Max(x - a, 0), W), x1 = Math.Min(Math.Max(x + a + 1, 0), W);
            int s = W + 1;
            sum = ii[y1 * s + x1] - ii[y0 * s + x1] - ii[y1 * s + x0] + ii[y0 * s + x0];
            count = (y1 - y0) * (x1 - x0);
        }

        // ----------------------------------------------------------------------------------------------- per worker buffers
        sealed class Scratch
        {
            public readonly double[] P = new double[ND * NS], m = new double[NS], col = new double[ND], cs = new double[NF], D = new double[64];
            public readonly double[] r = new double[ND], rf = new double[ND], ringR = new double[ND], Bj = new double[ND], cj = new double[ND];
            public readonly double[] dirTex = new double[ND], wl = new double[32], X = new double[11], tmp = new double[512];
            public readonly bool[] has = new bool[ND], ringOk = new bool[ND];
            public readonly double[] LO = new double[BNT * BNA], LS = new double[BNT * BNA], G = new double[BNT * BNA];
            public readonly double[] sec = new double[8], ejsec = new double[8], gsec = new double[8], rel = new double[8];
            public readonly double[] angAnn = new double[BNA], angR2 = new double[BNA], angCore = new double[BNA], angEdge = new double[BNA];
            public readonly double[] rela = new double[BNA], relr2 = new double[BNA], dk = new double[BNA];
            public double rho, c0, top, tmax;
            public int kmin;
        }

        struct Blob { public double conf, tex25, viv25; public bool water; }

        // ----------------------------------------------------------------------------------------------- one image
        sealed class Work
        {
            readonly Color32[] pixels;
            readonly int W, H;
            readonly Settings s;
            readonly ParallelOptions po;
            float[] Lb, Lp, Lclose, tex25, viv25;
            float[] rgb3;    // blurred sRGB, interleaved r g b
            float[] blob3;   // opened log luminance (blur 0.7), log luminance (blur 0.7), greenness (blur 1), interleaved
            float[] grad2;   // gradients of the opened log luminance (blur 1), interleaved x y
            int[] order;     // candidates block by block (cache locality; results do not depend on it)
            bool[] exclude;  // texture order, may be null
            double[] iiLo;
            int[] iiGreen;
            int leaf;
            public int n;
            double[] cx, cy;
            double[] rbar, C, confR, ringMode, darkMode, ringc, ringFrac, exgS, rring, r85, greenFrac;
            double[] conf;
            bool[] waterB;

            public Work(Color32[] pixels, int width, int height, Settings settings, ParallelOptions po)
            {
                this.pixels = pixels; W = width; H = height; s = settings; this.po = po;
            }

            // the detector works north-up (row 0 = north); the pixels come in texture order (row 0 = south)
            int Src(int x, int y) => (H - 1 - y) * W + x;

            public void Prepare()
            {
                int N = W * H;
                var A = new float[N]; var B = new float[N]; var T = new float[N];
                var Y = new float[N]; var green = new float[N]; var Llog = new float[N];
                Parallel.For(0, H, po, y =>
                {
                    for (int x = 0; x < W; x++)
                    {
                        var c = pixels[Src(x, y)];
                        int i = y * W + x;
                        float r = c.r, g = c.g, b = c.b;
                        A[i] = 0.299f * r + 0.587f * g + 0.114f * b;
                        float lr = Lut[c.r], lg = Lut[c.g], lb = Lut[c.b];
                        float yy = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
                        Y[i] = yy;
                        green[i] = (lg - Math.Max(lr, lb)) / (lg + 0.01f);
                        Llog[i] = (float)Math.Log(yy + 0.02f);
                    }
                });
                int viv = 0, total = 0;
                for (int y = 0; y < H; y += 2)
                    for (int x = 0; x < W; x += 2)
                    {
                        int i = y * W + x;
                        if (green[i] > 0.45f && Y[i] > 0.02f) viv++;
                        total++;
                    }
                leaf = s.leafOn >= 0 ? (s.leafOn > 0 ? 1 : 0) : (viv / (double)total > s.vividShareLeafOn ? 1 : 0);

                // luma: opening 3x3 (water glints), blurs, closing 9x9 (context without small pits), integral image
                var Lo8 = new float[N];
                MinMax(A, B, T, W, H, 1, false, po);
                MinMax(B, Lo8, T, W, H, 1, true, po);
                Lb = new float[N]; Blur(Lo8, Lb, T, W, H, 1.0, po);
                Lp = new float[N]; Blur(Lo8, Lp, T, W, H, 0.7, po);
                MinMax(Lo8, A, T, W, H, 4, true, po);
                MinMax(A, B, T, W, H, 4, false, po);
                Lclose = new float[N]; Blur(B, Lclose, T, W, H, 1.0, po);
                iiLo = Integral(Lo8, W, H, po);
                Lo8 = null;
                // blurred sRGB channels and the integral of their ExG > 0.30 mask
                rgb3 = new float[3 * N];
                for (int ch = 0; ch < 3; ch++)
                {
                    int chan = ch;
                    Parallel.For(0, H, po, y =>
                    {
                        for (int x = 0; x < W; x++) { var c = pixels[Src(x, y)]; A[y * W + x] = chan == 0 ? c.r : chan == 1 ? c.g : c.b; }
                    });
                    Blur(A, rgb3, T, W, H, 0.7, po, 3, chan);
                }
                var gi = new int[(W + 1) * (H + 1)];
                Parallel.For(0, H, po, y =>
                {
                    int acc = 0, row = (y + 1) * (W + 1) + 1;
                    for (int x = 0; x < W; x++)
                    {
                        int i = 3 * (y * W + x);
                        float rr = rgb3[i], gg = rgb3[i + 1], bb = rgb3[i + 2];
                        if ((2 * gg - rr - bb) / (rr + gg + bb + 1) > 0.30f) acc++;
                        gi[row + x] = acc;
                    }
                });
                Parallel.For(0, (W + 63) / 64, po, bi =>
                {
                    int x0 = bi * 64 + 1, x1 = Math.Min(W, bi * 64 + 64);
                    for (int y = 2; y <= H; y++)
                    {
                        int row = y * (W + 1), prev = (y - 1) * (W + 1);
                        for (int x = x0; x <= x1; x++) gi[row + x] += gi[prev + x];
                    }
                });
                iiGreen = gi;
                // log luminance: blurred, opened + blurred, gradients and their 25 m mean (clutter)
                MinMax(Llog, B, T, W, H, 1, false, po);
                MinMax(B, A, T, W, H, 1, true, po);   // A = opened log luminance
                blob3 = new float[3 * N];
                Blur(A, blob3, T, W, H, 0.7, po, 3, 0);
                Blur(Llog, blob3, T, W, H, 0.7, po, 3, 1);
                Llog = null;
                Blur(A, B, T, W, H, 1.0, po);         // B = gradient source
                grad2 = new float[2 * N];
                var mag = A;
                var Lg = B;
                Parallel.For(0, H, po, y =>
                {
                    for (int x = 0; x < W; x++)
                    {
                        int i = y * W + x;
                        float dx = x >= 1 && x <= W - 2 ? 0.5f * (Lg[i + 1] - Lg[i - 1]) : 0f;
                        float dy = y >= 1 && y <= H - 2 ? -0.5f * (Lg[i + W] - Lg[i - W]) : 0f;
                        grad2[2 * i] = dx; grad2[2 * i + 1] = dy;
                        mag[i] = (float)Math.Sqrt(dx * dx + dy * dy);
                    }
                });
                tex25 = new float[N]; BoxMean(mag, tex25, W, H, 50, po);
                // greenness and the 25 m share of sunlit leaves
                Blur(green, blob3, T, W, H, 1.0, po, 3, 2);
                Parallel.For(0, H, po, y =>
                {
                    for (int x = 0; x < W; x++) { int i = y * W + x; A[i] = green[i] > 0.35f && Y[i] > 0.035f ? 1f : 0f; }
                });
                viv25 = new float[N]; BoxMean(A, viv25, W, H, 50, po);
            }

            public void FindCandidates(bool[] exclude)
            {
                this.exclude = exclude;
                var lm = new float[W * H];
                MinMax(Lb, lm, new float[W * H], W, H, 2, false, po);
                var found = new List<int>();
                int b = s.border;
                for (int y = b; y < H - b; y++)
                    for (int x = b; x < W - b; x++)
                    {
                        int i = y * W + x;
                        if (!(Lb[i] <= lm[i])) continue;
                        if (exclude != null && exclude[Src(x, y)]) continue;
                        double best = -1e9;
                        for (int q = 0; q < PreAc.Length; q++)
                        {
                            Box(iiLo, W, H, y, x, PreAc[q], out double sc, out int nc);
                            Box(iiLo, W, H, y, x, PreAi[q], out double si, out int ni);
                            Box(iiLo, W, H, y, x, PreAo[q], out double so, out int no);
                            double v = (so - si) / Math.Max(no - ni, 1) - sc / Math.Max(nc, 1);
                            if (v > best) best = v;
                        }
                        if (best >= s.candidateContrast) found.Add(i);
                    }
                Lb = null; iiLo = null;
                n = found.Count;
                cx = new double[n]; cy = new double[n];
                for (int k = 0; k < n; k++) { cx[k] = found[k] % W; cy[k] = found[k] / W; }
                int bw = (W + 31) / 32;
                var block = new long[n];
                order = new int[n];
                for (int k = 0; k < n; k++) { order[k] = k; block[k] = ((long)((found[k] / W) / 32) * bw + (found[k] % W) / 32) * n + k; }
                Array.Sort(block, order);
            }

            // ------------------------------------------------------------------------------------------- ring stage
            // radial profile, trimmed angular mean, scale curve, core and outside levels, per-direction half-level edges
            void Measure(Scratch sc, double x, double y)
            {
                var P = sc.P; var m = sc.m; var col = sc.col; var cs = sc.cs; var D = sc.D;
                if (x >= 65 && y >= 65 && x <= W - 66 && y <= H - 66)
                {
                    // the whole profile inside the image: no clamping, the same bilinear weights
                    var img = Lp;
                    for (int j = 0; j < ND; j++)
                    {
                        double cj = COS[j], sj = SIN[j];
                        for (int k = 0; k < NS; k++)
                        {
                            double px = x + cj * RAD[k], py = y + sj * RAD[k];
                            int x0 = (int)px, y0 = (int)py;
                            double fx = px - x0, fy = py - y0;
                            int i = y0 * W + x0;
                            P[j * NS + k] = (img[i] * (1 - fx) + img[i + 1] * fx) * (1 - fy) + (img[i + W] * (1 - fx) + img[i + W + 1] * fx) * fy;
                        }
                    }
                }
                else
                    for (int j = 0; j < ND; j++)
                        for (int k = 0; k < NS; k++)
                            P[j * NS + k] = Sample(Lp, W, H, x + COS[j] * RAD[k], y + SIN[j] * RAD[k]);
                for (int k = 0; k < NS; k++)
                {
                    for (int j = 0; j < ND; j++) col[j] = P[j * NS + k];
                    Sort16(col);
                    double sum = col[3];
                    for (int t = 4; t < 13; t++) sum += col[t];
                    m[k] = sum / 10.0;
                }
                double acc = 0;
                for (int i = 0; i < NF; i++)
                {
                    acc += (m[TI[i]] * (1 - TFR[i]) + m[TI[i] + 1] * TFR[i]) * WF[i];
                    cs[i] = acc;
                }
                int nr = RHO.Length;
                double dmax = double.NegativeInfinity;
                for (int q = 0; q < nr; q++)
                {
                    int i1 = IR1[q], i2 = IR2[q];
                    D[q] = (cs[i2] - cs[i1]) / (CWF[i2] - CWF[i1]) - cs[i1] / CWF[i1];
                    if (D[q] > dmax) dmax = D[q];
                }
                // the smallest local maximum within 75 % of the largest: one pit rather than a dark patch of several
                int pick = -1, amax = 0;
                for (int q = 0; q < nr; q++)
                {
                    if (D[q] > D[amax]) amax = q;
                    bool lmq = (q == 0 || D[q] >= D[q - 1]) && (q == nr - 1 || D[q] >= D[q + 1]);
                    if (pick < 0 && lmq && D[q] >= 0.75 * dmax && D[q] > 0) pick = q;
                }
                if (pick < 0) pick = amax;
                double rho = RHO[pick];
                double lim = Math.Max(1.0, 0.75 * rho);
                double c0 = double.PositiveInfinity;
                int kmin = 0;
                for (int k = 0; k < NS; k++)
                    if (RAD[k] <= lim && m[k] < c0) { c0 = m[k]; kmin = k; }
                double topSum = 0;
                int topN = 0;
                for (int k = 0; k < NS; k++)
                    if (RAD[k] >= 1.3 * rho && RAD[k] <= 2.0 * rho + 0.5) { topSum += m[k]; topN++; }
                double top = topSum / Math.Max(topN, 1);
                double tmax = Math.Min(2.2 * rho + 2.0, 64.0);
                double h = 0.5 * (c0 + top);
                for (int j = 0; j < ND; j++)
                {
                    sc.has[j] = false; sc.r[j] = double.NaN;
                    for (int k = kmin + 1; k < NS && RAD[k] <= tmax; k++)
                    {
                        if (P[j * NS + k] < h) continue;
                        double p1 = P[j * NS + k], p0 = P[j * NS + k - 1];
                        double frac = Clamp((h - p0) / Math.Max(p1 - p0, 1e-3), 0, 1);
                        sc.r[j] = RAD[k - 1] + frac * (RAD[k] - RAD[k - 1]);
                        sc.has[j] = true;
                        break;
                    }
                }
                sc.rho = rho; sc.c0 = c0; sc.top = top; sc.tmax = tmax; sc.kmin = kmin;
            }

            // moves the centre to the mean edge point when at least 12 rays closed; false when it stays put
            static bool Recenter(Scratch sc, ref double x, ref double y)
            {
                int cnt = 0;
                double ex = 0, ey = 0;
                for (int j = 0; j < ND; j++)
                {
                    double rr = sc.has[j] ? sc.r[j] : 0.0;
                    if (sc.has[j]) cnt++;
                    ex += rr * COS[j]; ey += rr * SIN[j];
                }
                ex /= Math.Max(cnt, 1); ey /= Math.Max(cnt, 1);
                double lim = 0.5 * sc.rho + 0.75;
                double d = Hypot(ex, ey);
                double f = d > lim ? lim / Math.Max(d, 1e-6) : 1.0;
                if (cnt < 12) return false;
                double nx = x + ex * f, ny = y + ey * f;
                bool moved = nx != x || ny != y;
                x = nx; y = ny;
                return moved;
            }

            public void MeasureRings()
            {
                rbar = new double[n]; C = new double[n]; confR = new double[n]; ringMode = new double[n]; darkMode = new double[n];
                ringc = new double[n]; ringFrac = new double[n]; exgS = new double[n]; rring = new double[n]; r85 = new double[n];
                greenFrac = new double[n];
                Parallel.For(0, n, po, () => new Scratch(), (oi, state, sc) =>
                {
                    int i = order[oi];
                    double x = cx[i], y = cy[i];
                    Measure(sc, x, y);
                    // a centre that stays put would measure the same again
                    for (int it = 0; it < s.recenterIterations && Recenter(sc, ref x, ref y); it++) Measure(sc, x, y);
                    cx[i] = x; cy[i] = y;
                    RingScore(sc, i, x, y);
                    return sc;
                }, sc => { });
            }

            void RingScore(Scratch sc, int i, double x, double y)
            {
                var P = sc.P; var m = sc.m; var rf = sc.rf; var t = sc.tmp;
                double c0 = sc.c0, top = sc.top;
                // shape of the edge
                int ncl = 0;
                for (int j = 0; j < ND; j++) { rf[j] = sc.has[j] ? sc.r[j] : sc.tmax; if (sc.has[j]) ncl++; }
                double rbr = Mean(rf, ND);
                Array.Copy(rf, t, ND);
                double rmed = Quantile(t, ND, 0.5);
                double mex = 0, mey = 0;
                for (int j = 0; j < ND; j++) { mex += rf[j] * COS[j]; mey += rf[j] * SIN[j]; }
                mex /= ND; mey /= ND;
                double sxx = 0, syy = 0, sxy = 0;
                for (int j = 0; j < ND; j++)
                {
                    double dx = rf[j] * COS[j] - mex, dy = rf[j] * SIN[j] - mey;
                    sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
                }
                sxx /= ND; syy /= ND; sxy /= ND;
                double tr = sxx + syy, det = sxx * syy - sxy * sxy;
                double dsc = Math.Sqrt(Math.Max(tr * tr / 4 - det, 0));
                double l1 = tr / 2 + dsc, l2 = Math.Max(tr / 2 - dsc, 0);
                double q = Math.Sqrt(l2 / Math.Max(l1, 1e-6));
                double Cc = top - c0;
                double rb = Math.Max(rbr, 1.0);
                double cv2 = Std(rf, ND) / (rbr + 1.0);
                // per-direction outside level over [1.3 rb, 2 rb + 0.5]: weakest side, spread, one-sidedness
                double lo = 1.3 * rb, hi = Math.Min(2.0 * rb + 0.5, 64);
                KRange(lo, hi, out int ka, out int kb);
                for (int j = 0; j < ND; j++)
                {
                    int c = 0;
                    for (int k = ka; k <= kb; k++) t[c++] = P[j * NS + k];
                    sc.Bj[j] = Quantile(t, c, 0.5);
                }
                Array.Copy(sc.Bj, t, ND);
                double B = Quantile(t, ND, 0.5);
                double cmin = double.PositiveInfinity, a1x = 0, a1y = 0;
                for (int j = 0; j < ND; j++)
                {
                    sc.cj[j] = sc.Bj[j] - c0;
                    if (sc.cj[j] < cmin) cmin = sc.cj[j];
                    t[j] = Math.Abs(sc.Bj[j] - B);
                }
                double angsd = 1.4826 * Quantile(t, ND, 0.5);
                for (int j = 0; j < ND; j++) { double v = double.IsNaN(sc.cj[j]) ? 0.0 : sc.cj[j]; a1x += v * COS[j]; a1y += v * SIN[j]; }
                a1x *= 2.0 / ND; a1y *= 2.0 / ND;
                double A1 = Hypot(a1x, a1y) / Math.Max(Cc, 1.0);
                // texture: radial std over [1.3 rb, 2.6 rb + 1] per direction, lower quartile over the directions
                hi = Math.Min(2.6 * rb + 1.0, 64);
                KRange(lo, hi, out ka, out kb);
                for (int j = 0; j < ND; j++)
                {
                    int c = 0;
                    for (int k = ka; k <= kb; k++) t[c++] = P[j * NS + k];
                    sc.dirTex[j] = c > 0 ? Std(t, c) : double.NaN;
                }
                Array.Copy(sc.dirTex, t, ND);
                double tex = Quantile(t, ND, 0.25);
                // far level over [3 rb + 2, 5 rb + 6]
                double farv;
                {
                    int c = 0;
                    double flo = 3.0 * rb + 2, fhi = Math.Min(5.0 * rb + 6, 64);
                    KRange(flo, fhi, out ka, out kb);
                    for (int k = ka; k <= kb; k++) t[c++] = m[k];
                    double fv = Quantile(t, c, 0.5);
                    farv = double.IsNaN(fv) ? top : fv;
                }
                // bright (ejecta) ring over [rb, 3 rb + 2]: its peak, extent, completeness and evenness
                double rlo = rb, rhi = 3.0 * rb + 2;
                KRange(rlo, rhi, out int ra, out int rbk);
                int kring = 0;
                double best = double.NegativeInfinity;
                for (int k = ra; k <= rbk; k++) if (m[k] > best) { best = m[k]; kring = k; }
                double ringpk = m[kring];
                double ringC = ringpk - farv;
                double hh = 0.5 * (ringpk + farv);
                double rRing = double.NaN;
                for (int k = kring + 1; k < NS; k++) if (m[k] <= hh) { rRing = RAD[k]; break; }
                int nok = 0;
                double sumOk = 0, need = Math.Max(6.0, 0.5 * ringC);
                for (int j = 0; j < ND; j++)
                {
                    int kp = 0;
                    double bp = double.NegativeInfinity;
                    for (int k = ra; k <= rbk; k++)
                        if (P[j * NS + k] > bp) { bp = P[j * NS + k]; kp = k; }
                    bool ok = P[j * NS + kp] - farv >= need;
                    sc.ringOk[j] = ok;
                    sc.ringR[j] = RAD[kp];
                    if (ok) { nok++; sumOk += RAD[kp]; }
                }
                double frac = nok / (double)ND;
                double meanOk = sumOk / Math.Max(nok, 1), varOk = 0;
                for (int j = 0; j < ND; j++) if (sc.ringOk[j]) { double d = sc.ringR[j] - meanOk; varOk += d * d; }
                varOk /= Math.Max(nok, 1);
                double ringCv = nok > 0 ? Math.Sqrt(varOk) / Math.Max(meanOk, 1e-3) : 9.0;
                // core texture (water is smooth, crowns are not)
                double clim = Math.Max(0.6 * rb, 1.0);
                int ncore = 0;
                double csum = 0;
                KRange(double.NegativeInfinity, clim, out int c0k, out int c1k);
                for (int j = 0; j < ND; j++) for (int k = c0k; k <= c1k; k++) { csum += P[j * NS + k]; ncore++; }
                double cmean = csum / ncore, cvar = 0;
                for (int j = 0; j < ND; j++) for (int k = c0k; k <= c1k; k++) { double d = P[j * NS + k] - cmean; cvar += d * d; }
                double coreSd = Math.Sqrt(cvar / ncore);
                double dark = c0 / Math.Max(farv, 1.0);
                // darkened patch: the radius where the darkening recovered 85 % of the way to the far level
                double lev85 = c0 + 0.85 * (farv - c0);
                int k85 = -1;
                for (int k = 0; k < NS; k++) if (RAD[k] >= rmed && RAD[k] <= 3.0 * rb + 2.0 && m[k] >= lev85) { k85 = k; break; }
                double rPatch = double.NaN;
                if (k85 >= 0 && farv - c0 > 6)
                {
                    int kp = Math.Max(k85 - 1, 0);
                    double f85 = Clamp((lev85 - m[kp]) / Math.Max(m[k85] - m[kp], 1e-3), 0, 1);
                    rPatch = Math.Max(RAD[kp] + f85 * (RAD[k85] - RAD[kp]), rbr);
                }
                int kedge = 0;
                while (kedge < NS && RAD[kedge] < rmed) kedge++;
                kedge = Math.Min(Math.Max(kedge, 1), NS - 2);
                double slope = (m[kedge + 1] - m[kedge - 1]) / (RAD[kedge + 1] - RAD[kedge - 1]);
                double width = Cc / Math.Max(slope, 0.05);
                // colour of the core and the surround, the sun side, wide context
                ColourAt(x, y, rb, 0.0, 0.35, 0.7, 0.0, out double cr, out double cg, out double cbl);
                ColourAt(x, y, rb, 1.4, 1.8, 2.2, 0.5, out double sr, out double sg, out double sbl);
                double exgC = (2 * cg - cr - cbl) / (cr + cg + cbl + 1);
                double exgSv = (2 * sg - sr - sbl) / (sr + sg + sbl + 1);
                double az = s.sunAzimuth * (Math.PI / 180.0);
                double sinAz = Math.Sin(az), negCosAz = -Math.Cos(az);
                double wsum = 0, esum = 0, rad18 = rb * 1.8 + 0.5;
                for (int j = 0; j < ND; j++)
                {
                    double wsun = Math.Max(COS[j] * sinAz + SIN[j] * negCosAz, 0);
                    wsun *= wsun;
                    Weights(W, H, x + COS[j] * rad18, y + SIN[j] * rad18, out int wi, out double fx, out double fy);
                    double jr = Lerp2(rgb3, 3 * wi, 3, 3 * W, fx, fy), jg = Lerp2(rgb3, 3 * wi + 1, 3, 3 * W, fx, fy), jb = Lerp2(rgb3, 3 * wi + 2, 3, 3 * W, fx, fy);
                    esum += (2 * jg - jr - jb) / (jr + jg + jb + 1) * wsun;
                    wsum += wsun;
                }
                double exgSun = esum / wsum;
                int gcount = 0;
                for (int ri = 0; ri < 2; ri++)
                {
                    double rad = ri == 0 ? rb * 3.0 + 3.0 : rb * 5.0 + 5.0;
                    for (int j = 0; j < ND; j++)
                    {
                        Weights(W, H, x + rad * COS[j], y + rad * SIN[j], out int wi, out double fx, out double fy);
                        double wr = Lerp2(rgb3, 3 * wi, 3, 3 * W, fx, fy), wg = Lerp2(rgb3, 3 * wi + 1, 3, 3 * W, fx, fy), wb = Lerp2(rgb3, 3 * wi + 2, 3, 3 * W, fx, fy);
                        if ((2 * wg - wr - wb) / (wr + wg + wb + 1) > 0.20) gcount++;
                        sc.wl[ri * ND + j] = 0.299 * wr + 0.587 * wg + 0.114 * wb;
                        t[ri * ND + j] = Lerp2(Lclose, wi, 1, W, fx, fy);
                    }
                }
                double gFrac = gcount / 32.0;
                double wideSd = Std(sc.wl, 32), wideSdc = Std(t, 32);
                int gyi = (int)Math.Round(y), gxi = (int)Math.Round(x);
                int y0 = Math.Min(Math.Max(gyi - 205, 0), H), y1 = Math.Min(Math.Max(gyi + 206, 0), H);
                int x0 = Math.Min(Math.Max(gxi - 205, 0), W), x1 = Math.Min(Math.Max(gxi + 206, 0), W);
                int st = W + 1;
                double green200 = (double)(iiGreen[y1 * st + x1] - iiGreen[y0 * st + x1] - iiGreen[y1 * st + x0] + iiGreen[y0 * st + x0])
                                  / Math.Max((y1 - y0) * (x1 - x0), 1);

                // ring confidence: classifier + context prior, strong-pit mode, vetoes; bright-ejecta ring mode
                double lf = leaf;
                double Cp = Math.Max(Cc, 1.0), rbp = Math.Max(rbr, 0.5), farv1 = Math.Max(farv, 1.0);
                bool water = exgC >= 0.22 && exgSv <= 0.10 && cg > cr && ringC >= 12 && frac >= 0.7 && dark >= 0.4;
                double veg = water ? 1.0 : 1.0 - lf * Ramp(exgC, 0.22, 0.30);
                double concrete = 1.0 - Ramp(top, 135.0, 155.0);
                double vegsun = 1.0 - lf * Ramp(exgSun, 0.24, 0.32);
                double castshadow = 1.0 - lf * (1.0 - Ramp(dark, 0.25, 0.35));
                double size = Ramp(rbr, s.pitRadiusMin, s.pitRadiusFull);
                double big = Ramp(Cc - 2.5 * Math.Max(rbr - 4.5, 0.0), 10.0, 14.0);
                double wsdcRel = (double.IsNaN(wideSdc) ? 99.0 : wideSdc) / farv1;
                double prior = -s.clutterPrior * Ramp(wsdcRel, 0.12, 0.25)
                               - lf * (1.5 * Ramp(gFrac, 0.15, 0.45) + s.leafPrior * Ramp(green200, 0.04, 0.12));
                double angsdRel = (double.IsNaN(angsd) ? 99.0 : angsd) / Cp;
                double cminRel = (double.IsNaN(cmin) ? -99.0 : cmin) / Cp;
                double strong = Ramp(Cc, 28.0, 40.0) * Ramp(-cv2, -0.20, -0.12) * Ramp(q, 0.78, 0.88) * Ramp(cminRel, 0.40, 0.60)
                                * Ramp(ncl, 15.5, 16.0) * Ramp(-angsdRel, -0.35, -0.20) * (1.0 - lf);
                double ring = Ramp(ringC, 12.0, 22.0) * Ramp(frac, 0.70, 0.90) * Ramp(-ringCv, -0.35, -0.22) * Ramp(-wideSd, -26.0, -20.0)
                              * Ramp(cminRel, 0.25, 0.45) * Ramp(-cv2, -0.30, -0.20) * Ramp(ncl, 15.0, 16.0) * Ramp(-exgSv, -0.14, -0.09)
                              * Ramp(rbr, 2.0, 2.6) * big;
                var X = sc.X;
                X[0] = 16.0 - ncl;
                X[1] = cv2;
                X[2] = q;
                X[3] = Clamp(cminRel, -1, 1);
                X[4] = Clamp(A1, 0, 2);
                X[5] = Clamp(width / (rbp + 1.0), 0, 5);
                X[6] = (double.IsNaN(wideSdc) ? 30.0 : wideSdc) / farv1;
                X[7] = Clamp((double.IsNaN(angsd) ? 30.0 : angsd) / Cp, 0, 3);
                X[8] = Clamp((double.IsNaN(tex) ? 10.0 : tex) / Cp, 0, 2);
                X[9] = Clamp((double.IsNaN(coreSd) ? 10.0 : coreSd) / Cp, 0, 2);
                X[10] = Math.Log(rbp);
                double z = 0;
                for (int f = 0; f < 11; f++) z += (X[f] - s.classifierMean[f]) / s.classifierScale[f] * s.classifierWeights[f + 1];
                double cls = 1.0 / (1.0 + Math.Exp(-(s.classifierWeights[0] + z)));
                double cl = Clamp(cls, 1e-6, 1 - 1e-6);
                double lg = Math.Log(cl / (1 - cl)) + prior;
                double dm = Math.Max(1.0 / (1.0 + Math.Exp(-lg)), strong);
                dm *= veg; dm *= vegsun; dm *= castshadow; dm *= concrete; dm *= size; dm *= big;
                dm *= 1.0 - s.leafDarkPenalty * lf;
                bool gate = Cc >= s.gateContrast && ncl >= s.gateClosedRays && q >= s.gateRoundness && cv2 <= s.gateEdgeSpread
                            && rbr >= s.gateRadiusMin && rbr <= s.gateRadiusMax;
                rbar[i] = rbr; C[i] = Cc;
                confR[i] = gate ? Math.Max(dm, ring) : 0.0;
                ringMode[i] = ring; darkMode[i] = dm;
                ringc[i] = ringC; ringFrac[i] = frac; exgS[i] = exgSv; rring[i] = rRing; r85[i] = rPatch; greenFrac[i] = gFrac;
            }

            void ColourAt(double x, double y, double rb, double f0, double f1, double f2, double add, out double r, out double g, out double b)
            {
                double sr = 0, sg = 0, sb = 0;
                for (int fi = 0; fi < 3; fi++)
                {
                    double rad = rb * (fi == 0 ? f0 : fi == 1 ? f1 : f2) + add;
                    for (int j = 0; j < ND; j++)
                    {
                        Weights(W, H, x + rad * COS[j], y + rad * SIN[j], out int wi, out double fx, out double fy);
                        sr += Lerp2(rgb3, 3 * wi, 3, 3 * W, fx, fy); sg += Lerp2(rgb3, 3 * wi + 1, 3, 3 * W, fx, fy); sb += Lerp2(rgb3, 3 * wi + 2, 3, 3 * W, fx, fy);
                    }
                }
                r = sr / 48; g = sg / 48; b = sb / 48;
            }

            // ------------------------------------------------------------------------------------------- blob stage
            // bilinear sample of a linear-colour channel straight from the pixels (as the reference's float32 image)
            double SampleLinear(int channel, double x, double y)
            {
                if (x < 0) x = 0; else if (x > W - 1.001) x = W - 1.001;
                if (y < 0) y = 0; else if (y > H - 1.001) y = H - 1.001;
                int x0 = (int)x, y0 = (int)y;
                double fx = x - x0, fy = y - y0;
                double a = Channel(pixels[Src(x0, y0)], channel), b = Channel(pixels[Src(x0 + 1, y0)], channel);
                double c = Channel(pixels[Src(x0, y0 + 1)], channel), d = Channel(pixels[Src(x0 + 1, y0 + 1)], channel);
                return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
            }

            static float Channel(Color32 c, int channel) => Lut[channel == 0 ? c.r : channel == 1 ? c.g : c.b];

            Blob BlobAt(Scratch sc, double x, double y, double R)
            {
                var LO = sc.LO; var LS = sc.LS; var G = sc.G; var t = sc.tmp;
                for (int ti = 0; ti < BNT; ti++)
                {
                    double r = R * BT[ti];
                    for (int a = 0; a < BNA; a++)
                    {
                        Weights(W, H, x + r * BCOS[a], y - r * BSIN[a], out int wi, out double fx, out double fy);
                        int k = ti * BNA + a;
                        LO[k] = Lerp2(blob3, 3 * wi, 3, 3 * W, fx, fy); LS[k] = Lerp2(blob3, 3 * wi + 1, 3, 3 * W, fx, fy);
                        G[k] = Lerp2(blob3, 3 * wi + 2, 3, 3 * W, fx, fy);
                    }
                }
                double c0 = RangeMean(LO, 0, 3), ann = RangeMean(LO, 6, 11);
                int c = Copy(LO, 16, 29, t);
                double bg = Quantile(t, c, 0.5);
                for (int k = 0; k < 8; k++)
                {
                    sc.sec[k] = SectorMean(LO, 6, 11, k);
                    sc.ejsec[k] = SectorMean(LO, 5, 13, k) - bg;
                    sc.gsec[k] = SectorMean(G, 6, 11, k);
                }
                for (int a = 0; a < BNA; a++)
                {
                    sc.angAnn[a] = AngleMean(LO, 6, 11, a); sc.angR2[a] = AngleMean(LO, 10, 17, a);
                    sc.angCore[a] = AngleMean(LO, 2, 6, a); sc.angEdge[a] = AngleMean(LO, 4, 8, a);
                }
                // second moments of the dark core: isotropy and centroid offset
                double lvl = 0.5 * (c0 + ann);
                double cxx = 0, cyy = 0, cxy = 0, wsx = 0, wsy = 0, ws = 0;
                for (int ti = 0; ti < 6; ti++)
                    for (int a = 0; a < BNA; a++)
                    {
                        double wc = Math.Max(lvl - LO[ti * BNA + a], 0) * BT[ti];
                        double xc = BT[ti] * BCOS[a], yc = BT[ti] * BSIN[a];
                        cxx += wc * xc * xc; cyy += wc * yc * yc; cxy += wc * xc * yc;
                        wsx += wc * xc; wsy += wc * yc; ws += wc;
                    }
                double ctr = cxx + cyy;
                double cdisc = Math.Sqrt(Math.Max(ctr * ctr / 4 - (cxx * cyy - cxy * cxy), 0));
                double qcore = (ctr / 2 - cdisc) / Math.Max(ctr / 2 + cdisc, 1e-9);
                double coff = Hypot(wsx, wsy) / Math.Max(ws, 1e-9);
                double ej = RangeMean(LO, 5, 13) - bg;
                c = Copy(LS, 6, 11, t);
                double texAnn = Std(t, c);
                c = Copy(LO, 10, 29, t);
                double rq50 = Quantile(t, c, 0.5), rq90 = Quantile(t, c, 0.9);
                c = Copy(LO, 6, 11, t);
                double aq50 = Quantile(t, c, 0.5), aq90 = Quantile(t, c, 0.9);
                int nb = 0;
                for (int k = 6 * BNA; k < 21 * BNA; k++) if (LO[k] > rq50 + 0.35) nb++;
                double brightFrac = nb / (15.0 * BNA);
                double jxx = 0, jyy = 0, jxy = 0;
                for (int ti = 5; ti < 21; ti++)
                {
                    double r = R * BT[ti];
                    for (int a = 0; a < BNA; a++)
                    {
                        Weights(W, H, x + r * BCOS[a], y - r * BSIN[a], out int wi, out double fx, out double fy);
                        double ggx = Lerp2(grad2, 2 * wi, 2, 2 * W, fx, fy), ggy = Lerp2(grad2, 2 * wi + 1, 2, 2 * W, fx, fy);
                        jxx += ggx * ggx; jyy += ggy * ggy; jxy += ggx * ggy;
                    }
                }
                jxx /= 16.0 * BNA; jyy /= 16.0 * BNA; jxy /= 16.0 * BNA;
                double coher = Math.Sqrt((jxx - jyy) * (jxx - jyy) + 4 * jxy * jxy) / Math.Max(jxx + jyy, 1e-9);
                double gIn = RangeMean(G, 0, 5), gC = RangeMean(G, 0, 3), gAnn = RangeMean(G, 6, 11), gEj = RangeMean(G, 5, 13);
                c = Copy(G, 16, 29, t);
                double gBg = Quantile(t, c, 0.5);
                double rc = 0, gc = 0, bc = 0, ra = 0, ga = 0, ba = 0;
                for (int ti = 0; ti < 11; ti++)
                {
                    if (ti >= 3 && ti < 6) continue;
                    double r = R * BT[ti];
                    for (int a = 0; a < BNA; a++)
                    {
                        double px = x + r * BCOS[a], py = y - r * BSIN[a];
                        double lr = SampleLinear(0, px, py), lgv = SampleLinear(1, px, py), lb = SampleLinear(2, px, py);
                        if (ti < 3) { rc += lr; gc += lgv; bc += lb; } else { ra += lr; ga += lgv; ba += lb; }
                    }
                }
                rc /= 3 * BNA; gc /= 3 * BNA; bc /= 3 * BNA; ra /= 5 * BNA; ga /= 5 * BNA; ba /= 5 * BNA;
                int xi = Math.Min(Math.Max((int)Math.Round(x), 0), W - 1), yi = Math.Min(Math.Max((int)Math.Round(y), 0), H - 1);
                double t25 = tex25[yi * W + xi], v25 = viv25[yi * W + xi];
                // derived features
                double con = ann - c0, cn = Math.Max(con, 1e-3);
                for (int k = 0; k < 8; k++) sc.rel[k] = (sc.sec[k] - c0) / cn;
                Array.Sort(sc.rel, 0, 8);
                double relmin = sc.rel[0], rel2 = sc.rel[1];
                int nang = 0;
                for (int a = 0; a < BNA; a++)
                {
                    sc.rela[a] = (sc.angAnn[a] - c0) / cn; sc.relr2[a] = (sc.angR2[a] - c0) / cn;
                    if (sc.rela[a] > 0.5) nang++;
                }
                double angfrac = nang / (double)BNA;
                double line = Math.Max(LineOf(sc.rela, sc.dk), LineOf(sc.relr2, sc.dk));
                int ejn = 0;
                for (int k = 0; k < 8; k++) if (sc.ejsec[k] > 0.06) ejn++;
                double ejcv = Std(sc.ejsec, 8) / Math.Max(Mean(sc.ejsec, 8), 0.05);
                double sa = ((90.0 - s.sunAzimuth) % 360.0 + 360.0) % 360.0;
                int sunSector = ((int)Math.Round((sa - 22.5) / 45.0) % 8 + 8) % 8, antiSector = (sunSector + 4) % 8;
                double asymG = sc.gsec[sunSector] - sc.gsec[antiSector], gSun = sc.gsec[sunSector];
                double Yc = Math.Max(Math.Exp(c0) - 0.02, 0.0);
                double Yann = ra * 0.2126 + ga * 0.7152 + ba * 0.0722;
                double bOverR = (bc + 0.002) / (rc + 0.002);
                double amax = Math.Max(ra, Math.Max(ga, ba)), amin = Math.Min(ra, Math.Min(ga, ba));
                double satAnn = (amax - amin) / Math.Max(amax, 1e-3);
                double conzUp = con / Math.Max(rq90 - rq50, 0.04), annu = con / Math.Max(aq90 - aq50, 0.05);
                int ne = 0;
                for (int a = 0; a < BNA; a++) if (sc.angEdge[a] - sc.angCore[a] > 0.2 * cn) ne++;
                double edgeCons = ne / (double)BNA;

                // tests: geometric mean (product ^ 1/3), and the fresh-ring pathway
                bool lf = leaf > 0;
                double sSec = Ramp(rel2, s.rel2Lo, s.rel2Hi) * (relmin > s.relminVeto ? 1.0 : 0.0);
                double sAng = Ramp(angfrac, s.angLo, s.angHi), sLine = Ramp(line, s.lineHi, s.lineLo), sCoher = Ramp(coher, s.coherHi, s.coherLo);
                double sRound = Ramp(qcore, s.qcoreLo, s.qcoreHi), sCoff = Ramp(coff, s.coffHi, s.coffLo);
                double sGrey = Ramp(satAnn, s.satLoLeafOn, s.satHiLeafOn), sFol = Ramp(v25, s.vivHiLeafOn, s.vivLoLeafOn);
                bool freshRing = ej > 0.10 && ejn >= 5 && gEj < 0.10;
                bool strongRing = ej > 0.30 && ejn >= 7 && gEj < 0.05 && texAnn < 0.30;
                bool waterCol = gC > 0.15 && Yc < 0.10 && bOverR > 0.45;
                bool bareRing = Yc < 0.035 && gAnn < gC - 0.10 && Yann > 2.5 * Yc;
                bool waterLike = waterCol && (lf ? strongRing : (freshRing || bareRing));
                double veg = Ramp(gIn, s.vegGreen, s.vegGreen + 0.12) * Ramp(gIn - gBg, 0.0, 0.10);
                veg = lf ? Math.Max(veg, Ramp(gAnn, 0.12, 0.25)) : veg * Ramp(Yc, 0.03, 0.06);
                double shadow = Ramp(asymG, 0.06, 0.18) * Ramp(gSun, 0.08, 0.2);
                double blueSh = Ramp(bOverR, 1.05, 1.4) * Ramp(Yc, 0.03, 0.012) * (waterLike ? 0.0 : 1.0);
                if (lf) blueSh = Math.Max(blueSh, Ramp(Yc, s.darkHiLeafOn, s.darkLoLeafOn) * (waterLike ? 0.0 : 1.0));
                double prod = 1;
                prod *= Math.Max(Ramp(con, s.conLo, s.conHi), 1e-6);
                prod *= Math.Max(Ramp(conzUp, s.conzLo, s.conzHi), 1e-6);
                prod *= Math.Max(Ramp(annu, s.annuLo, s.annuHi), 1e-6);
                prod *= Math.Max(Ramp(brightFrac, s.brightHi, s.brightLo), 1e-6);
                prod *= Math.Max(sSec, 1e-6);
                prod *= Math.Max(sAng, 1e-6);
                prod *= Math.Max(Ramp(edgeCons, s.edgeLo, s.edgeHi), 1e-6);
                prod *= Math.Max(sLine, 1e-6);
                prod *= Math.Max(sCoher, 1e-6);
                prod *= Math.Max(sRound, 1e-6);
                prod *= Math.Max(sCoff, 1e-6);
                if (lf)
                {
                    prod *= Math.Max(Ramp(t25, s.ctxHiLeafOn, s.ctxLoLeafOn), 1e-6);
                    prod *= Math.Max(sGrey, 1e-6);
                    prod *= Math.Max(sFol, 1e-6);
                }
                else
                {
                    prod *= Math.Max(Ramp(t25, s.ctxHi, s.ctxLo), 1e-6);
                    prod *= Math.Max(Ramp(Yc, s.darkHiLeafOff, s.darkLoLeafOff), 1e-6);
                }
                prod *= Math.Max(1.0 - (waterLike ? 0.0 : veg), 1e-6);
                prod *= Math.Max(1.0 - Math.Max(shadow, blueSh), 1e-6);
                double conf = Math.Pow(prod, 1.0 / 3.0);
                double rlo = lf ? s.ringLoLeafOn : s.ringLo, rhi = lf ? s.ringHiLeafOn : s.ringHi;
                prod = 1;
                prod *= Math.Max(Ramp(ej, rlo, rhi) * Ramp(ejn, 5.5, 7.5), 1e-6);
                prod *= Math.Max(Ramp(ejcv, 0.6, 0.3), 1e-6);
                prod *= Math.Max(Ramp(gEj, 0.08, 0.0), 1e-6);
                prod *= Math.Max(Ramp(con, 0.40, 0.70), 1e-6);
                prod *= Math.Max(sSec, 1e-6);
                prod *= Math.Max(sAng, 1e-6);
                prod *= Math.Max(sLine, 1e-6);
                prod *= Math.Max(sRound, 1e-6);
                prod *= Math.Max(sCoff, 1e-6);
                prod *= Math.Max(sCoher, 1e-6);
                prod *= Math.Max(lf ? Ramp(t25, 0.09, 0.06) : Ramp(t25, 0.12, 0.08), 1e-6);
                if (lf) { prod *= Math.Max(sGrey, 1e-6); prod *= Math.Max(sFol, 1e-6); }
                double confRing = Math.Pow(prod, 1.0 / 3.0);
                bool ringPath = confRing > conf && confRing >= 0.5;
                waterLike = waterLike || (ringPath && gC > 0.15 && bOverR > 0.45 && Yc < 0.20);
                return new Blob { conf = Math.Max(conf, confRing), water = waterLike, tex25 = t25, viv25 = v25 };
            }

            static double RangeMean(double[] v, int t0, int t1)
            {
                double s = 0;
                for (int k = t0 * BNA; k < t1 * BNA; k++) s += v[k];
                return s / ((t1 - t0) * BNA);
            }

            static int Copy(double[] v, int t0, int t1, double[] dst)
            {
                int c = 0;
                for (int k = t0 * BNA; k < t1 * BNA; k++) dst[c++] = v[k];
                return c;
            }

            static double SectorMean(double[] v, int t0, int t1, int sector)
            {
                double s = 0;
                for (int ti = t0; ti < t1; ti++) for (int a = 3 * sector; a < 3 * sector + 3; a++) s += v[ti * BNA + a];
                return s / ((t1 - t0) * 3);
            }

            static double AngleMean(double[] v, int t0, int t1, int a)
            {
                double s = 0;
                for (int ti = t0; ti < t1; ti++) s += v[ti * BNA + a];
                return s / (t1 - t0);
            }

            // strongest pair of opposite directions both darker than the mid level: a line (road, trench, wall) through the blob
            static double LineOf(double[] rv, double[] dk)
            {
                for (int a = 0; a < BNA; a++) dk[a] = 1.0 - (rv[a] + rv[(a + BNA - 1) % BNA] + rv[(a + 1) % BNA]) / 3.0;
                double best = double.NegativeInfinity;
                for (int a = 0; a < BNA / 2; a++) best = Math.Max(best, Math.Min(dk[a], dk[a + BNA / 2]));
                return Clamp(best, 0, 1);
            }

            // combined confidence: both agree (ring confidence remapped so that ringThreshold -> 0.5), or one of the pathways
            public void ScoreBlobs()
            {
                double thr = s.ringThreshold;
                double floorBoth = s.clusterMinConfidence * 2 * thr;
                double floor = leaf > 0 ? floorBoth : Math.Min(floorBoth, s.blobStrongRingMin);
                conf = new double[n]; waterB = new bool[n];
                Parallel.For(0, n, po, () => new Scratch(), (oi, state, sc) =>
                {
                    int i = order[oi];
                    double cr = confR[i];
                    if (!(cr >= floor && cr > 0)) return sc;
                    double rp = cr >= thr ? 0.5 + 0.5 * (cr - thr) / (1.0 - thr) : 0.5 * cr / thr;
                    Blob best = default;
                    for (int k = 0; k < s.blobRadiusScales.Length; k++)
                    {
                        if (k > 0 && rbar[i] < s.blobMultiRadiusMin) break;
                        var b = BlobAt(sc, cx[i], cy[i], s.blobRadiusScales[k] * rbar[i]);
                        if (k == 0 || b.conf > best.conf) best = b;
                    }
                    waterB[i] = best.water;
                    double both = Math.Min(rp, best.conf);
                    if (leaf == 0 && best.conf >= s.blobStrong && cr >= s.blobStrongRingMin)
                        both = Math.Max(both, 0.5 + (best.conf - s.blobStrong));
                    if (leaf > 0 && ringMode[i] >= s.ringModeMin && best.tex25 <= s.ringModeClutter && greenFrac[i] <= s.ringModeGreen
                        && best.viv25 <= s.ringModeLeaves)
                        both = Math.Max(both, 0.5 + 0.5 * (ringMode[i] - s.ringModeMin) / (1.0 - s.ringModeMin));
                    conf[i] = both;
                    return sc;
                }, sc => { });
            }

            // ------------------------------------------------------------------------------------------- selection
            // greedy NMS in descending score (ties in index order): a point closer than factor * max(r) + 1 px to a kept one is
            // dropped; kept points are looked up in 16 px cells within the point's own reach, as the reference does
            static List<int> Nms(int[] idx, double[] x, double[] y, double[] rad, double[] score, double factor)
            {
                var order = new int[idx.Length];
                for (int k = 0; k < order.Length; k++) order[k] = k;
                Array.Sort(order, (a, b) => { int c = score[idx[b]].CompareTo(score[idx[a]]); return c != 0 ? c : a.CompareTo(b); });
                var keep = new List<int>();
                var grid = new Dictionary<long, List<int>>();
                const double cell = 16.0;
                foreach (int o in order)
                {
                    int i = idx[o];
                    int gx = (int)Math.Floor(x[i] / cell), gy = (int)Math.Floor(y[i] / cell);
                    int span = (int)Math.Ceiling((rad[i] * 2 + 2) / cell) + 1;
                    bool ok = true;
                    for (int ix = gx - span; ix <= gx + span && ok; ix++)
                        for (int iy = gy - span; iy <= gy + span && ok; iy++)
                        {
                            if (!grid.TryGetValue(Key(ix, iy), out var list)) continue;
                            foreach (int j in list)
                                if (Hypot(x[i] - x[j], y[i] - y[j]) < factor * Math.Max(rad[i], rad[j]) + 1.0) { ok = false; break; }
                        }
                    if (!ok) continue;
                    keep.Add(i);
                    long key = Key(gx, gy);
                    if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>();
                    l.Add(i);
                }
                return keep;
            }

            public void Select(Rect area, List<Detection> result)
            {
                if (exclude != null)   // a centre recentred onto an excluded pixel is no crater either
                    for (int i = 0; i < n; i++)
                    {
                        int xi = Math.Min(Math.Max((int)Math.Round(cx[i]), 0), W - 1), yi = Math.Min(Math.Max((int)Math.Round(cy[i]), 0), H - 1);
                        if (exclude[Src(xi, yi)]) conf[i] = 0;
                    }
                var score = new double[n];
                for (int i = 0; i < n; i++) score[i] = conf[i] + 1e-4 * C[i];
                // a dark patch holding two or more separate pits gives way to them
                var conf2 = (double[])conf.Clone();
                var good = new List<int>();
                var cells = new Dictionary<long, List<int>>();
                for (int i = 0; i < n; i++)
                {
                    if (!(conf[i] >= s.clusterMinConfidence)) continue;
                    good.Add(i);
                    long key = Key((int)Math.Floor(cx[i] / 8.0), (int)Math.Floor(cy[i] / 8.0));
                    if (!cells.TryGetValue(key, out var l)) cells[key] = l = new List<int>();
                    l.Add(i);
                }
                var order = good.ToArray();
                Array.Sort(order, (a, b) => { int c = rbar[b].CompareTo(rbar[a]); return c != 0 ? c : a.CompareTo(b); });
                var inside = new List<int>();
                foreach (int i in order)
                {
                    double R = rbar[i];
                    if (R < s.clusterMinRadius) continue;
                    int span = (int)Math.Ceiling(R / 8.0) + 1;
                    int gx = (int)Math.Floor(cx[i] / 8.0), gy = (int)Math.Floor(cy[i] / 8.0);
                    inside.Clear();
                    for (int ix = gx - span; ix <= gx + span; ix++)
                        for (int iy = gy - span; iy <= gy + span; iy++)
                        {
                            if (!cells.TryGetValue(Key(ix, iy), out var list)) continue;
                            foreach (int j in list)
                                if (j != i && rbar[j] < s.clusterInner * R && Hypot(cx[j] - cx[i], cy[j] - cy[i]) < 0.9 * R) inside.Add(j);
                        }
                    if (inside.Count < 2) continue;
                    inside.Sort();
                    if (Nms(inside.ToArray(), cx, cy, rbar, score, 1.0).Count >= 2) conf2[i] = 0;
                }
                // duplicates, then the threshold; strongest first
                var idx = new List<int>();
                var score2 = new double[n];
                for (int i = 0; i < n; i++) { score2[i] = conf2[i] + 1e-4 * C[i]; if (conf2[i] >= 0.15) idx.Add(i); }
                var kept = Nms(idx.ToArray(), cx, cy, rbar, score2, s.nmsFactor);
                kept.RemoveAll(i => !(conf2[i] >= s.confidenceMin));
                var rank = new Dictionary<int, int>();
                for (int k = 0; k < kept.Count; k++) rank[kept[k]] = k;
                kept.Sort((a, b) => { int c = score2[b].CompareTo(score2[a]); return c != 0 ? c : rank[a].CompareTo(rank[b]); });
                double pxx = area.width / (double)W, pxz = area.height / (double)H;
                foreach (int i in kept)
                {
                    double rb = rbar[i];
                    bool fresh = ringMode[i] >= darkMode[i]
                                 || (ringc[i] >= s.freshRingContrast && ringFrac[i] >= s.freshRingShare && exgS[i] <= s.freshMaxGreen);
                    double rpatch = 0;
                    if (fresh && !double.IsNaN(rring[i]) && rring[i] > rb) rpatch = rring[i];
                    else if (!double.IsNaN(r85[i])) rpatch = r85[i];
                    result.Add(new Detection
                    {
                        x = (float)(area.xMin + (cx[i] + 0.5) * pxx),
                        z = (float)(area.yMax - (cy[i] + 0.5) * pxz),
                        pitDiameter = (float)(2 * rb * pxx),
                        patchDiameter = rpatch > 0 ? (float)Math.Max(2 * rpatch * pxx, 2 * rb * pxx) : 0f,
                        confidence = (float)conf2[i],
                        fresh = fresh,
                        water = waterB[i]
                    });
                }
            }
        }
    }
}
