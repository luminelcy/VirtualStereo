// 听音室：房间几何 → 一阶镜像反射（image source）→ 双耳，尾巴由 FDN 混响补密度。
//
// 几何：轴对齐盒 [0,W]×[0,H]×[0,D]（y=0 为地面），听者 (x,y,z) + 朝向 yaw；
//       音箱仍由"空间模拟"的 az/el/dist 绕听者摆位，换算到房间坐标（两套坐标不打架）。
// 反射：每音箱对 6 面墙各取一阶镜像源：
//       延迟 = |听者−镜像|/c；增益 = (直达距离/路径长)·Γ，Γ=√(1−α)，α=墙面吸声；
//       入射方向换到头坐标 → 每路 Woodworth ITD + 等功率 ILD（每耳独立分数延迟）。
// 混响：Sabine 公式 α/体积/表面积 → RT60 → 8 线 Hadamard FDN（见 RoomReverb.cs）。
// v1 有意简化：反射不带指向性着色、只一阶镜像、无空气吸收（更高阶密度由尾巴承担）。
//
// 挂载：指向性+距离之后的两路音箱信号进本模块，产出加到两耳信号上
//       （即"耳朵"听到 直达 + 一次反射 + 混响）。
// 线程模型：UI 写参数 / 音频线程每块重算路径（路径计算 ~百次浮点，忽略不计；
//           增益一阶平滑、延迟 FracDelay 内部平滑，推参数不炸音）。
using System;

namespace VirtualStereo.Dsp
{
    /// <summary>一条反射路径的声学参数（延迟/增益/头坐标入射方向）。</summary>
    internal struct PathInfo
    {
        public float DelaySec;
        public float Gain;
        public float DirX, DirY, DirZ; // 头坐标：X右 Y上 Z前
    }

    /// <summary>房间几何与镜像源路径计算（纯数学，无状态）。</summary>
    internal sealed class RoomModel
    {
        public const float SpeedOfSound = 343f;
        public const int Walls = 6;
        public const int Sources = 2;
        public const int MaxPaths = Sources * Walls;

        // 房间尺寸（米）
        public volatile float W = 6f, D = 7f, H = 2.8f;
        // 听者位置（米；y=离地耳朵高度）与朝向（yaw 度，0=朝 +Z 前墙）
        public volatile float ListenerX = 3f, ListenerY = 1.2f, ListenerZ = 3.5f;
        public volatile float YawDeg = 0f;
        // 墙面吸声系数 α（0.02 瓷砖 .. 0.7 软包）
        public volatile float Absorb = 0.15f;

        /// <summary>RT60（秒，Sabine）：0.161V / (S·ᾱ)。</summary>
        public float Rt60()
        {
            float w = Math.Max(1f, W), h = Math.Max(1f, H), d = Math.Max(1f, D);
            float v = w * h * d;
            float s = 2f * (w * d + w * h + d * h);
            float a = Math.Clamp(Absorb, 0.02f, 1f);
            return Math.Clamp(0.161f * v / (s * a), 0.05f, 8f);
        }

        /// <summary>音箱在房间坐标的位置（由 az/el/dist 绕听者摆位换算）。</summary>
        public void SpeakerPos(float azDeg, float elDeg, float dist,
            out float x, out float y, out float z)
        {
            float psi = YawDeg * (float)Math.PI / 180f;
            float cw = (float)Math.Cos(psi), sw = (float)Math.Sin(psi);
            ToRoom(azDeg, elDeg, dist, cw, sw, out float hx, out float hy, out float hz);
            x = ListenerX + hx;
            y = ListenerY + hy;
            z = ListenerZ + hz;
        }

