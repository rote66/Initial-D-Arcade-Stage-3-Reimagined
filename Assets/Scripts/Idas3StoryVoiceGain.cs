using UnityEngine;

// AudioSource.volume stops at 100%; the shared options allow up to 200% per bus.
public sealed class Idas3StoryVoiceGain : MonoBehaviour
{
    public volatile float Gain=1;
    private void OnAudioFilterRead(float[] data,int channels){
        float gain=Gain;
        for(int i=0;i<data.Length;++i){
            float sample=data[i]*gain;
            data[i]=sample< -1?-1:sample>1?1:sample;
        }
    }
}
