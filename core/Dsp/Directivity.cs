// 音箱指向性（频率相关）：N 分频带 + 加权偶极子图案，公式与 Steam Audio 一致：
//   g(θ) = |(1−w) + w·cosθ|^p          （directivity.cpp: evaluate()）
//   w=dipoleWeight: 0=全向 0.5=心形 1=8字；p=dipolePower: 锐度
//
// 6 带 × 5 分频点（默认 125/350/1k/3k/10k，对数分布，最高可到 16k）：
// 真实喇叭低频绕射强、高频聚拢，宽带单值图案表达不了——每带独立 (w, p)
// 求增益再合成；带间过渡由 Butterworth 互补对的滤波斜率自然平滑。
//
// 结构与教训：
// - 滤波状态必须**每声源一份**（曾共用一套，左右声道互污染滤波器记忆，
//   产生持续调制失真——"声音不正常"的真凶，不是缓冲区）
// - 分频 = 级联 LR4 分割（每级 LP/HP 各两个 BW2 级联）；曾用单对 BW LP+HP——
//   功率互补但幅度不互补，分频点复数和为零，重组出深谷（后换 LR4 根治）
// - 挂载：前置增益之后、空间模拟之前（声源属性，各空间模式共用）
using System;

namespace VirtualStereo.Dsp
{
    /// <summary>朝向模式：声源朝哪，决定各方向的 θ。</summary>
    internal enum AimMode
    {
        TowardListener = 0, // 朝向听者（真实摆位；θ=0，响应平直）
        HeadForward = 1,    // 固定朝头前方 +Z（声源偏离正前方即可听出离轴变色）
        Manual = 2,         // 手动角度
    }

    /// <summary>每声源一份的滤波状态（5 级 LR4 分割，20 个双二阶）。</summary>
    internal sealed class DirectivityState
    {
        internal readonly LrCrossover[] X;
        internal readonly float[] AppliedFreq;
        internal float AppliedSr = -1f;

        public DirectivityState()
        {
            X = new LrCrossover[DirectivityProcessor.Splits];
            AppliedFreq = new float[DirectivityProcessor.Splits];
            for (int k = 0; k < DirectivityProcessor.Splits; k++)
            {
                X[k] = new LrCrossover();
                AppliedFreq[k] = -1f;
            }
        }
    }

    internal sealed class DirectivityProcessor
    {
        public const int Bands = 6;    // 低 / 中低 / 中中 / 中高 / 次高 / 高
        public const int Splits = 5;   // 分频点数
        public const float MinFreq = 60f;
        public const float MaxFreq = 16000f;

        public volatile bool Enabled;

        // 分频点（对数分布默认）与每带图案参数（元素级并发读写为良性竞争）
        public readonly float[] Freqs = { 125f, 350f, 1000f, 3000f, 10000f };
        public readonly float[] W = { 0f, 0f, 0f, 0f, 0f, 0f };  // 权重
        public readonly float[] P = { 1f, 1f, 1f, 1f, 1f, 1f };  // 锐度

        private readonly float[] _g = new float[Bands]; // 音频线程 scratch（单线程使用）

        public DirectivityState CreateState() => new DirectivityState();

        /// <summary>处理一块单声道（就地）。state = 该声源独享的滤波状态。
        /// 朝向每源独立：aimMode/aimAz/aimEl 决定该音箱的指向。</summary>
        public void Process(DirectivityState state, float[] mono, int frames,
            float sampleRate, float srcAzDeg, float srcElDeg,
            int aimMode, float aimAz, float aimEl)
        {
            if (!Enabled) return;
            RefreshCoeffs(state, sampleRate);
            GainsInto(srcAzDeg, srcElDeg, aimMode, aimAz, aimEl, _g);

            for (int i = 0; i < frames; i++)
            {
                float s = mono[i];
                float sum = 0f;
                for (int k = 0; k < Splits; k++)
                {
                    float lp = state.X[k].TickLow(s);
                    float hp = state.X[k].TickHigh(s);
                    sum += lp * _g[k];
                    s = hp;
                }
                sum += s * _g[Splits];
                mono[i] = sum;
            }
        }

