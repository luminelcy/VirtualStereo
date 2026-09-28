// 音箱校准：每只音箱单独扫频 → 测"该音箱 → 两耳"的频响 → 源端反相 EQ 补偿到平直。
//
// 与"校准"（输出校准）是链路上不同层的两块地基：
//   输出校准（Calibration.cs）：双音箱同扫、**含 HRTF**、EQ 在输出侧——
//     把"耳机听到的一切"拉平（含 HRTF 的频谱塑形；要保双耳线索就关掉它）。
//   音箱校准（本模块）：每只单独扫、**测量时强制直通旁路 HRTF**（房间反射仍在）、
//     EQ 在源端（指向性之前）——拉平"音箱本身"（指向性/距离/房间的染色），
//     HRTF 叠在其上不受影响 → 双耳线索原样保留。
//
// 链路（音频线程）：
//   前置增益 → [输出校准扫频注入] → 音箱EQ(源端) → [音箱校准扫频注入=旁路自身EQ]
//   → 指向性 → 距离 → (测量中:直通 / 否则:双耳) → 房间 → 两耳 tap
// 测量 tap 与输出校准同点（在输出校准 EQ 之前）；每次测量都测裸链路。
// 补偿目标：该音箱两耳响应的 dB 平均（单源两耳 → 一条曲线）。
// 测量含房间：改指向性/摆位/房间参数 → 重新校准。
//
// 状态机与分析管线同 Calibration：Done → UI 泵进后台 Analyze → Ready → Redesign。
using System;
using System.Threading.Tasks;
using VirtualStereo.Dsp;

namespace VirtualStereo.Desktop
{
    internal sealed class SpeakerCal
    {
        // ── 扫频参数（与输出校准同族，但每箱各扫一遍）──
        private const double Amp = 0.2;
        private const double SweepSeconds = 8.0; // 每箱 8s（两箱共 ~17s）
        private const double TailSeconds = 0.25;

        public const int GridN = Calibration.GridN;
        public const int MaxBands = Calibration.MaxBands;

        // ── 状态机 ──
        public const int Idle = 0, SweepL = 1, SweepR = 2,
                          Done = 3, Analyzing = 4, Ready = 5, Failed = 6;

        public volatile int State = Idle;
        public volatile float Progress;   // 0..1（两箱合计）
        public volatile float CurFreq;
        public volatile string Message = "";
        public long DoneAtTicks;
        public volatile bool DesignDirty;
        public volatile bool HasCurve;
        public volatile bool Enabled;

        public volatile float SmoothOct = 1f / 3f;
        public volatile float MaxBoostDb = 6f;

        /// <summary>测量中（直通旁路 HRTF 用——FillStage 据此强制直通）。</summary>
        public bool Measuring => State == SweepL || State == SweepR;

        // 测量结果（每音箱两耳 dB 平均）
        public readonly float[] GridHz = new float[GridN];
        public readonly float[] MeasSrcL = new float[GridN];
        public readonly float[] MeasSrcR = new float[GridN];
        public readonly float[] DispSrcL = new float[GridN];
        public readonly float[] DispSrcR = new float[GridN];
        public readonly float[] CorrSrcL = new float[GridN];
        public readonly float[] CorrSrcR = new float[GridN];
        public readonly float[] PredSrcL = new float[GridN];
        public readonly float[] PredSrcR = new float[GridN];
        public float MeasF1 = Calibration.F1, MeasF2 = Calibration.F2Limit;
        public float MeasRate = 48000;

        // ── 扫频发生/捕获（音频线程）──
        private double _L;
        private int _sweepLen, _tailLen;
        private long _pos;          // 每箱内单调计数
        private long _blkBase;
        private bool _blkLeft;      // 本块属于哪只箱（OnEars 路由）
        private bool _blkActive;
        private int _rate = 48000;
        private int _runId;
        private float[] _capL0, _capR0, _capL1, _capR1; // [每箱][耳]

        // 设计 scratch（UI 线程）
        private readonly float[] _smL = new float[GridN];
        private readonly float[] _smR = new float[GridN];
        private readonly float[] _bandHz = new float[MaxBands];
        private readonly float[] _gL = new float[MaxBands];
        private readonly float[] _gR = new float[MaxBands];

        private readonly EqCorrector _eq = new EqCorrector();

        public bool IsBusy => State == SweepL || State == SweepR || State == Analyzing;

