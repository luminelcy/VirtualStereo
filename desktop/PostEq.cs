// 后处理 PEQ：校准 EQ 之后、播放之前的输出调音（用户自定义频响）。
//
// 8 带，每带可选 峰化 / 低架 / 高架，左右声道共用一套系数（听感调音）。
// 典型用途：校准把两耳频响拉平后低音显薄，用低架把低频垫回来。
//
// 线程模型与 Directivity 同款：UI 线程写参数，音频线程检测变化就地刷系数
// （元素级并发读写为良性竞争；改系数保留滤波状态，防咔哒）。
// 显示用幅响走独立探针双二阶，与处理路径不互踩。
using System;
using VirtualStereo.Dsp;

namespace VirtualStereo.Desktop
{
    internal sealed class PostEq
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

        public PostEq()
        {
            _ch = new Biquad[2][];
            for (int c = 0; c < 2; c++)
            {
                _ch[c] = new Biquad[Bands];
                for (int i = 0; i < Bands; i++) _ch[c][i] = new Biquad();
            }
            // 默认：全关；频点铺开一组常用的
            float[] defF = { 60f, 150f, 400f, 1000f, 2500f, 6000f, 12000f, 16000f };
            for (int i = 0; i < Bands; i++)
            {
                Freq[i] = defF[i];
                Q[i] = 0.7071f;
                _aType[i] = -1; // 强制首次刷系数
            }
        }

        /// <summary>音频线程：处理一块交错立体声（就地）。</summary>
        public void Process(float[] stereo, int frames, int rate)
        {
            Refresh(rate);
            for (int i = 0; i < frames; i++)
            {
                float l = stereo[i * 2], r = stereo[i * 2 + 1];
                for (int b = 0; b < Bands; b++)
                {
                    if (!_active[b]) continue;
                    l = _ch[0][b].Tick(l);
                    r = _ch[1][b].Tick(r);
                }
                stereo[i * 2] = l;
                stereo[i * 2 + 1] = r;
            }
        }

        private void Refresh(int rate)
        {
            for (int b = 0; b < Bands; b++)
            {
                bool on = On[b] && Math.Abs(GainDb[b]) > 0.001f;
                if (!on)
                {
                    _active[b] = false;
                    _aType[b] = -1; // 下次启用重刷系数
                    continue;
                }
                _active[b] = true;
                int type = Type[b];
                float f = Freq[b], g = GainDb[b], q = Q[b];
                if (type == _aType[b] && rate == _aSr
                    && Math.Abs(f - _aFreq[b]) < 0.01f
                    && Math.Abs(g - _aGain[b]) < 0.01f
                    && Math.Abs(q - _aQ[b]) < 0.0001f) continue;

                // 刷系数（保留滤波状态，防咔哒）
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

        /// <summary>显示用：当前设置的合成幅响（dB，全部带叠加）。</summary>
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

        /// <summary>清零状态（开关切换时防残留瞬态）。</summary>
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
    }
}
