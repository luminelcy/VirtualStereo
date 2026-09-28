// 校准模式：虚拟音箱扫频 → 测"模拟人头两耳"频响 → 反相 EQ 拉平（可开可关）。
//
// 链路与 tap 点（全数字域测量，无传声器）：
//   [扫频注入替换两路音箱信号] → 指向性 → 距离 → 空间模拟 → 两耳 tap（测量/监视同点）
//   → 校准 EQ（输出侧） → 后置增益 → 出声
// 测量 tap 在校准 EQ 之前 → 重校准永远测的是裸链路，与校准开关无关；
// 两耳分析/录制也始终是裸双耳信号（实验数据），EQ 只作用于耳机回放这最后一段。
//
// 测量：双音箱**同时**扫频（同一对数扫频信号分别进两路音箱），
// 10Hz .. min(24k, 0.495×fs)；两耳 tap 各直接测得一条 FRF——即"该耳的
// 频响曲线"（= 左右两路传递函数之和）。
//
// 反相：H = Y·conj(S)/|S|²（谱除，弱能量 bin 跳过）→ 对数栅格带内平均
// → 平滑 → 中带归一（不改整体响度）→ 限幅（深谷不猛抬）→ 每耳
// 级联峰化双二阶（图形 EQ 式）。平滑/提升上限改动即时重设计，不用重扫。
using System;
using System.Threading.Tasks;
using VirtualStereo.Dsp;

namespace VirtualStereo.Desktop
{
    internal sealed class Calibration
    {
        // ── 扫频参数 ──
        public const float F1 = 10f;
        public const float F2Limit = 24000f;
        private const double Amp = 0.2;           // 注入幅度（数字域，留足余量）
        private const double SweepSeconds = 10.0; // 每音箱扫频时长（LF 分辨靠它）
        private const double TailSeconds = 0.25;  // 扫后尾巴：收滤波器/HRTF 冲出

        public const int GridN = 128;   // 对数频点栅格（显示 + 设计）
        public const int MaxBands = 32; // 校准 EQ 峰化带上限

        // ── 状态机（音频线程推进 / UI 轮询）──
        public const int Idle = 0, Sweep = 1,
                          Done = 2, Analyzing = 3, Ready = 4, Failed = 5;

        public volatile int State = Idle;
        public volatile float Progress;    // 0..1
        public volatile float CurFreq;     // 当前扫到的频率
        public volatile string Message = "";
        public long DoneAtTicks; // volatile 不支持 long；发布顺序由随后的 State 写担保
        public volatile bool DesignDirty;  // UI 观察到后调 Redesign()
        public volatile bool HasCurve;
        public volatile bool Enabled;      // 校准模式开/关（应用 EQ）

        // 设计参数（UI 写 / Redesign 读）
        public volatile float SmoothOct = 1f / 3f; // 平滑倍频程
        public volatile float MaxBoostDb = 6f;     // 提升上限（深谷不猛抬）

        // 测量结果（分析线程发布后 UI 只读；Corr/Pred/Disp 仅 UI 线程写）
        public readonly float[] GridHz = new float[GridN];
        public readonly float[] MeasL = new float[GridN]; // 实测 dB（原始电平）
        public readonly float[] MeasR = new float[GridN];
        public readonly float[] DispL = new float[GridN]; // 平滑后中带归一（显示）
        public readonly float[] DispR = new float[GridN];
        public readonly float[] CorrL = new float[GridN]; // 校准 EQ 目标 dB
        public readonly float[] CorrR = new float[GridN];
        public readonly float[] PredL = new float[GridN]; // 校准后预期 dB
        public readonly float[] PredR = new float[GridN];
        public float MeasF1 = F1, MeasF2 = F2Limit;
        public float MeasRate = 48000;

        // ── 扫频发生/捕获（音频线程）──
        private double _L;               // 扫频指数：phase = 2π f1 L (e^{t/L} − 1)
        private int _sweepLen, _tailLen; // 样本数
        private long _pos;               // 全程单调计数 0..sweepLen+tailLen
        private long _blkBase;           // 本块起点（OnEars 与 Generate 同块对齐）
        private bool _blkActive;
        private int _rate = 48000;
        private int _runId;
        private float[] _capL, _capR;

