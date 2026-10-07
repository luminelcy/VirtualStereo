// 输入侧 PEQ：作用在**进音箱之前**的原始 L/R 上（改的是"音频输入响应"），
// 8 带，每带可选 峰化 / 低架 / 高架，左右共用一套系数。
//
// 为什么需要它：现在四对音箱里同一份 M 内容由 4 只同相（低频几乎完全同相）叠加，
// 而高频因各方向 HRTF 不同而部分去相关——结果是低频相对偏重。用输入侧的低架
// 把低频压回来，比在输出侧动增益更干净（不动声像，也不影响房间反射的比例）。
//
// 线程模型与 Directivity 同款：UI 线程写参数，音频线程检测变化就地刷系数
// （元素级并发读写为良性竞争；改系数保留滤波状态，防咔哒）。
using System;

namespace VirtualStereo.Dsp
{
    public sealed class InputEq
    {
        public const int Bands = 8;
        public const int TypePeak = 0, TypeLowShelf = 1, TypeHighShelf = 2;
        public static readonly string[] TypeNames = { "峰值", "低架", "高架" };

        public volatile bool Enabled;

        // 每带参数（UI 写 / 音频线程读）
        public readonly bool[] On = new bool[Bands];
        public readonly int[] Type = new int[Bands];
        public readonly float[] Freq = new float[Bands];
        public readonly float[] GainDb = new float[Bands];
        public readonly float[] Q = new float[Bands];

        private readonly Biquad[][] _ch;          // [2][Bands]
        private readonly Biquad _probe = new Biquad(); // 显示用（UI 线程独享）
        private readonly bool[] _active = new bool[Bands];
        private readonly int[] _aType = new int[Bands];
        private readonly float[] _aFreq = new float[Bands];
        private readonly float[] _aGain = new float[Bands];
        private readonly float[] _aQ = new float[Bands];
        private float _aSr = -1f;

        public InputEq()
        {
            _ch = new Biquad[2][];
            for (int c = 0; c < 2; c++)
            {
                _ch[c] = new Biquad[Bands];
                for (int i = 0; i < Bands; i++) _ch[c][i] = new Biquad();
            }

            float[] defF = { 30f, 300f, 800f, 2000f, 4000f, 8000f, 12000f, 16000f };
            for (int i = 0; i < Bands; i++)
            {
                Freq[i] = defF[i];
                Q[i] = 0.7071f;
                _aType[i] = -1; // 强制首次刷系数
            }

            // 默认：第 1 带 = 低架 30Hz −6dB / Q0.3（很宽的低架，只压次低频），其余关
            Enabled = true;
            On[0] = true;
            Type[0] = TypeLowShelf;
            GainDb[0] = -6f;
            Q[0] = 0.3f;
        }

        /// <summary>音频线程：处理一块（就地，L/R 两条分离缓冲）。</summary>
        public void Process(float[] l, float[] r, int frames, int rate)
        {
            if (!Enabled) return;
            Refresh(rate);
            for (int i = 0; i < frames; i++)
            {
                float sl = l[i], sr = r[i];
                for (int b = 0; b < Bands; b++)
                {
                    if (!_active[b]) continue;
                    sl = _ch[0][b].Tick(sl);
                    sr = _ch[1][b].Tick(sr);
                }
                l[i] = sl;
                r[i] = sr;
            }
        }

        /// <summary>系数刷新（音频线程，参数变化时才动）。</summary>
        private void Refresh(int rate)
        {
            for (int b = 0; b < Bands; b++)
            {
                bool on = On[b] && Math.Abs(GainDb[b]) > 0.001f;
                if (!on)
                {
                    _active[b] = false;
                    _aType[b] = -1;
                    continue;
                }
                _active[b] = true;
                int type = Type[b];
                float f = Freq[b], g = GainDb[b], q = Q[b];
                if (type == _aType[b] && rate == _aSr
                    && Math.Abs(f - _aFreq[b]) < 0.01f
                    && Math.Abs(g - _aGain[b]) < 0.01f
                    && Math.Abs(q - _aQ[b]) < 0.0001f) continue;

                for (int c = 0; c < 2; c++)
                {
                    if (type == TypeLowShelf) _ch[c][b].SetLowShelf(rate, f, q, g);
                    else if (type == TypeHighShelf) _ch[c][b].SetHighShelf(rate, f, q, g);
                    else _ch[c][b].SetPeaking(rate, f, q, g);
                }
                _aType[b] = type;
                _aFreq[b] = f;
                _aGain[b] = g;
                _aQ[b] = q;
            }
            _aSr = rate;
        }

        /// <summary>显示用：当前设置的合成幅响（dB）。</summary>
        public float MagDb(float freq, int rate)
        {
            if (rate <= 0) rate = 48000;
            float sum = 0f;
            for (int b = 0; b < Bands; b++)
            {
                if (!On[b] || Math.Abs(GainDb[b]) <= 0.001f) continue;
                int type = Type[b];
                float f = Freq[b], g = GainDb[b], q = Q[b];
                if (type == TypeLowShelf) _probe.SetLowShelf(rate, f, q, g);
                else if (type == TypeHighShelf) _probe.SetHighShelf(rate, f, q, g);
                else _probe.SetPeaking(rate, f, q, g);
                sum += _probe.MagDb(freq, rate);
            }
            return sum;
        }

        /// <summary>清零滤波状态（开关切换防残留瞬态）。</summary>
        public void Reset()
        {
            for (int c = 0; c < 2; c++)
                for (int b = 0; b < Bands; b++)
                    _ch[c][b].Reset();
        }

        public void SetEnabled(bool on)
        {
            if (Enabled == on) return;
            Enabled = on;
            Reset();
        }

        /// <summary>全部平直（关所有带）。</summary>
        public void ClearAll()
        {
            for (int b = 0; b < Bands; b++) On[b] = false;
        }

        /// <summary>回到默认：第 1 带低架 30Hz −6dB / Q0.3。</summary>
        public void ResetToDefault()
        {
            ClearAll();
            Type[0] = TypeLowShelf;
            Freq[0] = 30f;
            GainDb[0] = -6f;
            Q[0] = 0.3f;
            On[0] = true;
            Reset();
        }
    }
}
