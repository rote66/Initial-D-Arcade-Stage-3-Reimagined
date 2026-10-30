using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Networking;

public sealed class Idas3StoryVoices : MonoBehaviour
{
    [Serializable] public sealed class Clip {
        public string id,file,sourceSha256;
        public float seconds;
        public int[] glyphs,englishGlyphs;
    }
    [Serializable] public sealed class Bank { public int id;public Clip[] clips;public Idas3StoryVoiceLogic.Rule[] rules; }
    [Serializable] public sealed class Catalog { public int version,atlasPages;public float[] advances;public Bank[] banks; }
    [DllImport(Idas3Native.Library,CallingConvention=CallingConvention.Cdecl)]
    internal static extern int Idas3SceneGetStoryVoiceState(ref Idas3StoryVoiceLogic.State state);
    [DllImport(Idas3Native.Library,CallingConvention=CallingConvention.Cdecl)]
    private static extern int Idas3SceneCopyStoryVoiceFrames([Out] Idas3StoryVoiceLogic.State[] states,int capacity);
    private readonly Idas3StoryVoiceLogic logic=new Idas3StoryVoiceLogic();
    private readonly Dictionary<string,AudioClip> cache=new Dictionary<string,AudioClip>();
    private readonly Idas3StoryVoiceLogic.State[] frames=new Idas3StoryVoiceLogic.State[32];
    private Idas3SceneGame host;
    private Idas3GameOptions options;
    private string root;
    private Catalog catalog;
    private Texture2D atlas;
    private AudioSource source;
    private Idas3StoryVoiceGain gain;
    private UnityWebRequest request,preloadRequest;
    private Coroutine loading,preloading;
    private Bank bank;
    private Clip current;
    private uint generation;
    private bool paused,attempted,wasEnabled;
    private float subtitleElapsed;
    private ulong lastTick=ulong.MaxValue;
    private bool initialized,bankPreloaded;
    private Idas3StoryVoiceLogic.State previous;
    internal Catalog Media=>catalog;
    internal Clip Current=>current;
    internal string LastError {get;private set;}
    internal int PlayedCount {get;private set;}
    internal bool Loading=>loading!=null;
    internal bool IsPaused=>paused;
    internal bool ManualPresentation;
    internal int ActiveEvent=>logic.Active?.@event??0;
    internal ulong LastScheduledTick {get;private set;}
    internal uint LastRequestClock {get;private set;}
    internal int ScheduledFrames {get;private set;}