        // 设计 scratch（UI 线程）
        private readonly float[] _smL = new float[GridN];
        private readonly float[] _smR = new float[GridN];
        private readonly float[] _bandHz = new float[MaxBands];
        private readonly float[] _gL = new float[MaxBands];
        private readonly float[] _gR = new float[MaxBands];

        private readonly Corrector _eq = new Corrector();

        public bool IsBusy => State == Sweep || State == Analyzing;

        /// <summary>启动校准。返回 null=成功 / 错误信息。</summary>
        public string Start(int sampleRate)
        {
            if (IsBusy) return "校准进行中";
            if (sampleRate < 8000) sampleRate = 48000;
            _rate = sampleRate;
            MeasRate = sampleRate;
            MeasF1 = F1;
            MeasF2 = Math.Min(F2Limit, sampleRate * 0.495f);
            _L = SweepSeconds / Math.Log(MeasF2 / F1);
            _sweepLen = (int)(SweepSeconds * sampleRate);
            _tailLen = (int)(TailSeconds * sampleRate);
            int n = _sweepLen + _tailLen;
            _capL = new float[n];
            _capR = new float[n];
            _pos = 0;
            _blkActive = false;
            Message = "";
            DoneAtTicks = 0;
            _runId++;
            State = Sweep;
            Progress = 0f;
            return null;
        }

        public void Cancel()
        {
            _runId++;
            State = Idle;
            _blkActive = false;
            Message = "已取消";
            Progress = 0f;
        }

        // ── 音频线程：生成扫频（替换两路音箱信号，双音箱同扫）──
        public void Generate(float[] monoL, float[] monoR, int frames, int sampleRate)
        {
            if (State != Sweep) { _blkActive = false; return; }

            int total = _sweepLen + _tailLen;
            _blkBase = _pos;
            _blkActive = true;

            for (int i = 0; i < frames; i++)
            {
                long p = _pos + i;
                if (p >= _sweepLen)
                {
                    monoL[i] = 0f; monoR[i] = 0f; // 尾巴：静默收冲出
                    continue;
                }
                // 无状态发生器：相位由样本位置直接算（长扫不漂移）
                double t = p / (double)sampleRate;
                double ph = 2.0 * Math.PI * F1 * _L * (Math.Exp(t / _L) - 1.0);
                float s = (float)(Amp * Math.Sin(ph));
                monoL[i] = s; // 双音箱同时播同一扫频
                monoR[i] = s;
            }

            long after = _pos + frames;
            long shown = after < total ? after : total;
            Progress = (float)(shown / (double)total);
            long cur = shown < total ? shown : total - 1;
            CurFreq = cur < _sweepLen
                ? (float)(F1 * Math.Exp((cur / (double)sampleRate) / _L))
                : 0f;

            _pos = after;
            if (_pos >= total) State = Done;
        }

        /// <summary>音频线程：两耳 tap 捕获（与 Generate 的块对齐）。</summary>
        public void OnEars(float[] stereo, int frames)
        {
            if (!_blkActive || _capL == null || _capR == null) return;
            int total = _sweepLen + _tailLen;
            for (int i = 0; i < frames; i++)
            {
                long p = _blkBase + i;
                if (p >= total) break;
                int off = (int)p;
                if (off >= _capL.Length) continue;
                _capL[off] = stereo[i * 2];
                _capR[off] = stereo[i * 2 + 1];
            }
        }

        /// <summary>输出侧校准 EQ（仅 Enabled 时生效）。</summary>
        public void Process(float[] stereo, int frames)
        {
            if (Enabled) _eq.Process(stereo, frames);
        }

        public void SetEnabled(bool on)
        {
            if (Enabled == on) return;
            Enabled = on;
            _eq.ResetAll(); // 清状态，防开关切换残留瞬态
        }

