// 人头两耳信号监视与录制。
// tap 点 = 空间模拟之后、后置增益之前——即虚拟人头两只耳朵实际收到的信号
// （双耳HRTF/ITD+ILD 模式下就是各耳的卷积/合成结果）。
// 电平统计由音频线程 Push 更新；WAV 录制写 32bit float（IEEE），可选 左耳/右耳/双声道。
using System;
using System.IO;
using System.Threading;

namespace VirtualStereo.Desktop
{
    internal sealed class EarMonitor
    {
        public enum RecMode { Left = 0, Right = 1, Stereo = 2 }

        // 电平（音频线程写 / UI 读）
        public volatile float RmsL, RmsR, PeakL, PeakR;

        // 录制（UI 线程启停 / 音频线程写样本）
        public volatile int Mode = (int)RecMode.Stereo;
        public volatile bool Recording;
        public volatile float RecSeconds;
        public string LastError;

        private FileStream _stream;
        private readonly object _lock = new object();
        private long _dataBytes;
        private long _samples;      // 每声道样本数
        private int _channels = 2;
        private int _rate = 48000;
        private float[] _scratch = new float[8192];

        /// <summary>音频线程调用：interleaved 交错立体声（[0]=左耳/左声道）。</summary>
        public void Push(float[] interleaved, int frames, int sampleRate)
        {
            float sumL = 0f, sumR = 0f;
            float pkL = PeakL, pkR = PeakR;
            for (int i = 0; i < frames; i++)
            {
                float l = interleaved[i * 2], r = interleaved[i * 2 + 1];
                sumL += l * l;
                sumR += r * r;
                float al = l < 0f ? -l : l;
                float ar = r < 0f ? -r : r;
                if (al > pkL) pkL = al;
                if (ar > pkR) pkR = ar;
            }
            RmsL = (float)Math.Sqrt(sumL / Math.Max(1, frames));
            RmsR = (float)Math.Sqrt(sumR / Math.Max(1, frames));
            PeakL = pkL * 0.985f; // 峰值保持带缓降
            PeakR = pkR * 0.985f;

            if (!Recording) return;
            lock (_lock)
            {
                if (_stream == null) return;
                try
                {
                    int count = _channels == 1 ? frames : frames * 2;
                    if (_scratch.Length < count) _scratch = new float[count];
                    if (_channels == 1)
                    {
                        bool left = Mode == (int)RecMode.Left;
                        for (int i = 0; i < frames; i++)
                            _scratch[i] = left ? interleaved[i * 2] : interleaved[i * 2 + 1];
                    }
                    else
                    {
                        Array.Copy(interleaved, _scratch, frames * 2);
                    }
                    int bytes = count * 4;
                    var buf = new byte[bytes]; // v1: 每块一次分配；量小可接受
                    Buffer.BlockCopy(_scratch, 0, buf, 0, bytes);
                    _stream.Write(buf, 0, bytes);
                    _dataBytes += bytes;
                    _samples += frames;
                    RecSeconds = _samples / (float)_rate;
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                    Recording = false;
                }
            }
        }

        /// <summary>开始录制。成功返回文件路径；失败返回 null（看 LastError）。</summary>
        public string StartRecording(RecMode mode, int sampleRate, string folder)
        {
            lock (_lock)
            {
                if (Recording) return null;
                try
                {
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder,
                        "ear_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav");
                    _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 65536);
                    _channels = mode == RecMode.Stereo ? 2 : 1;
                    _rate = sampleRate > 0 ? sampleRate : 48000;
                    Mode = (int)mode;
                    _dataBytes = 0;
                    _samples = 0;
                    RecSeconds = 0f;
                    WriteHeader();
                    Recording = true;
                    LastError = null;
                    return path;
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                    try { _stream?.Dispose(); } catch { }
                    _stream = null;
                    return null;
                }
            }
        }

        public void StopRecording()
        {
            lock (_lock)
            {
                if (_stream == null) return;
                try
                {
                    // 回填 RIFF/fact/data 尺寸
                    long total = _stream.Length;
                    _stream.Seek(4, SeekOrigin.Begin);
                    WriteU32((uint)(total - 8));
                    _stream.Seek(44, SeekOrigin.Begin);
                    WriteU32((uint)_samples);
                    _stream.Seek(52, SeekOrigin.Begin);
                    WriteU32((uint)_dataBytes);
                }
                catch { }
                try { _stream.Dispose(); } catch { }
                _stream = null;
                Recording = false;
            }
        }

        private void WriteHeader()
        {
            // RIFF/WAVE + fmt(IEEE float) + fact + data（尺寸关闭时回填）
            WriteTag("RIFF");
            WriteU32(0);              // +4  riffSize
            WriteTag("WAVE");
            WriteTag("fmt ");
            WriteU32(16);             // fmt 块大小
            WriteU16(3);              // WAVE_FORMAT_IEEE_FLOAT
            WriteU16((ushort)_channels);
            WriteU32((uint)_rate);
            WriteU32((uint)(_rate * _channels * 4));
            WriteU16((ushort)(_channels * 4));
            WriteU16(32);             // bits
            WriteTag("fact");
            WriteU32(4);
            WriteU32(0);              // +44 sampleLength
            WriteTag("data");
            WriteU32(0);              // +52 dataSize
        }

        private void WriteTag(string tag)
        {
            var b = new byte[4];
            for (int i = 0; i < 4; i++) b[i] = (byte)tag[i];
            _stream.Write(b, 0, 4);
        }

        private void WriteU32(uint v)
        {
            var b = BitConverter.GetBytes(v);
            _stream.Write(b, 0, 4);
        }

        private void WriteU16(ushort v)
        {
            var b = BitConverter.GetBytes(v);
            _stream.Write(b, 0, 2);
        }
    }
}