        /// <summary>启动音箱校准。返回 null=成功 / 错误信息。</summary>
        public string Start(int sampleRate)
        {
            if (IsBusy) return "音箱校准进行中";
            if (sampleRate < 8000) sampleRate = 48000;
            _rate = sampleRate;
            MeasRate = sampleRate;
            MeasF1 = Calibration.F1;
            MeasF2 = Math.Min(Calibration.F2Limit, sampleRate * 0.495f);
            _L = SweepSeconds / Math.Log(MeasF2 / Calibration.F1);
            _sweepLen = (int)(SweepSeconds * sampleRate);
            _tailLen = (int)(TailSeconds * sampleRate);
            int n = _sweepLen + _tailLen;
            _capL0 = new float[n]; _capR0 = new float[n];
            _capL1 = new float[n]; _capR1 = new float[n];
            _pos = 0;
            _blkActive = false;
            Message = "";
            DoneAtTicks = 0;
            _runId++;
            State = SweepL;
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

        // ── 音频线程：扫频发生（每箱一遍；替换信号=旁路自身 EQ）──
        public void Generate(float[] monoL, float[] monoR, int frames, int sampleRate)
        {
            int st = State;
            if (st != SweepL && st != SweepR) { _blkActive = false; return; }

            long per = _sweepLen + _tailLen;
            bool left = st == SweepL;
            _blkBase = _pos;
            _blkLeft = left;
            _blkActive = true;

            for (int i = 0; i < frames; i++)
            {
                long p = _pos + i;
                if (p >= _sweepLen)
                {
                    monoL[i] = 0f; monoR[i] = 0f; // 尾巴
                    continue;
                }
                double t = p / (double)sampleRate;
                double ph = 2.0 * Math.PI * Calibration.F1 * _L * (Math.Exp(t / _L) - 1.0);
                float s = (float)(Amp * Math.Sin(ph));
                if (left) { monoL[i] = s; monoR[i] = 0f; }
                else { monoL[i] = 0f; monoR[i] = s; }
            }

            long after = _pos + frames;
            // 总进度 = 两箱合计
            double done = (left ? 0.0 : per) + Math.Min(after, per);
            Progress = (float)(done / (per * 2.0));
            long cur = Math.Min(after, per);
            CurFreq = cur < _sweepLen
                ? (float)(Calibration.F1 * Math.Exp((cur / (double)sampleRate) / _L))
                : 0f;

            _pos = after;
            if (_pos >= per)
            {
                if (left) { State = SweepR; _pos = 0; }
                else State = Done;
            }
        }

        /// <summary>音频线程：两耳 tap 捕获（与 Generate 块对齐，按箱路由）。</summary>
        public void OnEars(float[] stereo, int frames)
        {
            if (!_blkActive) return;
            float[] cl = _blkLeft ? _capL0 : _capL1;
            float[] cr = _blkLeft ? _capR0 : _capR1;
            if (cl == null || cr == null) return;
            long per = _sweepLen + _tailLen;
            for (int i = 0; i < frames; i++)
            {
                long p = _blkBase + i;
                if (p >= per) break;
                int off = (int)p;
                if (off >= cl.Length) continue;
                cl[off] = stereo[i * 2];
                cr[off] = stereo[i * 2 + 1];
            }
        }

        /// <summary>源端补偿（指向性之前；仅 Enabled 时生效）。</summary>
        public void Process(float[] monoL, float[] monoR, int frames)
        {
            if (Enabled) _eq.ProcessDual(monoL, monoR, frames);
        }

        public void SetEnabled(bool on)
        {
            if (Enabled == on) return;
            Enabled = on;
            _eq.ResetAll();
        }

        // ── 分析（后台线程）──
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
            float[] l0 = _capL0, r0 = _capR0, l1 = _capL1, r1 = _capR1;
            if (l0 == null || r0 == null || l1 == null || r1 == null)
            {
                Message = "无测量数据";
                State = Failed;
                return;
            }

            int rate = _rate;
            int n = _sweepLen + _tailLen;
            int p = 1;
            while (p < n) p <<= 1;

            // 重建扫频（与 Generate 同公式）
            var sRe = new float[p];
            var sIm = new float[p];
            for (int i = 0; i < _sweepLen; i++)
            {
                double t = i / (double)rate;
                double ph = 2.0 * Math.PI * Calibration.F1 * _L * (Math.Exp(t / _L) - 1.0);
                sRe[i] = (float)(Amp * Math.Sin(ph));
            }
            Fft.Forward(sRe, sIm);

            float maxS2 = 0f;
            for (int k = 0; k < p; k++)
            {
                float s2 = sRe[k] * sRe[k] + sIm[k] * sIm[k];
                if (s2 > maxS2) maxS2 = s2;
            }
            float thr = maxS2 * 1e-6f;

            BuildGrid();
            AnalyzeOne(l0, r0, sRe, sIm, thr, p, rate, MeasSrcL);
            if (runId != _runId) return;
            AnalyzeOne(l1, r1, sRe, sIm, thr, p, rate, MeasSrcR);
            if (runId != _runId) return;

            _capL0 = _capR0 = _capL1 = _capR1 = null;
            DoneAtTicks = DateTime.Now.Ticks;
            HasCurve = true;
            DesignDirty = true;
            if (runId == _runId) State = Ready;
        }