        // ── 分析（后台线程一次算完）──
        public void Analyze()
        {
            int runId = _runId;
            try
            {
                AnalyzeCore(runId);
            }
            catch (Exception e)
            {
                if (runId == _runId)
                {
                    Message = "分析失败: " + e.Message;
                    State = Failed;
                }
            }
        }

        private void AnalyzeCore(int runId)
        {
            // 先抓本地引用，防与下一轮 Start 互踩
            float[] cL = _capL, cR = _capR;
            if (cL == null || cR == null)
            {
                Message = "无测量数据";
                State = Failed;
                return;
            }

            int rate = _rate;
            int n = _sweepLen + _tailLen;
            int p = 1;
            while (p < n) p <<= 1;

            // 重建注入扫频（与 Generate 同公式）
            var sRe = new float[p];
            var sIm = new float[p];
            for (int i = 0; i < _sweepLen; i++)
            {
                double t = i / (double)rate;
                double ph = 2.0 * Math.PI * F1 * _L * (Math.Exp(t / _L) - 1.0);
                sRe[i] = (float)(Amp * Math.Sin(ph));
            }
            Fft.Forward(sRe, sIm);

            float maxS2 = 0f;
            for (int k = 0; k < p; k++)
            {
                float s2 = sRe[k] * sRe[k] + sIm[k] * sIm[k];
                if (s2 > maxS2) maxS2 = s2;
            }
            float thr = maxS2 * 1e-6f; // 扫频带外弱能量 bin 跳过

            // 每耳 H = Y·conj(S)/|S|²（双音箱同扫，测得即两路传递函数之和）
            var hLRe = new float[p]; var hLIm = new float[p];
            var hRRe = new float[p]; var hRIm = new float[p];
            AccumulateOne(cL, sRe, sIm, thr, hLRe, hLIm, p);
            AccumulateOne(cR, sRe, sIm, thr, hRRe, hRIm, p);
            if (runId != _runId) return;

            BuildGrid();
            ToGrid(hLRe, hLIm, sRe, sIm, thr, p, rate, GridHz, MeasL);
            ToGrid(hRRe, hRIm, sRe, sIm, thr, p, rate, GridHz, MeasR);

            _capL = _capR = null; // 捕获缓冲用完即放
            DoneAtTicks = DateTime.Now.Ticks;
            HasCurve = true;
            DesignDirty = true;
            if (runId == _runId) State = Ready;
        }

        /// <summary>一条捕获 → FFT → H = Y·conj(S)/|S|² 累加进目标耳。</summary>
        private static void AccumulateOne(float[] y,
            float[] sRe, float[] sIm, float thr,
            float[] dstRe, float[] dstIm, int p)
        {
            var re = new float[p];
            var im = new float[p];
            int n = y.Length < p ? y.Length : p;
            Array.Copy(y, re, n);
            Fft.Forward(re, im);
            for (int k = 0; k < p; k++)
            {
                float sr = sRe[k], si = sIm[k];
                float s2 = sr * sr + si * si;
                if (s2 < thr) continue;
                float yr = re[k], yi = im[k];
                dstRe[k] += (yr * sr + yi * si) / s2;
                dstIm[k] += (si * yr - sr * yi) / s2;
            }
        }

        private void BuildGrid()
        {
            double ratio = Math.Log(MeasF2 / MeasF1);
            for (int i = 0; i < GridN; i++)
                GridHz[i] = (float)(MeasF1 * Math.Exp(ratio * i / (GridN - 1)));
        }