        /// <summary>某方向的分带增益（供 UI 实时显示；dst 长度 = Bands）。</summary>
        public void GainsAt(float srcAzDeg, float srcElDeg,
            int aimMode, float aimAz, float aimEl, float[] dst)
        {
            GainsInto(srcAzDeg, srcElDeg, aimMode, aimAz, aimEl, dst);
        }

        /// <summary>按端点渐变填充各带 (w, p)：i/(Bands-1) 线性插值（"更均匀"的省事做法）。</summary>
        public void FillGradient(float wLow, float pLow, float wHigh, float pHigh)
        {
            for (int i = 0; i < Bands; i++)
            {
                float t = (float)i / (Bands - 1);
                W[i] = wLow + (wHigh - wLow) * t;
                P[i] = pLow + (pHigh - pLow) * t;
            }
        }

        private void GainsInto(float srcAzDeg, float srcElDeg,
            int aimMode, float aimAz, float aimEl, float[] dst)
        {
            DirFromAngles(srcAzDeg, srcElDeg, out float dx, out float dy, out float dz);
            GetAim(aimMode, aimAz, aimEl, dx, dy, dz, out float ax, out float ay, out float az);
            float cosT = ax * -dx + ay * -dy + az * -dz;
            if (cosT > 1f) cosT = 1f;
            if (cosT < -1f) cosT = -1f;
            for (int i = 0; i < Bands; i++)
                dst[i] = Pattern(W[i], P[i], cosT);
        }

        /// <summary>Steam Audio 加权偶极子：| (1−w) + w·cosθ | ^ p，w=0 时恒为 1。</summary>
        private static float Pattern(float w, float p, float cosTheta)
        {
            if (w <= 0f) return 1f;
            float base_ = (1f - w) + w * cosTheta;
            float v = base_ < 0f ? -base_ : base_;
            return (float)Math.Pow(v, p);
        }

        private static void GetAim(int aimMode, float aimAz, float aimEl,
            float dx, float dy, float dz, out float ax, out float ay, out float az)
        {
            switch ((AimMode)aimMode)
            {
                case AimMode.TowardListener: // 朝向听者 = 反向源方向，θ 恒 0
                    ax = -dx; ay = -dy; az = -dz;
                    break;
                case AimMode.HeadForward: // 固定朝头前方 +Z
                    ax = 0f; ay = 0f; az = 1f;
                    break;
                default: // 手动
                    DirFromAngles(aimAz, aimEl, out ax, out ay, out az);
                    break;
            }
        }

        private static void DirFromAngles(float azDeg, float elDeg, out float x, out float y, out float z)
        {
            double a = azDeg * Math.PI / 180.0;
            double e = elDeg * Math.PI / 180.0;
            double ce = Math.Cos(e);
            x = (float)(Math.Sin(a) * ce);
            y = (float)Math.Sin(e);
            z = (float)(Math.Cos(a) * ce);
        }