        /// <summary>重算 12 条一阶反射路径（2 音箱 × 6 墙）。</summary>
        public void ComputePaths(float azL, float elL, float distL,
            float azR, float elR, float distR, PathInfo[] dst)
        {
            float psi = YawDeg * (float)Math.PI / 180f;
            float cw = (float)Math.Cos(psi), sw = (float)Math.Sin(psi);
            float lx = ListenerX, ly = ListenerY, lz = ListenerZ;
            float w = Math.Max(1f, W), h = Math.Max(1f, H), d = Math.Max(1f, D);
            float gamma = (float)Math.Sqrt(Math.Max(0.001, 1.0 - Math.Clamp(Absorb, 0f, 0.999f)));

            ToRoom(azL, elL, distL, cw, sw, out float ax, out float ay, out float az);
            ToRoom(azR, elR, distR, cw, sw, out float bx, out float by, out float bz);
            BuildFor(0, lx + ax, ly + ay, lz + az, distL, lx, ly, lz, w, h, d, gamma, cw, sw, dst);
            BuildFor(1, lx + bx, ly + by, lz + bz, distR, lx, ly, lz, w, h, d, gamma, cw, sw, dst);
        }

        private static void ToRoom(float azDeg, float elDeg, float dist,
            float cw, float sw, out float x, out float y, out float z)
        {
            double a = azDeg * Math.PI / 180.0, e = elDeg * Math.PI / 180.0;
            double ce = Math.Cos(e);
            float hx = (float)(Math.Sin(a) * ce * dist);
            float hy = (float)(Math.Sin(e) * dist);
            float hz = (float)(Math.Cos(a) * ce * dist);
            x = hx * cw + hz * sw;
            y = hy;
            z = -hx * sw + hz * cw;
        }

        private static void BuildFor(int src, float sx, float sy, float sz, float distDirect,
            float lx, float ly, float lz, float w, float h, float d,
            float gamma, float cw, float sw, PathInfo[] dst)
        {
            for (int wall = 0; wall < Walls; wall++)
            {
                float ix = sx, iy = sy, iz = sz;
                switch (wall)
                {
                    case 0: ix = -sx; break;
                    case 1: ix = 2 * w - sx; break;
                    case 2: iy = -sy; break;
                    case 3: iy = 2 * h - sy; break;
                    case 4: iz = -sz; break;
                    case 5: iz = 2 * d - sz; break;
                }
                float dx = lx - ix, dy = ly - iy, dz = lz - iz;
                float r = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (r < 0.05f) r = 0.05f;

                var p = new PathInfo
                {
                    DelaySec = r / SpeedOfSound,
                    // 直达按 2m 参考反比；反射按同一律折算成相对直达的倍率
                    Gain = gamma * Math.Min(4f, Math.Max(0.05f, distDirect) / r),
                };
                float ux = dx / r, uy = dy / r, uz = dz / r;
                p.DirX = ux * cw - uz * sw;
                p.DirY = uy;
                p.DirZ = ux * sw + uz * cw;
                dst[src * Walls + wall] = p;
            }
        }
    }

    /// <summary>房间渲染器：一次反射（ITD/ILD）+ FDN 混响尾，产出加到两耳。</summary>
    internal sealed class RoomRenderer
    {
        public volatile bool Enabled;
        public volatile float ReflDb = -6f;    // 早期反射电平（相对直达）
        public volatile float ReverbDb = -12f; // 混响电平
        public volatile float Damp = 0.4f;     // 混响明暗（0 亮 .. 1 暗）

        public readonly RoomModel Model = new RoomModel();

        private readonly PathInfo[] _paths = new PathInfo[RoomModel.MaxPaths];
        private readonly RoomDelay[] _delay;
        private readonly float[,] _gain = new float[RoomModel.MaxPaths, 2];
        private readonly float[,] _gainT = new float[RoomModel.MaxPaths, 2];
        private readonly float[,] _dlyT = new float[RoomModel.MaxPaths, 2];
        private readonly RoomReverb _reverb = new RoomReverb();
        private float[] _revL, _revR;

        public RoomRenderer()
        {
            _delay = new RoomDelay[RoomModel.MaxPaths * 2];
            for (int i = 0; i < _delay.Length; i++) _delay[i] = new RoomDelay();
        }

