// 混响尾：8 线 Hadamard FDN（feedback delay network）。
//   - 互质延迟线（48k 基准 ~32-39ms，按采样率缩放）→ 密而无规的模态分布
//   - 反馈矩阵 = 归一化 8 点 Hadamard（正交 → 能量守恒 → 衰减只由每线增益决定）
//   - 每线反馈增益 g_k = 10^(−3·d_k/RT60)（振幅每 RT60 衰 60dB）
//   - 反馈回路里一阶低通做高频衰减（"明暗"滑条）——真实房间 HF 衰得更快
//   - 输出取两组正交符号组合 → 左右去相关
// 无状态参数推拽：RT60/明暗 的目标值每块一阶平滑，稳定不爆。
using System;

namespace VirtualStereo.Dsp
{
    internal sealed class RoomReverb
    {
        private const int Lines = 8;
        private static readonly int[] BaseLen =
            { 1543, 1601, 1657, 1693, 1741, 1783, 1811, 1873 }; // 48k 基准

        private readonly float[][] _buf = new float[Lines][];
        private readonly int[] _len = new int[Lines];
        private readonly int[] _pos = new int[Lines];
        private readonly float[] _read = new float[Lines];
        private readonly float[] _mix = new float[Lines];
        private readonly float[] _lp = new float[Lines];
        private readonly float[] _g = new float[Lines];
        private int _rate;

        public RoomReverb()
        {
            for (int k = 0; k < Lines; k++)
            {
                _buf[k] = new float[8192];
                _g[k] = 0.5f;
            }
        }

        /// <summary>处理一块（单声道馈入 → 去相关双声道写出）。</summary>
        public void Process(float[] monoL, float[] monoR, float[] outL, float[] outR,
            int frames, int rate, float rt60, float damp)
        {
            if (rate != _rate)
            {
                _rate = rate;
                for (int k = 0; k < Lines; k++)
                {
                    int n = (int)(BaseLen[k] * (rate / 48000.0));
                    _len[k] = Math.Clamp(n, 64, 8191);
                    _pos[k] = 0;
                }
            }

            float rt = Math.Clamp(rt60, 0.05f, 8f);
            for (int k = 0; k < Lines; k++)
            {
                // 反馈增益直接设目标值：它只影响尾巴衰减速度，跳变不产生咔哒；
                // （按调用次数平滑会让"一次喂一大块"的场景永远升不到目标 RT60）
                _g[k] = (float)Math.Pow(10.0, -3.0 * (_len[k] / (double)rate) / rt);
            }
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
                    _lp[k] += (_mix[k] * _g[k] - _lp[k]) * dampA;
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
