// 注入组件：把 Unity 音频线程的 OnAudioFilterRead 桥接到捕获环。
// 每个 AudioSource（L/R）挂一个，Channel 标识取哪条环。
using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace VirtualStereo
{
    public class VirtualStereoChannel : MonoBehaviour
    {
        public VirtualStereoChannel(IntPtr ptr) : base(ptr) { }

        public int Channel;

        public void OnAudioFilterRead(Il2CppStructArray<float> data, int channels)
        {
            AudioEngine.OnFilterRead(Channel, data, channels);
        }
    }
}