        /// <summary>复数 H → 对数栅格带内平均 → dB（带空缺向外扩找有效 bin）。</summary>
        private static void ToGrid(float[] hRe, float[] hIm, float[] sRe, float[] sIm,
            float thr, int p, int rate, float[] grid, float[] dst)
        {
            int gn = grid.Length;
            double gridOct = Math.Log(grid[gn - 1] / grid[0], 2.0) / (gn - 1);
            for (int i = 0; i < gn; i++)
            {
                float fc = grid[i];
                double sum = 0;
                int cnt = 0;
                for (int pass = 0; pass < 6 && cnt == 0; pass++)
                {
                    double half = Math.Pow(2.0, gridOct * (0.5 + pass));
                    int k0 = Math.Max(1, (int)(fc / half / rate * p));
                    int k1 = Math.Min(p - 1, (int)(fc * half / rate * p) + 1);
                    for (int k = k0; k <= k1; k++)
                    {
                        float sr = sRe[k], si = sIm[k];
                        if (sr * sr + si * si < thr) continue;
                        double mag = Math.Sqrt((double)hRe[k] * hRe[k] + (double)hIm[k] * hIm[k]);
                        sum += mag;
                        cnt++;
                    }
                }
                dst[i] = cnt > 0
                    ? (float)(20.0 * Math.Log10(Math.Max(1e-12, sum / cnt)))
                    : -60f;
            }
        }

        // ── 设计（UI 线程；平滑/提升上限改动即时重算）──
        public void Redesign()
        {
            if (!HasCurve) return;
            float smooth = SmoothOct;
            float maxBoost = MaxBoostDb;

            double spanOct = Math.Log(GridHz[GridN - 1] / GridHz[0], 2.0);
            float gridOct = (float)(spanOct / (GridN - 1));
            SmoothInto(MeasL, _smL, smooth, gridOct);
            SmoothInto(MeasR, _smR, smooth, gridOct);

            float refL = MidbandMean(_smL);
            float refR = MidbandMean(_smR);
            for (int i = 0; i < GridN; i++)
            {
                DispL[i] = _smL[i] - refL;
                DispR[i] = _smR[i] - refR;
                CorrL[i] = Clamp(-DispL[i], -30f, maxBoost);
                CorrR[i] = Clamp(-DispR[i], -30f, maxBoost);
            }

            // 峰化 EQ：1/3 倍频程布带（≤32），Q 由带宽公式反推
            int n = (int)Math.Round(spanOct / (1.0 / 3.0)) + 1;
            if (n < 8) n = 8;
            if (n > MaxBands) n = MaxBands;
            float spacing = (float)(spanOct / (n - 1));
            float q = (float)(1.0 / (2.0 * Math.Sinh(0.5 * Math.Log(2.0) * spacing)));
            for (int b = 0; b < n; b++)
            {
                _bandHz[b] = (float)(GridHz[0] * Math.Pow(2.0, spacing * b));
                _gL[b] = InterpAt(CorrL, _bandHz[b]);
                _gR[b] = InterpAt(CorrR, _bandHz[b]);
            }
            _eq.ApplyDesign(_bandHz, _gL, _gR, n, q, (int)MeasRate);

            // 校准后预期 = 平滑实测(归一) + EQ 合成响应（看"拉平了没有"）
            for (int i = 0; i < GridN; i++)
            {
                float sumL = 0f, sumR = 0f;
                for (int b = 0; b < n; b++)
                {
                    sumL += _eq.MagDb(0, b, GridHz[i]);
                    sumR += _eq.MagDb(1, b, GridHz[i]);
                }
                PredL[i] = DispL[i] + sumL;
                PredR[i] = DispR[i] + sumR;
            }
        }

        /// <summary>设置恢复：载入上次测量曲线（参数可继续调，不必重扫）。</summary>
        public bool LoadMeasured(float[] grid, float[] mL, float[] mR, float f1, float f2, long ticks)
        {
            if (grid == null || mL == null || mR == null || grid.Length != GridN) return false;
            Array.Copy(grid, GridHz, GridN);
            Array.Copy(mL, MeasL, GridN);
            Array.Copy(mR, MeasR, GridN);
            MeasF1 = f1;
            MeasF2 = f2;
            DoneAtTicks = ticks;
            HasCurve = true;
            DesignDirty = true;
            return true;
        }

        /// <summary>诊断：取校准 EQ 某带的幅响（dB）。</summary>
        public float MagDbProbe(int ch, int band, float freq) => _eq.MagDb(ch, band, freq);

        /// <summary>校准 EQ 的合成幅响（dB）。未校准或模式关 = 0（输出曲线合成用）。</summary>
        public float EqMagDb(float freq)
        {
            if (!HasCurve || !Enabled) return 0f;
            return _eq.MagSumDb(0, freq);
        }