        private void RefreshCoeffs(DirectivityState st, float sampleRate)
        {
            bool dirty = sampleRate != st.AppliedSr;
            float prev = MinFreq;
            Span<float> f = stackalloc float[Splits];
            for (int k = 0; k < Splits; k++)
            {
                float lo = prev * 1.5f;
                float hi = sampleRate * (k == Splits - 1 ? 0.45f : 0.4f);
                f[k] = Clamp(Freqs[k], lo, hi);
                if (Math.Abs(f[k] - st.AppliedFreq[k]) > 0.01f) dirty = true;
                prev = f[k];
            }
            if (!dirty) return;

            for (int k = 0; k < Splits; k++)
            {
                st.X[k].Set(sampleRate, f[k]);
                st.AppliedFreq[k] = f[k];
            }
            st.AppliedSr = sampleRate;
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    /// <summary>Linkwitz-Riley 4 阶分频器：两个同截止 2阶 BW 级联。
    /// 为什么不用单纯 LP+HP：BW 功率互补但**不幅度互补**——复数和在分频点精确归零
    /// （带间相位相反 180°，重组相消出深谷）。LR4 在分频点两路同相（都 −0.5），
    /// 和为全通、幅度恒平，带增益不同时平滑过渡——这是重组的正确姿势。
    /// 结构：low = LP2a→LP2b 级联；high = HP2a→HP2b 级联（各两阶）。</summary>
    internal sealed class LrCrossover
    {
        private readonly Biquad _lpA = new Biquad();
        private readonly Biquad _lpB = new Biquad();
        private readonly Biquad _hpA = new Biquad();
        private readonly Biquad _hpB = new Biquad();

        public void Set(float sr, float freq)
        {
            _lpA.SetLowpass(sr, freq);
            _lpB.SetLowpass(sr, freq);
            _hpA.SetHighpass(sr, freq);
            _hpB.SetHighpass(sr, freq);
        }

        public float TickLow(float s) => _lpB.Tick(_lpA.Tick(s));
        public float TickHigh(float s) => _hpB.Tick(_hpA.Tick(s));
    }

    /// <summary>2阶 Butterworth 双二阶（RBJ cookbook），转置直接 II 型。</summary>
    internal sealed class Biquad
    {
        private float _b0, _b1, _b2, _a1, _a2, _z1, _z2;

        public void SetLowpass(float sr, float freq, float q = 0.707106781f)
        {
            double w0 = 2 * Math.PI * freq / sr;
            double cosw = Math.Cos(w0), sinw = Math.Sin(w0);
            double alpha = sinw / (2 * q);
            double b0 = (1 - cosw) / 2, b1 = 1 - cosw, b2 = (1 - cosw) / 2;
            double a0 = 1 + alpha, a1 = -2 * cosw, a2 = 1 - alpha;
            Set(b0, b1, b2, a0, a1, a2);
        }

        public void SetHighpass(float sr, float freq, float q = 0.707106781f)
        {
            double w0 = 2 * Math.PI * freq / sr;
            double cosw = Math.Cos(w0), sinw = Math.Sin(w0);
            double alpha = sinw / (2 * q);
            double b0 = (1 + cosw) / 2, b1 = -(1 + cosw), b2 = (1 + cosw) / 2;
            double a0 = 1 + alpha, a1 = -2 * cosw, a2 = 1 - alpha;
            Set(b0, b1, b2, a0, a1, a2);
        }

        /// <summary>峰化 EQ（RBJ peaking，中心 freq / 带宽 Q / 增益 dB）——校准拉平用。</summary>
        public void SetPeaking(float sr, float freq, float q, float gainDb)
        {
            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2 * Math.PI * freq / sr;
            double cosw = Math.Cos(w0), sinw = Math.Sin(w0);
            double alpha = sinw / (2 * q);
            double b0 = 1 + alpha * a, b1 = -2 * cosw, b2 = 1 - alpha * a;
            double a0 = 1 + alpha / a, a1 = -2 * cosw, a2 = 1 - alpha / a;
            Set(b0, b1, b2, a0, a1, a2);
        }

        /// <summary>低架 EQ（RBJ low shelf，freq 转折 / 斜率 q / 增益 dB）——后处理补低音用。</summary>
        public void SetLowShelf(float sr, float freq, float q, float gainDb)
        {
            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2 * Math.PI * freq / sr;
            double cosw = Math.Cos(w0), sinw = Math.Sin(w0);
            double alpha = sinw / (2 * q);
            double sq = 2 * Math.Sqrt(a) * alpha;
            double b0 = a * ((a + 1) - (a - 1) * cosw + sq);
            double b1 = 2 * a * ((a - 1) - (a + 1) * cosw);
            double b2 = a * ((a + 1) - (a - 1) * cosw - sq);
            double a0 = (a + 1) + (a - 1) * cosw + sq;
            double a1 = -2 * ((a - 1) + (a + 1) * cosw);
            double a2 = (a + 1) + (a - 1) * cosw - sq;
            Set(b0, b1, b2, a0, a1, a2);
        }

        /// <summary>高架 EQ（RBJ high shelf）。</summary>
        public void SetHighShelf(float sr, float freq, float q, float gainDb)
        {
            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2 * Math.PI * freq / sr;
            double cosw = Math.Cos(w0), sinw = Math.Sin(w0);
            double alpha = sinw / (2 * q);
            double sq = 2 * Math.Sqrt(a) * alpha;
            double b0 = a * ((a + 1) + (a - 1) * cosw + sq);
            double b1 = -2 * a * ((a - 1) + (a + 1) * cosw);
            double b2 = a * ((a + 1) + (a - 1) * cosw - sq);
            double a0 = (a + 1) - (a - 1) * cosw + sq;
            double a1 = 2 * ((a - 1) - (a + 1) * cosw);
            double a2 = (a + 1) - (a - 1) * cosw - sq;
            Set(b0, b1, b2, a0, a1, a2);
        }

        /// <summary>清零状态（切换校准开关时用，防残留瞬态）。</summary>
        public void Reset() { _z1 = 0f; _z2 = 0f; }

        /// <summary>幅度响应（dB）——校准面板画"EQ 合成曲线"用。
        /// 零极点分解式：多项式形式在高 Q 极点附近浮点相消，会算出 ±100dB 假尖峰。</summary>
        public float MagDb(float freq, float sr)
        {
            double w = 2 * Math.PI * freq / sr;
            double cw = Math.Cos(w), sw = Math.Sin(w);

            // H(z) = b0·(z−z1)(z−z2) / (z−p1)(z−p2)；|e^jw − c|² = (cw−cr)² + (sw−ci)²
            RootPair(1.0, _a1, _a2, out double p1r, out double p1i, out double p2r, out double p2i);
            double den = Dist2(cw, sw, p1r, p1i) * Dist2(cw, sw, p2r, p2i);
            if (den < 1e-300) den = 1e-300;

            double b0 = _b0;
            double num = b0 * b0;
            if (Math.Abs(b0) > 1e-30)
            {
                RootPair(b0, _b1, _b2, out double z1r, out double z1i, out double z2r, out double z2i);
                num *= Dist2(cw, sw, z1r, z1i) * Dist2(cw, sw, z2r, z2i);
            }
            return (float)(10.0 * Math.Log10(Math.Max(1e-300, num / den)));
        }

        private static double Dist2(double cw, double sw, double cr, double ci)
            => (cw - cr) * (cw - cr) + (sw - ci) * (sw - ci);

        /// <summary>a·x² + b·x + c = 0 的两根（可为复根）。</summary>
        private static void RootPair(double a, double b, double c,
            out double r1, out double i1, out double r2, out double i2)
        {
            double disc = b * b - 4 * a * c;
            double inv = 0.5 / a;
            double br = -b * inv;
            if (disc >= 0)
            {
                double sq = Math.Sqrt(disc) * inv;
                r1 = br + sq; i1 = 0;
                r2 = br - sq; i2 = 0;
            }
            else
            {
                double sq = Math.Sqrt(-disc) * Math.Abs(inv);
                r1 = br; i1 = sq;
                r2 = br; i2 = -sq;
            }
        }

        private void Set(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = (float)(b0 / a0);
            _b1 = (float)(b1 / a0);
            _b2 = (float)(b2 / a0);
            _a1 = (float)(a1 / a0);
            _a2 = (float)(a2 / a0);
            // 改分频点时保留滤波状态（避免咔哒）；状态会快速自然衰减
        }

        public float Tick(float x)
        {
            float y = _b0 * x + _z1;
            _z1 = _b1 * x + _z2 - _a1 * y;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }
}
