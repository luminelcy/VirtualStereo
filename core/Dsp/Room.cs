// 听音室：房间几何 → 一阶镜像反射（image source）→ 双耳，尾巴由 FDN 混响补密度。
//
// 几何：轴对齐盒 [0,W]×[0,H]×[0,D]（y=0 为地面），听者 (x,y,z) + 朝向 yaw；
//       音箱仍由"空间模拟"的 az/el/dist 绕听者摆位，换算到房间坐标（两套坐标不打架）。
// 材质：分表面（地板/天花板/四壁）× 分频带（低<250Hz 中250-2k 高>2k）吸声系数 α——
//       地毯吸高频远多于低频、天花板和地板不一种材料，都由这个 3×3 表达。
// 反射：每音箱对 6 面墙各取一阶镜像源：
//       延迟 = |听者−镜像|/c；增益 = 距离律 × Γ(频带)，Γ=√(1−α[表面][频带])
//       —— 每条路径挂三频带滤波（按命中表面），入射方向做 Woodworth ITD + 等功率 ILD。
// 混响：Sabine 按表面面积加权出 三带 RT60 → FDN 按频带差异化衰减（RoomReverb.cs）。
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
        public const int Surfaces = 3; // 0=地板 1=天花板 2=四壁
        public const int Bands = 3;    // 0=低(<250Hz) 1=中(250-2k) 2=高(>2k)

        // 房间尺寸（米）
        public volatile float W = 6f, D = 7f, H = 2.8f;
        // 听者位置（米；y=离地耳朵高度）与朝向（yaw 度，0=朝 +Z 前墙）
        public volatile float ListenerX = 3f, ListenerY = 1.2f, ListenerZ = 3.5f;
        public volatile float YawDeg = 0f;

        // 吸声系数 [表面][频带]（元素级并发读写为良性竞争）
        public readonly float[,] Abs = new float[Surfaces, Bands];

        public RoomModel()
        {
            // 默认：地板=薄地毯、天花板=抹灰、四壁=木地板（真实听音室常见组合）
            SetSurface(0, 0.05f, 0.20f, 0.50f);
            SetSurface(1, 0.02f, 0.03f, 0.05f);
            SetSurface(2, 0.08f, 0.07f, 0.10f);
        }

        public void SetSurface(int surface, float low, float mid, float high)
        {
            Abs[surface, 0] = low;
            Abs[surface, 1] = mid;
            Abs[surface, 2] = high;
        }

        /// <summary>墙序号 → 表面（y=0 地板 / y=H 天花板 / 其余四壁）。</summary>
        public static int SurfaceOfWall(int wall) => wall == 2 ? 0 : (wall == 3 ? 1 : 2);

        /// <summary>三带 RT60（秒，Sabine 按表面面积加权）：0.161V / (S·ᾱ[带])。dst 长度=3。</summary>
        public void Rt60Bands(float[] dst)
        {
            float w = Math.Max(1f, W), h = Math.Max(1f, H), d = Math.Max(1f, D);
            float v = w * h * d;
            float sFloor = w * d, sCeil = w * d, sWall = 2f * (w + d) * h;
            float sTot = sFloor + sCeil + sWall;
            for (int b = 0; b < Bands; b++)
            {
                float aa = (sFloor * Clamp01(Abs[0, b])
                          + sCeil * Clamp01(Abs[1, b])
                          + sWall * Clamp01(Abs[2, b])) / sTot;
                if (aa < 0.02f) aa = 0.02f;
                dst[b] = Math.Clamp(0.161f * v / (sTot * aa), 0.05f, 8f);
            }
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

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

        /// <summary>重算 12 条一阶反射路径（2 音箱 × 6 墙）。增益只含距离律——
        /// 频率相关的 Γ（材料吸收）由渲染器按命中表面的频带滤波施加。</summary>
        public void ComputePaths(float azL, float elL, float distL,
            float azR, float elR, float distR, PathInfo[] dst)
        {
            float psi = YawDeg * (float)Math.PI / 180f;
            float cw = (float)Math.Cos(psi), sw = (float)Math.Sin(psi);
            float lx = ListenerX, ly = ListenerY, lz = ListenerZ;
            float w = Math.Max(1f, W), h = Math.Max(1f, H), d = Math.Max(1f, D);

            ToRoom(azL, elL, distL, cw, sw, out float ax, out float ay, out float az);
            ToRoom(azR, elR, distR, cw, sw, out float bx, out float by, out float bz);
            BuildFor(0, lx + ax, ly + ay, lz + az, distL, lx, ly, lz, w, h, d, cw, sw, dst);
            BuildFor(1, lx + bx, ly + by, lz + bz, distR, lx, ly, lz, w, h, d, cw, sw, dst);
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
            float cw, float sw, PathInfo[] dst)
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
                    Gain = Math.Min(4f, Math.Max(0.05f, distDirect) / r),
                };
                float ux = dx / r, uy = dy / r, uz = dz / r;
                p.DirX = ux * cw - uz * sw;
                p.DirY = uy;
                p.DirZ = ux * sw + uz * cw;
                dst[src * Walls + wall] = p;
            }
        }
    }

    /// <summary>三频带吸声滤波（LR4 分割 → 每带 Γ=√(1−α) → 求和）。
    /// 用 LR4 不用单对 BW：BW 功率互补但幅度不互补，分频点复数和归零、带间相消出深谷。
    /// 状态每反射路径一份——共用会互相污染滤波器记忆（指向性踩过的坑）。</summary>
    internal sealed class AbsBandFilter
    {
        private const float CrossLow = 250f;   // 低/中 分界
        private const float CrossMid = 2000f;  // 中/高 分界
        private readonly LrCrossover _x1 = new LrCrossover();
        private readonly LrCrossover _x2 = new LrCrossover();
        private float _rate = -1f;

        public float Process(float s, float g0, float g1, float g2, float rate)
        {
            if (rate != _rate)
            {
                _x1.Set(rate, CrossLow);
                _x2.Set(rate, CrossMid);
                _rate = rate;
            }
            float low = _x1.TickLow(s);
            float rest = _x1.TickHigh(s);
            float mid = _x2.TickLow(rest);
            float high = _x2.TickHigh(rest);
            return low * g0 + mid * g1 + high * g2;
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
        private readonly AbsBandFilter[] _filt;         // 每路径一条（材料频带吸收）
        private readonly int[] _surfOf = new int[RoomModel.MaxPaths]; // 命中表面
        private readonly float[,] _gamma = new float[RoomModel.Surfaces, RoomModel.Bands];
        private readonly float[,] _gain = new float[RoomModel.MaxPaths, 2];
        private readonly float[,] _gainT = new float[RoomModel.MaxPaths, 2];
        private readonly float[,] _dlyT = new float[RoomModel.MaxPaths, 2];
        private readonly RoomReverb _reverb = new RoomReverb();
        private readonly float[] _rt = new float[RoomModel.Bands];
        private float[] _revL, _revR;

        public RoomRenderer()
        {
            _delay = new RoomDelay[RoomModel.MaxPaths * 2];
            _filt = new AbsBandFilter[RoomModel.MaxPaths];
            for (int i = 0; i < _delay.Length; i++) _delay[i] = new RoomDelay();
            for (int p = 0; p < RoomModel.MaxPaths; p++)
            {
                _filt[p] = new AbsBandFilter();
                _surfOf[p] = RoomModel.SurfaceOfWall(p % RoomModel.Walls);
            }
        }

        /// <summary>音频线程：把房间贡献加到两耳交错立体声上（累加，不清零）。</summary>
        public void Process(float[] monoL, float[] monoR, float[] earsInterleaved,
            int frames, int rate,
            float azL, float elL, float distL, float azR, float elR, float distR)
        {
            Model.ComputePaths(azL, elL, distL, azR, elR, distR, _paths);
            RefreshTargets(rate);
            RefreshGammas();

            float reflG = (float)Math.Pow(10.0, ReflDb / 20.0);
            for (int i = 0; i < frames; i++)
            {
                float sl = monoL[i], sr = monoR[i];
                float accL = 0f, accR = 0f;
                for (int p = 0; p < RoomModel.MaxPaths; p++)
                {
                    float s = p < RoomModel.Walls ? sl : sr;
                    int surf = _surfOf[p];
                    // 按命中表面的材料吸收着色（低/中/高 Γ）
                    float sf = _filt[p].Process(s,
                        _gamma[surf, 0], _gamma[surf, 1], _gamma[surf, 2], rate);
                    accL += _gain[p, 0] * _delay[p * 2].Process(sf, _dlyT[p, 0]);
                    accR += _gain[p, 1] * _delay[p * 2 + 1].Process(sf, _dlyT[p, 1]);
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
                Model.Rt60Bands(_rt); // 三带 RT60（面积加权）
                _reverb.Process(monoL, monoR, _revL, _revR, frames, rate, _rt[0], _rt[1], _rt[2], Damp);
                for (int i = 0; i < frames; i++)
                {
                    earsInterleaved[i * 2] += _revL[i] * revG;
                    earsInterleaved[i * 2 + 1] += _revR[i] * revG;
                }
            }
        }

        /// <summary>材料 → 每表面每频带 Γ = √(1−α)。</summary>
        private void RefreshGammas()
        {
            for (int s = 0; s < RoomModel.Surfaces; s++)
                for (int b = 0; b < RoomModel.Bands; b++)
                    _gamma[s, b] = (float)Math.Sqrt(
                        Math.Max(0.001, 1.0 - Math.Clamp(Model.Abs[s, b], 0f, 0.999f)));
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
