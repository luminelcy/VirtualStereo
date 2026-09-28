// 单生产者单消费者 float 环形缓冲（捕获线程写、音频线程读）。
// 索引用 Volatile 发布；容量取 2 的幂以便用掩码取模。
using System;
using System.Threading;

namespace VirtualStereo.Capture
{
    public sealed class SampleRing
    {
        private readonly float[] _buf;
        private readonly int _mask;
        private long _write; // 捕获线程独占
        private long _read;  // 消费线程独占

        public SampleRing(int capacityPow2)
        {
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0)
                throw new ArgumentException("容量必须是 2 的幂");
            _buf = new float[capacityPow2];
            _mask = capacityPow2 - 1;
        }

        public int Capacity => _buf.Length;

        /// <summary>可读样本数（近似值，仅用于统计/控制）。</summary>
        public int Available
        {
            get
            {
                long w = Volatile.Read(ref _write);
                long r = Volatile.Read(ref _read);
                long avail = w - r;
                return avail < 0 ? 0 : (int)Math.Min(avail, _buf.Length);
            }
        }

        /// <summary>捕获线程调用：写满 count 个样本，写不下就丢最旧的。</summary>
        public void Write(float[] src, int srcOffset, int count)
        {
            long w = _write;
            long r = Volatile.Read(ref _read);
            if (w - r + count > _buf.Length)
            {
                // 溢出：把读指针推到只保留最新 Capacity-count 个样本的位置
                Volatile.Write(ref _read, w + count - _buf.Length);
            }
            for (int i = 0; i < count; i++)
                _buf[(int)((w + i) & _mask)] = src[srcOffset + i];
            Volatile.Write(ref _write, w + count);
        }

        /// <summary>消费线程调用：读 count 个样本；不足时尾部补零，返回实际读到的样本数。</summary>
        public int Read(float[] dst, int count)
        {
            long r = _read;
            long w = Volatile.Read(ref _write);
            long avail = w - r;
            int n = (int)Math.Min(count, Math.Max(0, Math.Min(avail, _buf.Length)));
            for (int i = 0; i < n; i++)
                dst[i] = _buf[(int)((r + i) & _mask)];
            for (int i = n; i < count; i++)
                dst[i] = 0f;
            Volatile.Write(ref _read, r + n);
            return n;
        }

        /// <summary>消费侧丢弃最旧的 n 个样本（延迟纠正：捕获时钟快于播放时钟时把环砍回目标电平）。</summary>
        public void Discard(int n)
        {
            if (n <= 0) return;
            long r = _read;
            long w = Volatile.Read(ref _write);
            long avail = w - r;
            if (avail <= 0) return;
            if (n > avail) n = (int)avail;
            Volatile.Write(ref _read, r + n);
        }

        /// <summary>丢弃缓冲内容。</summary>
        public void Clear()
        {
            long w = Volatile.Read(ref _write);
            Volatile.Write(ref _read, w);
        }
    }
}