        private static void SmoothInto(float[] src, float[] dst, float smoothOct, float gridOct)
        {
            int w = (int)Math.Round(smoothOct / Math.Max(1e-6f, gridOct));
            if (w < 1) w = 1;
            if ((w & 1) == 0) w++;
            int half = w / 2;
            int n = src.Length;
            for (int i = 0; i < n; i++)
            {
                double sum = 0;
                int cnt = 0;
                for (int j = i - half; j <= i + half; j++)
                {
                    if (j < 0 || j >= n) continue;
                    sum += src[j];
                    cnt++;
                }
                dst[i] = (float)(sum / Math.Max(1, cnt));
            }
        }

        /// <summary>中带（200..4000Hz）均值：归一参考，校准不改整体响度。</summary>
        private float MidbandMean(float[] sm)
        {
            double sum = 0;
            int cnt = 0;
            for (int i = 0; i < GridN; i++)
            {
                if (GridHz[i] < 200f || GridHz[i] > 4000f) continue;
                sum += sm[i];
                cnt++;
            }
            return cnt > 0 ? (float)(sum / cnt) : sm[GridN / 2];
        }

        /// <summary>对数频率插值取校准曲线值。</summary>
        private float InterpAt(float[] curve, float f)
        {
            if (f <= GridHz[0]) return curve[0];
            if (f >= GridHz[GridN - 1]) return curve[GridN - 1];
            double lf = Math.Log(f);
            for (int i = 1; i < GridN; i++)
            {
                if (GridHz[i] < f) continue;
                double t = (lf - Math.Log(GridHz[i - 1]))
                    / (Math.Log(GridHz[i]) - Math.Log(GridHz[i - 1]));
                return (float)(curve[i - 1] + (curve[i] - curve[i - 1]) * t);
            }
            return curve[GridN - 1];
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>每耳级联峰化双二阶（图形 EQ）。音频线程 Tick / UI 线程改系数。</summary>
        private sealed class Corrector
        {
            private readonly Biquad[][] _ch;
            private readonly object _lock = new object();
            private int _n;
            private int _rate = 48000;

            public Corrector()
            {
                _ch = new Biquad[2][];
                for (int c = 0; c < 2; c++)
                {
                    _ch[c] = new Biquad[MaxBands];
                    for (int i = 0; i < MaxBands; i++) _ch[c][i] = new Biquad();
                }
            }

            public void ApplyDesign(float[] hz, float[] gL, float[] gR, int n, float q, int rate)
            {
                lock (_lock)
                {
                    _rate = rate > 0 ? rate : 48000;
                    for (int i = 0; i < n; i++)
                    {
                        _ch[0][i].SetPeaking(_rate, hz[i], q, gL[i]);
                        _ch[1][i].SetPeaking(_rate, hz[i], q, gR[i]);
                    }
                    _n = n;
                }
            }

            public float MagDb(int ch, int band, float freq)
            {
                lock (_lock) return _ch[ch][band].MagDb(freq, _rate);
            }

            public float MagSumDb(int ch, float freq)
            {
                lock (_lock)
                {
                    float sum = 0f;
                    for (int b = 0; b < _n; b++) sum += _ch[ch][b].MagDb(freq, _rate);
                    return sum;
                }
            }

            public void Process(float[] stereo, int frames)
            {
                lock (_lock)
                {
                    if (_n <= 0) return;
                    for (int i = 0; i < frames; i++)
                    {
                        float l = stereo[i * 2], r = stereo[i * 2 + 1];
                        for (int b = 0; b < _n; b++) l = _ch[0][b].Tick(l);
                        for (int b = 0; b < _n; b++) r = _ch[1][b].Tick(r);
                        stereo[i * 2] = l;
                        stereo[i * 2 + 1] = r;
                    }
                }
            }

            public void ResetAll()
            {
                lock (_lock)
                {
                    for (int c = 0; c < 2; c++)
                        for (int b = 0; b < MaxBands; b++)
                            _ch[c][b].Reset();
                }
            }
        }
    }
}