    public void Initialize(Idas3SceneGame owner,Idas3GameOptions settings,string assets){
        host=owner;options=settings;root=Path.Combine(assets,"data/story_voices");
        // Keep the native stream's OnAudioFilterRead off this separate source.
        var speech=new GameObject("Street Stage Speech");speech.transform.SetParent(transform,false);
        source=speech.AddComponent<AudioSource>();source.playOnAwake=false;source.loop=false;
        source.spatialBlend=0;source.priority=16;source.ignoreListenerPause=true;
        gain=speech.AddComponent<Idas3StoryVoiceGain>();
    }
    internal bool LoadMedia(){
        if(attempted)return catalog!=null;
        attempted=true;
        // Older data packs remain playable with the optional speech disabled.
        if(!File.Exists(Path.Combine(root,"voices.json"))){LastError="Story voice data is not installed.";return false;}
        try{
            var loaded=JsonUtility.FromJson<Catalog>(File.ReadAllText(Path.Combine(root,"voices.json")));
            if(loaded==null||loaded.version!=2||loaded.banks==null||loaded.banks.Length!=30||loaded.atlasPages<=0||
                loaded.advances==null||loaded.advances.Length==0||loaded.advances.Length>loaded.atlasPages*256)
                throw new InvalidDataException("Invalid story voice catalog.");
            var texture=new Texture2D(2,2,TextureFormat.RGBA32,false);
            if(!texture.LoadImage(File.ReadAllBytes(Path.Combine(root,"subtitles.png")))){Destroy(texture);throw new InvalidDataException("Invalid subtitle atlas.");}
            texture.filterMode=FilterMode.Bilinear;texture.wrapMode=TextureWrapMode.Clamp;
            atlas=texture;catalog=loaded;return true;
        }catch(Exception error){LastError=error.Message;Debug.LogWarning("Story voices: "+LastError);return false;}
    }
    private void Update(){
        if(ManualPresentation)return;
        if(host==null||!host.Ready){StopPlayback();return;}
        var state=new Idas3StoryVoiceLogic.State{size=64};
        if(Idas3SceneGetStoryVoiceState(ref state)!=1){StopPlayback();return;}
        int count=Idas3SceneCopyStoryVoiceFrames(frames,frames.Length);
        if(count<0){LastError="Invalid speech frame buffer.";StopPlayback();return;}
        Present(state,Application.isFocused,frames,count);
    }
    internal void Present(Idas3StoryVoiceLogic.State state,bool focused)=>Present(state,focused,null,0);
    private void Present(Idas3StoryVoiceLogic.State state,bool focused,Idas3StoryVoiceLogic.State[] samples,int count){
        bool hold=state.Paused||host.PauseMenu.IsOpen||(!focused&&options.Current.muteWhenUnfocused);
        if(hold)state.flags|=2u;
        bool enabled=options.Current.storyVoices;
        bool newRace=!initialized||state.raceId!=previous.raceId||state.enemy!=previous.enemy;
        if(!state.Eligible||newRace||enabled!=wasEnabled)StopPlayback();
        previous=state;wasEnabled=enabled;
        if(!state.Eligible){initialized=false;return;}
        if(!LoadMedia())return;
        if(newRace){
            var next=Array.Find(catalog.banks,b=>b.id==Idas3StoryVoiceLogic.Bank(state.enemy));
            if(next==null)return;
            if(bank!=next){ClearBank();bank=next;}
            logic.Reset(bank.rules,unchecked((ulong)(uint)Environment.TickCount));
            lastTick=ulong.MaxValue;initialized=true;
        }
        if(enabled&&!bankPreloaded&&preloading==null)preloading=StartCoroutine(PreloadBank(bank));
        if(hold!=paused){paused=hold;if(paused)source.Pause();else source.UnPause();}
        gain.Gain=options.Current.masterVolume*options.Current.effectsVolume;
        if(paused)return;
        if(samples==null)Schedule(state,enabled);
        else for(int i=0;i<count;++i)
            if(samples[i].raceId==state.raceId&&samples[i].enemy==state.enemy)Schedule(samples[i],enabled);
        if(current!=null){
            subtitleElapsed+=Time.unscaledDeltaTime;
            if(!source.isPlaying&&loading==null){current=null;source.clip=null;}
        }
    }
    private void Schedule(Idas3StoryVoiceLogic.State state,bool enabled){
        if(!state.Eligible||state.Paused||(state.phase!=2&&state.phase!=3)||state.ticks%2!=0||lastTick==state.ticks)return;
        lastTick=state.ticks;LastScheduledTick=lastTick;
        ++ScheduledFrames;
        var rule=logic.Tick(new Idas3StoryVoiceLogic.Input{
            sector=(int)state.sector,clockFrame=(int)state.clockFrame,progress=state.progress,
            speedKmh=state.speedKmh,gap=state.advantage,playerAhead=state.playerAhead!=0,
            playing=loading!=null||source.isPlaying});
        if(rule!=null&&enabled){
            LastRequestClock=state.clockFrame;
            var clip=Array.Find(bank.clips,c=>c.id==rule.clip);
            if(clip==null){LastError="Missing original speech rule clip: "+rule.clip;return;}
            StopPlayback();
            if(cache.TryGetValue(clip.file,out var decoded))StartClip(clip,decoded);
            else loading=StartCoroutine(LoadClip(clip,++generation));
        }
    }
    private void StartClip(Clip clip,AudioClip audio){
        source.clip=audio;current=clip;subtitleElapsed=0;source.Play();if(paused)source.Pause();++PlayedCount;
    }
    private IEnumerator PreloadBank(Bank target){
        yield return null;
        var ordered=new List<Clip>();
        // Fetch authored opening lines before the remaining bank during showcase.
        foreach(var rule in target.rules)if(rule.@event==1||rule.@event==12){
            var clip=Array.Find(target.clips,c=>c.id==rule.clip);if(clip!=null&&!ordered.Contains(clip))ordered.Add(clip);
        }
        foreach(var clip in target.clips)if(!ordered.Contains(clip))ordered.Add(clip);
        foreach(var clip in ordered){
            if(cache.ContainsKey(clip.file))continue;
            string file=Path.Combine(root,clip.file);
            if(Path.GetFileName(clip.file)!=clip.file||!File.Exists(file)){LastError="Missing story voice: "+clip.id;break;}
            preloadRequest=UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri,AudioType.MPEG);
            yield return preloadRequest.SendWebRequest();
            if(preloadRequest.result==UnityWebRequest.Result.Success){
                var decoded=DownloadHandlerAudioClip.GetContent(preloadRequest);
                if(decoded!=null){if(cache.ContainsKey(clip.file))Destroy(decoded);else cache.Add(clip.file,decoded);}
            }else LastError=preloadRequest.error;
            preloadRequest.Dispose();preloadRequest=null;
        }
        bankPreloaded=true;preloading=null;
    }
    private IEnumerator LoadClip(Clip clip,uint token){
        yield return null;
        string file=Path.Combine(root,clip.file);
        if(Path.GetFileName(clip.file)!=clip.file||!File.Exists(file)){LastError="Missing story voice: "+clip.id;loading=null;yield break;}
        request=UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri,AudioType.MPEG);
        yield return request.SendWebRequest();
        if(token!=generation)yield break;
        if(request.result!=UnityWebRequest.Result.Success){LastError=request.error;request.Dispose();request=null;loading=null;yield break;}
        var audio=DownloadHandlerAudioClip.GetContent(request);request.Dispose();request=null;loading=null;
        if(audio==null){LastError="Unable to decode story voice: "+clip.id;yield break;}
        if(cache.TryGetValue(clip.file,out var existing)){Destroy(audio);audio=existing;}else cache.Add(clip.file,audio);
        StartClip(clip,audio);
    }
    public void StopPlayback(){
        ++generation;
        if(loading!=null){StopCoroutine(loading);loading=null;}
        if(request!=null){request.Abort();request.Dispose();request=null;}
        if(source!=null){source.Stop();source.clip=null;}
        current=null;paused=false;
    }
    private void ClearBank(){
        if(preloading!=null){StopCoroutine(preloading);preloading=null;}
        if(preloadRequest!=null){preloadRequest.Abort();preloadRequest.Dispose();preloadRequest=null;}
        foreach(var clip in cache.Values)Destroy(clip);cache.Clear();
        bankPreloaded=false;
    }
    private void OnApplicationPause(bool value){if(value){source?.Pause();paused=true;}}
    private void OnGUI(){
        if(Event.current.type!=EventType.Repaint||current==null||paused||atlas==null||
            !options.Current.storyVoices||!options.Current.storyVoiceSubtitles||subtitleElapsed>current.seconds)return;
        var safe=Screen.safeArea;safe.y=Screen.height-safe.yMax;
        DrawSubtitle(options.Current.storyVoiceSubtitleLanguage==1?current.englishGlyphs:current.glyphs,safe);
    }
    internal static Rect SubtitleBounds(int[] glyphs,Rect viewport,float[] advances=null){
        float columns=0,longest=0;int rows=1;
        foreach(int glyph in glyphs){
            if(glyph==-1){longest=Mathf.Max(longest,columns);columns=0;++rows;}
            else columns+=glyph==-2?.5f:advances==null?1:advances[glyph];
        }
        longest=Mathf.Max(longest,columns);
        // Street Stage placement: lower screen, white type and a one-pixel shadow.
        float cell=12*viewport.height/272f,width=longest*cell;
        return new Rect(viewport.center.x-width/2,viewport.y+180*viewport.height/272f,width,rows*cell);
    }
    internal void DrawSubtitle(int[] glyphs,Rect viewport){
        Rect bounds=SubtitleBounds(glyphs,viewport,catalog.advances);
        float cell=12*viewport.height/272f,shadow=viewport.height/272f;
        Color color=GUI.color;int depth=GUI.depth;GUI.depth=-15;
        for(int pass=0;pass<2;++pass){
            float offset=pass==0?shadow:0,x=bounds.x+offset,y=bounds.y+offset;
            GUI.color=pass==0?Color.black:Color.white;
            foreach(int glyph in glyphs){
                if(glyph==-1){x=bounds.x+offset;y+=cell;continue;}
                if(glyph==-2){x+=cell/2;continue;}
                float u=(glyph%16)/16f,v=1-(glyph/16+1)/(16f*catalog.atlasPages);
                GUI.DrawTextureWithTexCoords(new Rect(x,y,cell,cell),atlas,
                    new Rect(u,v,1/16f,1/(16f*catalog.atlasPages)));
                x+=cell*catalog.advances[glyph];
            }
        }
        GUI.color=color;GUI.depth=depth;
    }
    private void OnDestroy(){StopPlayback();ClearBank();if(atlas!=null)Destroy(atlas);if(source!=null)Destroy(source.gameObject);}
}