        /// <summary>一只箱的两次捕获（两耳）→ 每耳 H → 栅格 dB → 取 dB 平均。</summary>
        private void AnalyzeOne(float[] yL, float[] yR,
            float[] sRe, float[] sIm, float thr, int p, int rate, float[] dst)
        {
            var hLRe = new float[p]; var hLIm = new float[p];
            var hRRe = new float[p]; var hRIm = new float[p];
            Calibration.AccumulateOne(yL, sRe, sIm, thr, hLRe, hLIm, p);
            Calibration.AccumulateOne(yR, sRe, sIm, thr, hRRe, hRIm, p);
            var gL = new float[dst.Length];
            var gR = new float[dst.Length];
            Calibration.ToGrid(hLRe, hLIm, sRe, sIm, thr, p, rate, GridHz, gL);
            Calibration.ToGrid(hRRe, hRIm, sRe, sIm, thr, p, rate, GridHz, gR);
            for (int i = 0; i < dst.Length; i++)
                dst[i] = 0.5f * (gL[i] + gR[i]); // 单源两耳 → dB 平均
        }

        private void BuildGrid()
        {
            double ratio = Math.Log(MeasF2 / MeasF1);
            for (int i = 0; i < GridN; i++)
                GridHz[i] = (float)(MeasF1 * Math.Exp(ratio * i / (GridN - 1)));
        }

        // ── 设计（UI 线程）──
        public void Redesign()
        {
            if (!HasCurve) return;
            float smooth = SmoothOct;
            float maxBoost = MaxBoostDb;

            double spanOct = Math.Log(GridHz[GridN - 1] / GridHz[0], 2.0);
            float gridOct = (float)(spanOct / (GridN - 1));
            EqDesign.SmoothInto(MeasSrcL, _smL, smooth, gridOct);
            EqDesign.SmoothInto(MeasSrcR, _smR, smooth, gridOct);

            float refL = EqDesign.MidbandMean(GridHz, _smL);
            float refR = EqDesign.MidbandMean(GridHz, _smR);
            for (int i = 0; i < GridN; i++)
            {
                DispSrcL[i] = _smL[i] - refL;
                DispSrcR[i] = _smR[i] - refR;
                CorrSrcL[i] = EqDesign.Clamp(-DispSrcL[i], -30f, maxBoost);
                CorrSrcR[i] = EqDesign.Clamp(-DispSrcR[i], -30f, maxBoost);
            }

            int n = (int)Math.Round(spanOct / (1.0 / 3.0)) + 1;
            if (n < 8) n = 8;
            if (n > MaxBands) n = MaxBands;
            float spacing = (float)(spanOct / (n - 1));
            float q = (float)(1.0 / (2.0 * Math.Sinh(0.5 * Math.Log(2.0) * spacing)));
            for (int b = 0; b < n; b++)
            {
                _bandHz[b] = (float)(GridHz[0] * Math.Pow(2.0, spacing * b));
                _gL[b] = EqDesign.InterpAt(GridHz, CorrSrcL, _bandHz[b]);
                _gR[b] = EqDesign.InterpAt(GridHz, CorrSrcR, _bandHz[b]);
            }
            _eq.ApplyDesign(_bandHz, _gL, _gR, n, q, (int)MeasRate);

            for (int i = 0; i < GridN; i++)
            {
                float sumL = 0f, sumR = 0f;
                for (int b = 0; b < n; b++)
                {
                    sumL += _eq.MagDb(0, b, GridHz[i]);
                    sumR += _eq.MagDb(1, b, GridHz[i]);
                }
                PredSrcL[i] = DispSrcL[i] + sumL;
                PredSrcR[i] = DispSrcR[i] + sumR;
            }
        }

        /// <summary>设置恢复：载入上次测量曲线。</summary>
        public bool LoadMeasured(float[] grid, float[] mL, float[] mR, float f1, float f2, long ticks)
        {
            if (grid == null || mL == null || mR == null || grid.Length != GridN) return false;
            Array.Copy(grid, GridHz, GridN);
            Array.Copy(mL, MeasSrcL, GridN);
            Array.Copy(mR, MeasSrcR, GridN);
            MeasF1 = f1;
            MeasF2 = f2;
            DoneAtTicks = ticks;
            HasCurve = true;
            DesignDirty = true;
            return true;
        }

        /// <summary>源端补偿的合成幅响（输出曲线合成用）。未校准或关 = 0。</summary>
        public float EqMagDb(float freq)
        {
            if (!HasCurve || !Enabled) return 0f;
            return _eq.MagSumDb(0, freq);
        }
    }
}
