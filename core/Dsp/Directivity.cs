// 音箱指向性（频率相关）：分频带 + 加权偶极子图案，公式与 Steam Audio 一致：
//   g(θ) = |(1−w) + w·cosθ|^p          （directivity.cpp: evaluate()）
//   w=dipoleWeight: 0=全向 0.5=心形 1=8字；p=dipolePower: 锐度
//
// 思路：宽带单值图案无法表达"不同频率指向性不同"（真实喇叭低频绕射强、高频聚拢）——
// 把源信号 3 分频（2阶 Butterworth RBJ 双二阶），每带独立 (w, p) 求增益再合成。
// 挂载位置：前置增益之后、空间模拟之前（属于声源属性，不属于模拟器）。
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

    internal sealed class DirectivityProcessor
    {
        public volatile bool Enabled;
        public volatile float FreqLowMid = 300f;
        public volatile float FreqMidHigh = 2000f;
        // 每带 dipoleWeight（0全向/0.5心形/1八字）与 dipolePower（锐度）
        public volatile float WLow = 0f, WMid = 0f, WHigh = 0f;
        public volatile float PLow = 1f, PMid = 1f, PHigh = 1f;
        public volatile int Aim = (int)AimMode.TowardListener;
        public volatile float AimAz = 0f, AimEl = 0f; // 手动朝向（头坐标系角度）

        private readonly Biquad _lp1 = new Biquad();
        private readonly Biquad _hp1 = new Biquad();
        private readonly Biquad _lp2 = new Biquad();
        private readonly Biquad _hp2 = new Biquad();
        private float _cF1 = -1f, _cF2 = -1f; // 已应用的分频点
        private float _lastSampleRate;

        /// <summary>处理一块单声道（就地）。srcAz/srcEl = 声源方向（头坐标系角度：正前0°右正）。</summary>
        public void Process(float[] mono, int frames, float sampleRate, float srcAzDeg, float srcElDeg)
        {
            if (!Enabled) return;
            RefreshCoeffs(sampleRate);

            DirFromAngles(srcAzDeg, srcElDeg, out float dx, out float dy, out float dz);
            GetAim(dx, dy, dz, out float ax, out float ay, out float az);
            float cosT = ax * -dx + ay * -dy + az * -dz;
            if (cosT > 1f) cosT = 1f;
            if (cosT < -1f) cosT = -1f;

            float gL = Pattern(WLow, PLow, cosT);
            float gM = Pattern(WMid, PMid, cosT);
            float gH = Pattern(WHigh, PHigh, cosT);

            for (int i = 0; i < frames; i++)
            {
                float x = mono[i];
                float low = _lp1.Tick(x);
                float hp1 = _hp1.Tick(x);
                float mid = _lp2.Tick(hp1);
                float high = _hp2.Tick(hp1);
                mono[i] = low * gL + mid * gM + high * gH;
            }
        }

        /// <summary>某方向的分带增益（供 UI 实时显示）。</summary>
        public void GainsAt(float srcAzDeg, float srcElDeg, out float gLow, out float gMid, out float gHigh)
        {
            DirFromAngles(srcAzDeg, srcElDeg, out float dx, out float dy, out float dz);
            GetAim(dx, dy, dz, out float ax, out float ay, out float az);
            float cosT = ax * -dx + ay * -dy + az * -dz;
            gLow = Pattern(WLow, PLow, cosT);
            gMid = Pattern(WMid, PMid, cosT);
            gHigh = Pattern(WHigh, PHigh, cosT);
        }

        /// <summary>Steam Audio 加权偶极子：| (1−w) + w·cosθ | ^ p，w=0 时恒为 1。</summary>
        private static float Pattern(float w, float p, float cosTheta)
        {
            float base_ = (1f - w) + w * cosTheta;
            float v = base_ < 0f ? -base_ : base_;
            if (w <= 0f) return 1f;
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

        private void RefreshCoeffs(float sampleRate)
        {
            float f1 = Clamp(FreqLowMid, MinFreq, sampleRate * 0.45f);
            float f2 = Clamp(FreqMidHigh, f1 * 1.5f, sampleRate * 0.45f);
            if (sampleRate != _lastSampleRate || Math.Abs(f1 - _cF1) > 0.01f || Math.Abs(f2 - _cF2) > 0.01f)
            {
                _lp1.SetLowpass(sampleRate, f1);
                _hp1.SetHighpass(sampleRate, f1);
                _lp2.SetLowpass(sampleRate, f2);
                _hp2.SetHighpass(sampleRate, f2);
                _cF1 = f1; _cF2 = f2; _lastSampleRate = sampleRate;
            }
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        public const float MinFreq = 60f;
        public const float MaxFreq = 8000f;

        /// <summary>2阶 Butterworth 双二阶（RBJ cookbook），转置直接 II 型。</summary>
        private sealed class Biquad
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
}
