// 音箱指向性（频率相关）：4 分频带 + 加权偶极子图案，公式与 Steam Audio 一致：
//   g(θ) = |(1−w) + w·cosθ|^p          （directivity.cpp: evaluate()）
//   w=dipoleWeight: 0=全向 0.5=心形 1=8字；p=dipolePower: 锐度
//
// 4 带（低/中低/中高/高）× 3 个分频点：真实喇叭低频绕射强、高频聚拢，
// 宽带单值图案表达不了——每带独立 (w, p) 求增益再合成。
//
// 结构与教训：
// - 滤波状态必须**每声源一份**（曾共用一套，左右声道互污染滤波器记忆，
//   产生持续调制失真——"声音不正常"的真凶，不是缓冲区）
// - 分频用 2 阶 Butterworth 互补对（LP+HP 幅度和恒平），级联出 4 带；
//   每声源 6 个双二阶，成本微不足道
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

    /// <summary>每声源一份的滤波状态（4 带 = 3 级互补分割，6 个双二阶）。</summary>
    internal sealed class DirectivityState
    {
        internal readonly Biquad Lp1 = new Biquad();
        internal readonly Biquad Hp1 = new Biquad();
        internal readonly Biquad Lp2 = new Biquad();
        internal readonly Biquad Hp2 = new Biquad();
        internal readonly Biquad Lp3 = new Biquad();
        internal readonly Biquad Hp3 = new Biquad();
        internal float AppliedF1 = -1f, AppliedF2 = -1f, AppliedF3 = -1f, AppliedSr;
    }

    internal sealed class DirectivityProcessor
    {
        public volatile bool Enabled;
        public volatile float Freq1 = 200f;   // 低 | 中低
        public volatile float Freq2 = 800f;   // 中低 | 中高
        public volatile float Freq3 = 3000f;  // 中高 | 高
        // 每带 dipoleWeight（0全向/0.5心形/1八字）与 dipolePower（锐度）
        public volatile float WLow = 0f, WMidLow = 0f, WMidHigh = 0f, WHigh = 0f;
        public volatile float PLow = 1f, PMidLow = 1f, PMidHigh = 1f, PHigh = 1f;
        public volatile int Aim = (int)AimMode.TowardListener;
        public volatile float AimAz = 0f, AimEl = 0f; // 手动朝向（头坐标系角度）

        public const float MinFreq = 60f;
        public const float MaxFreq = 8000f;

        public DirectivityState CreateState() => new DirectivityState();

        /// <summary>处理一块单声道（就地）。state = 该声源独享的滤波状态。</summary>
        public void Process(DirectivityState state, float[] mono, int frames,
            float sampleRate, float srcAzDeg, float srcElDeg)
        {
            if (!Enabled) return;
            RefreshCoeffs(state, sampleRate);

            GainsAt(srcAzDeg, srcElDeg, out float g0, out float g1, out float g2, out float g3);

            for (int i = 0; i < frames; i++)
            {
                float x = mono[i];
                float hp1 = state.Hp1.Tick(x);
                float hp2 = state.Hp2.Tick(hp1);
                float low = state.Lp1.Tick(x);
                float midLow = state.Lp2.Tick(hp1);
                float midHigh = state.Lp3.Tick(hp2);
                float high = state.Hp3.Tick(hp2);
                mono[i] = low * g0 + midLow * g1 + midHigh * g2 + high * g3;
            }
        }

        /// <summary>某方向的分带增益（供 UI 实时显示）。</summary>
        public void GainsAt(float srcAzDeg, float srcElDeg,
            out float gLow, out float gMidLow, out float gMidHigh, out float gHigh)
        {
            DirFromAngles(srcAzDeg, srcElDeg, out float dx, out float dy, out float dz);
            GetAim(dx, dy, dz, out float ax, out float ay, out float az);
            float cosT = ax * -dx + ay * -dy + az * -dz;
            if (cosT > 1f) cosT = 1f;
            if (cosT < -1f) cosT = -1f;

            gLow = Pattern(WLow, PLow, cosT);
            gMidLow = Pattern(WMidLow, PMidLow, cosT);
            gMidHigh = Pattern(WMidHigh, PMidHigh, cosT);
            gHigh = Pattern(WHigh, PHigh, cosT);
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
            float f1 = Clamp(Freq1, MinFreq, sampleRate * 0.4f);
            float f2 = Clamp(Freq2, f1 * 1.5f, sampleRate * 0.4f);
            float f3 = Clamp(Freq3, f2 * 1.5f, sampleRate * 0.45f);
            if (sampleRate != st.AppliedSr ||
                Math.Abs(f1 - st.AppliedF1) > 0.01f ||
                Math.Abs(f2 - st.AppliedF2) > 0.01f ||
                Math.Abs(f3 - st.AppliedF3) > 0.01f)
            {
                st.Lp1.SetLowpass(sampleRate, f1);
                st.Hp1.SetHighpass(sampleRate, f1);
                st.Lp2.SetLowpass(sampleRate, f2);
                st.Hp2.SetHighpass(sampleRate, f2);
                st.Lp3.SetLowpass(sampleRate, f3);
                st.Hp3.SetHighpass(sampleRate, f3);
                st.AppliedF1 = f1; st.AppliedF2 = f2; st.AppliedF3 = f3; st.AppliedSr = sampleRate;
            }
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
