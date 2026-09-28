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
// - 分频 = 级联互补分割（每级 LP+HP，幅度和恒平）；每声源 5 级 10 个双二阶，
//   成本微不足道；将来要更多带时升级分割树/SVF 结构即可
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

    /// <summary>每声源一份的滤波状态（5 级互补分割，10 个双二阶）。</summary>
    internal sealed class DirectivityState
    {
        internal readonly Biquad[] Lp;
        internal readonly Biquad[] Hp;
        internal readonly float[] AppliedFreq;
        internal float AppliedSr = -1f;

        public DirectivityState()
        {
            Lp = new Biquad[DirectivityProcessor.Splits];
            Hp = new Biquad[DirectivityProcessor.Splits];
            AppliedFreq = new float[DirectivityProcessor.Splits];
            for (int k = 0; k < DirectivityProcessor.Splits; k++)
            {
                Lp[k] = new Biquad();
                Hp[k] = new Biquad();
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
        public volatile int Aim = (int)AimMode.TowardListener;
        public volatile float AimAz = 0f, AimEl = 0f; // 手动朝向（头坐标系角度）

        // 分频点（对数分布默认）与每带图案参数（元素级并发读写为良性竞争）
        public readonly float[] Freqs = { 125f, 350f, 1000f, 3000f, 10000f };
        public readonly float[] W = { 0f, 0f, 0f, 0f, 0f, 0f };  // 权重
        public readonly float[] P = { 1f, 1f, 1f, 1f, 1f, 1f };  // 锐度

        private readonly float[] _g = new float[Bands]; // 音频线程 scratch（单线程使用）

        public DirectivityState CreateState() => new DirectivityState();

        /// <summary>处理一块单声道（就地）。state = 该声源独享的滤波状态。</summary>
        public void Process(DirectivityState state, float[] mono, int frames,
            float sampleRate, float srcAzDeg, float srcElDeg)
        {
            if (!Enabled) return;
            RefreshCoeffs(state, sampleRate);
            GainsInto(srcAzDeg, srcElDeg, _g);

            for (int i = 0; i < frames; i++)
            {
                float s = mono[i];
                float sum = 0f;
                for (int k = 0; k < Splits; k++)
                {
                    float hp = state.Hp[k].Tick(s);
                    float lp = state.Lp[k].Tick(s);
                    sum += lp * _g[k];
                    s = hp;
                }
                sum += s * _g[Splits];
                mono[i] = sum;
            }
        }

        /// <summary>某方向的分带增益（供 UI 实时显示；dst 长度 = Bands）。</summary>
        public void GainsAt(float srcAzDeg, float srcElDeg, float[] dst)
        {
            GainsInto(srcAzDeg, srcElDeg, dst);
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

        private void GainsInto(float srcAzDeg, float srcElDeg, float[] dst)
        {
            DirFromAngles(srcAzDeg, srcElDeg, out float dx, out float dy, out float dz);
            GetAim(dx, dy, dz, out float ax, out float ay, out float az);
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

        private void GetAim(float dx, float dy, float dz, out float ax, out float ay, out float az)
        {
            switch ((AimMode)Aim)
            {
                case AimMode.TowardListener: // 朝向听者 = 反向源方向，θ 恒 0
                    ax = -dx; ay = -dy; az = -dz;
                    break;
                case AimMode.HeadForward: // 固定朝头前方 +Z
                    ax = 0f; ay = 0f; az = 1f;
                    break;
                default: // 手动
                    DirFromAngles(AimAz, AimEl, out ax, out ay, out az);
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
                st.Lp[k].SetLowpass(sampleRate, f[k]);
                st.Hp[k].SetHighpass(sampleRate, f[k]);
                st.AppliedFreq[k] = f[k];
            }
            st.AppliedSr = sampleRate;
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
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
