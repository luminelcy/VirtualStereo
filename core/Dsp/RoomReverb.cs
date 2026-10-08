// 混响尾：8 线 Hadamard FDN（feedback delay network），**按频带差异化衰减**。
//   - 互质延迟线（48k 基准 ~32-39ms，按采样率缩放）→ 密而无规的模态分布
//   - 反馈矩阵 = 归一化 8 点 Hadamard（正交 → 能量守恒 → 衰减只由增益决定）
//   - 每线反馈：三频带分割（250Hz/2k，与反射同一分界）→ 每带增益
//     g_{k,b} = 10^(−3·d_k/RT60_b)——RT60 三带来自 Sabine 按表面面积加权，
//     于是"地毯的房间"尾巴高频先死、低频拖着，和真实房间一个样
//   - 再过一阶低通做"明暗"用户微调（材料之外的手动倾斜）
//   - 输出取两组正交符号组合 → 左右去相关
// 增益直接设目标值：只影响尾巴衰减速度，跳变不产生咔哒，且对"一次喂一大块"也正确。
using System;

namespace VirtualStereo.Dsp
{
    internal sealed class RoomReverb
    {
        private const int Lines = 8;
        private const float CrossLow = 250f;
        private const float CrossMid = 2000f;
        private static readonly int[] BaseLen =
            { 1543, 1601, 1657, 1693, 1741, 1783, 1811, 1873 }; // 48k 基准

        private readonly float[][] _buf = new float[Lines][];
        private readonly int[] _len = new int[Lines];
        private readonly int[] _pos = new int[Lines];
        private readonly float[] _read = new float[Lines];
        private readonly float[] _mix = new float[Lines];
        private readonly float[] _lp = new float[Lines];      // 明暗低通状态
        private readonly float[] _g0 = new float[Lines];      // 每线低频带增益
        private readonly float[] _g1 = new float[Lines];
        private readonly float[] _g2 = new float[Lines];
        private readonly LrCrossover[][] _xr;                 // LR4 频带分割（每线两级）
        private int _rate;

        public RoomReverb()
        {
            for (int k = 0; k < Lines; k++) _buf[k] = new float[8192];
            _xr = new LrCrossover[Lines][];
            for (int k = 0; k < Lines; k++)
            {
                _xr[k] = new[] { new LrCrossover(), new LrCrossover() };
                _g0[k] = _g1[k] = _g2[k] = 0.5f;
            }
        }

        /// <summary>处理一块（单声道馈入 → 去相关双声道写出）。RT60 三带单位秒。</summary>
        /// <summary>清空混响内部状态（延迟线、低通、滤波器）。</summary>
        public void Reset()
        {
            for (int k = 0; k < Lines; k++)
            {
                Array.Clear(_buf[k], 0, _buf[k].Length);
                _pos[k] = 0;
                _lp[k] = 0f;
                _read[k] = 0f;
                _mix[k] = 0f;
                _xr[k][0].Reset();
                _xr[k][1].Reset();
            }
        }

        public void Process(float[] monoL, float[] monoR, float[] outL, float[] outR,
            int frames, int rate, float rtLow, float rtMid, float rtHigh, float damp)
        {
            if (rate != _rate)
            {
                _rate = rate;
                for (int k = 0; k < Lines; k++)
                {
                    int n = (int)(BaseLen[k] * (rate / 48000.0));
                    _len[k] = Math.Clamp(n, 64, 8191);
                    _pos[k] = 0;
                    _xr[k][0].Set(rate, CrossLow);
                    _xr[k][1].Set(rate, CrossMid);
                }
            }

            // 每线每带：RT60 → 反馈增益（振幅每 RT60 衰 60dB）
            BandGain(rtLow, _g0);
            BandGain(rtMid, _g1);
            BandGain(rtHigh, _g2);
            float dampA = 1f - 0.9f * Math.Clamp(damp, 0f, 1f); // 1=亮 … 0.1=暗

            for (int i = 0; i < frames; i++)
            {
                float x = (monoL[i] + monoR[i]) * 0.35f;
                for (int k = 0; k < Lines; k++)
                {
                    _read[k] = _buf[k][_pos[k]];
                    _mix[k] = _read[k];
                }

                // 归一化 Hadamard（FWHT/√8）：正交反馈
                Fwht(_mix);
                for (int k = 0; k < Lines; k++) _mix[k] *= 0.3535534f;

                for (int k = 0; k < Lines; k++)
                {
                    // 三频带差异化衰减（LR4 重组）：高频吸得多 → 高频带增益小 → 尾巴变暗
                    float lo = _xr[k][0].TickLow(_mix[k]);
                    float rest = _xr[k][0].TickHigh(_mix[k]);
                    float mi = _xr[k][1].TickLow(rest);
                    float hi = _xr[k][1].TickHigh(rest);
                    float fb = lo * _g0[k] + mi * _g1[k] + hi * _g2[k];
                    // 冲掉非规格化数：混响尾衰到 1e-38 附近时，CPU 会因 denormal
                    // 变慢几十倍，进而让音频线程喂不上声卡（听感就是咔哒/电流声）。
                    if (fb > -1e-15f && fb < 1e-15f) fb = 0f;
                    _lp[k] += (fb - _lp[k]) * dampA;
                    if (_lp[k] > -1e-15f && _lp[k] < 1e-15f) _lp[k] = 0f;
                    _buf[k][_pos[k]] = x + _lp[k];
                    if (++_pos[k] >= _len[k]) _pos[k] = 0;
                }

                // 两组正交符号组合 → 去相关左右
                outL[i] = 0.5f * (_read[0] + _read[1] + _read[2] + _read[3]
                                - _read[4] - _read[5] - _read[6] - _read[7]);
                outR[i] = 0.5f * (_read[0] - _read[1] + _read[2] - _read[3]
                                + _read[4] - _read[5] + _read[6] - _read[7]);
            }
        }

        private void BandGain(float rt60, float[] dst)
        {
            float rt = Math.Clamp(rt60, 0.05f, 8f);
            for (int k = 0; k < Lines; k++)
                dst[k] = (float)Math.Pow(10.0, -3.0 * (_len[k] / (double)_rate) / rt);
        }

        /// <summary>就地 8 点快速 Walsh-Hadamard 变换（未归一）。</summary>
        private static void Fwht(float[] v)
        {
            for (int stride = 1; stride < Lines; stride <<= 1)
            {
                for (int blk = 0; blk < Lines; blk += stride << 1)
                {
                    for (int j = 0; j < stride; j++)
                    {
                        float a = v[blk + j], b = v[blk + j + stride];
                        v[blk + j] = a + b;
                        v[blk + j + stride] = a - b;
                    }
                }
            }
        }
    }
}