        /// <summary>音频线程：把房间贡献加到两耳交错立体声上（累加，不清零）。</summary>
        public void Process(float[] monoL, float[] monoR, float[] earsInterleaved,
            int frames, int rate,
            float azL, float elL, float distL, float azR, float elR, float distR)
        {
            Model.ComputePaths(azL, elL, distL, azR, elR, distR, _paths);
            RefreshTargets(rate);

            float reflG = (float)Math.Pow(10.0, ReflDb / 20.0);
            for (int i = 0; i < frames; i++)
            {
                float sl = monoL[i], sr = monoR[i];
                float accL = 0f, accR = 0f;
                for (int p = 0; p < RoomModel.MaxPaths; p++)
                {
                    float s = p < RoomModel.Walls ? sl : sr;
                    accL += _gain[p, 0] * _delay[p * 2].Process(s, _dlyT[p, 0]);
                    accR += _gain[p, 1] * _delay[p * 2 + 1].Process(s, _dlyT[p, 1]);
                }
                earsInterleaved[i * 2] += accL * reflG;
                earsInterleaved[i * 2 + 1] += accR * reflG;
            }

            float revG = (float)Math.Pow(10.0, ReverbDb / 20.0);
            if (revG > 1e-4f)
            {
                if (_revL == null || _revL.Length < frames)
                {
                    _revL = new float[frames];
                    _revR = new float[frames];
                }
                _reverb.Process(monoL, monoR, _revL, _revR, frames, rate, Model.Rt60(), Damp);
                for (int i = 0; i < frames; i++)
                {
                    earsInterleaved[i * 2] += _revL[i] * revG;
                    earsInterleaved[i * 2 + 1] += _revR[i] * revG;
                }
            }
        }

        /// <summary>路径 → 每耳 目标延迟/增益（ITD/ILD），增益一阶平滑。</summary>
        private void RefreshTargets(int rate)
        {
            for (int p = 0; p < RoomModel.MaxPaths; p++)
            {
                ref PathInfo info = ref _paths[p];
                float delaySamples = info.DelaySec * rate;

                // 水平方位 → 等功率 pan + Woodworth ITD（a=8.75cm 球头）
                float h = (float)Math.Sqrt(info.DirX * info.DirX + info.DirZ * info.DirZ);
                float pan = h > 1e-5f ? info.DirX / h : 0f;
                if (pan > 1f) pan = 1f;
                if (pan < -1f) pan = -1f;
                double t = (pan + 1.0) * Math.PI / 4.0;
                float gL = (float)Math.Cos(t), gR = (float)Math.Sin(t);

                double az = Math.Atan2(info.DirX, info.DirZ);
                double abs = Math.Abs(az);
                float itd = (float)((0.0875 / RoomModel.SpeedOfSound) * (abs + Math.Sin(abs)) * rate);
                if (az < 0) itd = -itd; // 源在左 → 右耳晚

                _dlyT[p, 0] = delaySamples + itd * 0.5f;
                _dlyT[p, 1] = delaySamples - itd * 0.5f;
                _gainT[p, 0] = info.Gain * gL;
                _gainT[p, 1] = info.Gain * gR;
                _gain[p, 0] += (_gainT[p, 0] - _gain[p, 0]) * 0.05f;
                _gain[p, 1] += (_gainT[p, 1] - _gain[p, 1]) * 0.05f;
            }
        }

        /// <summary>每路每耳分数延迟线（目标延迟内部平滑，推参数不炸音）。</summary>
        private sealed class RoomDelay
        {
            private const int Size = 16384; // 340ms@48k，房间路径足够
            private const int Mask = Size - 1;
            private readonly float[] _buf = new float[Size];
            private int _write;
            private float _delay;

            public float Process(float sample, float targetDelaySamples)
            {
                if (targetDelaySamples < 0f) targetDelaySamples = 0f;
                if (targetDelaySamples > Size - 4) targetDelaySamples = Size - 4;
                _delay += (targetDelaySamples - _delay) * 0.02f;

                _buf[_write] = sample;
                float readPos = _write - _delay;
                while (readPos < 0f) readPos += Size;
                int i0 = (int)readPos & Mask;
                int i1 = (i0 + 1) & Mask;
                float frac = readPos - (int)readPos;
                float v = _buf[i0] * (1f - frac) + _buf[i1] * frac;
                _write = (_write + 1) & Mask;
                return v;
            }
        }
    }
}
