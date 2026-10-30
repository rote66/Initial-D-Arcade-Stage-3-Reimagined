using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Networking;

// Opt-in private-save test: real countdown telemetry plus controlled presentation events.
public sealed class Idas3StoryVoiceSmoke : MonoBehaviour
{
    [DllImport(Idas3Native.Library,CallingConvention=CallingConvention.Cdecl)] private static extern int Idas3SceneModeFlowFixture(int scene);
    [DllImport(Idas3Native.Library,CallingConvention=CallingConvention.Cdecl)] private static extern int Idas3SceneSetPaused(int paused);
    [Serializable] private sealed class Report {
        public string schema="idas3-story-voices-smoke-v3",error;
        public string scope="Private-save Simulator: native Legend countdown/running/30Hz input/pause, PSP frame-90 start, snow and time-attack exclusion, all 30 banks/1331 subtitle links, decoded PCM, production voice player and options draft. Subtitle pixel checks cover PSP lower placement, white type/shadow and transparent background at three aspect ratios. Scheduler separately compared with isolated original PSP CPU. Not a physical speaker or full PSP hardware timing test.";
        public bool passed,shutdownComplete;
        public int checks,decodedBanks,clips;
    }
    private static string pendingRoot;
    private static Idas3StoryVoiceSmoke active;
    private Idas3SceneGame host;
    private Idas3StoryVoices voices;
    private string root;
    private int checks,decodedBanks,clips;
    private double frameSeconds=1.0/60;
    private bool finished,presentationRequested,presentationVerified;
    public static bool Configure(ref string saves){
        var args=Idas3LaunchArguments.GetArguments();int at=Array.IndexOf(args,"-idas3-story-voice-smoke");if(at<0)return false;
        if(at+1>=args.Length)throw new ArgumentException("Story voice test requires a new private output directory.");
        pendingRoot=Path.GetFullPath(args[at+1]);if(Directory.Exists(pendingRoot))throw new IOException("Test directory already exists.");
        Directory.CreateDirectory(pendingRoot);saves=Path.Combine(pendingRoot,"userdata");Directory.CreateDirectory(saves);
        File.WriteAllText(Path.Combine(pendingRoot,"ISOLATED_MODE_FLOW_TEST.txt"),"Private speech fixtures; no user profiles.\n");
        File.WriteAllText(Path.Combine(saves,"settings.txt"),"0 0 0 0 0 1 1 0\n");
        File.WriteAllText(Path.Combine(saves,"native_selection.txt"),"0 0\n");
        Screen.SetResolution(1280,720,FullScreenMode.Windowed);return true;
    }
    public static void Attach(Idas3SceneGame game){
        if(pendingRoot==null)return;
        active=game.gameObject.AddComponent<Idas3StoryVoiceSmoke>();active.host=game;active.root=pendingRoot;
        game.DiagnosticFocusOverride=true;active.voices=game.GetComponent<Idas3StoryVoices>();
        active.StartCoroutine(active.Guard(active.Run()));
    }
    internal static bool PrepareFrame(ref Idas3Native.FrameInput frame){
        if(active==null)return true;if(active.finished)return false;
        frame=new Idas3Native.FrameInput{size=(uint)Marshal.SizeOf<Idas3Native.FrameInput>(),flags=1,deltaSeconds=active.frameSeconds};
        return true;
    }
    private void Check(bool value,string name){++checks;if(!value)throw new InvalidOperationException(name+"; "+voices.LastError);}
    private IEnumerator Frames(int n){for(int i=0;i<n;++i)yield return null;}
    private Idas3StoryVoiceLogic.State Read(){
        var state=new Idas3StoryVoiceLogic.State{size=64};
        Check(Idas3StoryVoices.Idas3SceneGetStoryVoiceState(ref state)==1&&state.version==2,"Read production speech telemetry");return state;
    }
    private IEnumerator Guard(IEnumerator routine){
        var stack=new Stack<IEnumerator>();stack.Push(routine);
        while(stack.Count>0&&!finished){
            object value=null;Exception error=null;
            try{if(!stack.Peek().MoveNext()){stack.Pop();continue;}value=stack.Peek().Current;}catch(Exception e){error=e;}
            if(error!=null){Finish(false,error.ToString());yield break;}
            if(value is IEnumerator child)stack.Push(child);else yield return value;
        }
    }
    private IEnumerator Capture(string name){
        yield return new WaitForEndOfFrame();
        var image=ScreenCapture.CaptureScreenshotAsTexture();
        File.WriteAllBytes(Path.Combine(root,name+".png"),image.EncodeToPNG());Destroy(image);
    }
    private void OnGUI(){
        if(!presentationRequested||Event.current.type!=EventType.Repaint)return;
        presentationRequested=false;
        try{VerifySubtitlePresentation();presentationVerified=true;}
        catch(Exception error){Finish(false,error.ToString());}
    }
    private void VerifySubtitlePresentation(){
        var prior=RenderTexture.active;var matrix=GUI.matrix;var color=GUI.color;
        int glyph=Array.FindIndex(voices.Media.advances,a=>Mathf.Abs(a-1)<.001f);
        var text=new[]{glyph,glyph,glyph,glyph,glyph,-1,glyph};
        GL.PushMatrix();
        try{
            foreach(var size in new[]{new Vector2Int(1280,720),new Vector2Int(1024,768),new Vector2Int(1440,660)}){
                var viewport=new Rect(0,0,size.x,size.y);
                Rect bounds=Idas3StoryVoices.SubtitleBounds(text,viewport,voices.Media.advances);
                float scale=size.y/272f;
                Check(Mathf.Abs(bounds.y-180*scale)<.01f,"PSP subtitle lower anchor");
                Check(Mathf.Abs(bounds.center.x-viewport.center.x)<.01f,"Longest subtitle line centers the block");
                Check(Mathf.Abs(bounds.width-60*scale)<.01f&&Mathf.Abs(bounds.height-24*scale)<.01f,"PSP subtitle type and line spacing");
                foreach(var bank in voices.Media.banks)foreach(var clip in bank.clips){
                    foreach(var translated in new[]{clip.glyphs,clip.englishGlyphs}){
                        Rect actual=Idas3StoryVoices.SubtitleBounds(translated,viewport,voices.Media.advances);
                        Check(actual.x>=0&&actual.xMax+scale<=size.x&&actual.yMax+scale<=size.y,"Bilingual subtitle fits landscape viewport");
                    }
                }
                var target=new RenderTexture(size.x,size.y,0,RenderTextureFormat.ARGB32);
                var image=new Texture2D(size.x,size.y,TextureFormat.RGB24,false);
                try{
                    Check(target.Create(),"Subtitle pixel target");
                    RenderTexture.active=target;GL.LoadPixelMatrix(0,size.x,size.y,0);
                    GL.Clear(false,true,new Color(.12f,.5f,.28f));GUI.matrix=Matrix4x4.identity;GUI.color=Color.white;
                    voices.DrawSubtitle(text,viewport);
                    image.ReadPixels(viewport,0,0);image.Apply();
                    var pixels=image.GetPixels32();Color32 background=pixels[0];
                    int outside=0,white=0,black=0,secondLine=0;
                    var ink=new Rect(bounds.x-1,bounds.y-1,bounds.width+scale+2,bounds.height+scale+2);
                    for(int y=0;y<size.y;++y)for(int x=0;x<size.x;++x){
                        Color32 pixel=pixels[(size.y-1-y)*size.x+x];
                        bool changed=Math.Abs(pixel.r-background.r)>3||Math.Abs(pixel.g-background.g)>3||Math.Abs(pixel.b-background.b)>3;
                        if(!changed)continue;
                        if(!ink.Contains(new Vector2(x,y)))++outside;
                        if(pixel.r>180&&pixel.g>180&&pixel.b>180)++white;
                        if(pixel.r<20&&pixel.g<20&&pixel.b<20)++black;
                        if(y>=bounds.y+12*scale&&x<bounds.x+12*scale)++secondLine;
                    }
                    Check(outside==0,"No subtitle background bar or displaced ink");
                    Check(white>20&&black>5,"White subtitle glyphs retain black drop shadow");
                    Check(secondLine>10,"Short subtitle row shares the longest row's left edge");
                    File.WriteAllBytes(Path.Combine(root,"subtitle-layout-"+size.x+"x"+size.y+".png"),image.EncodeToPNG());
                    Idas3StoryVoices.Clip longest=null;
                    foreach(var bank in voices.Media.banks)foreach(var clip in bank.clips)
                        if(longest==null||clip.englishGlyphs.Length>longest.englishGlyphs.Length)longest=clip;
                    GL.Clear(false,true,new Color(.12f,.5f,.28f));
                    voices.DrawSubtitle(longest.englishGlyphs,viewport);
                    image.ReadPixels(viewport,0,0);image.Apply();
                    File.WriteAllBytes(Path.Combine(root,"subtitle-english-"+size.x+"x"+size.y+".png"),image.EncodeToPNG());
                }finally{RenderTexture.active=prior;Destroy(image);target.Release();Destroy(target);}
            }
            var safe=new Rect(64,8,1152,680);
            Rect inset=Idas3StoryVoices.SubtitleBounds(text,safe);
            Check(Mathf.Abs(inset.center.x-safe.center.x)<.01f&&Mathf.Abs(inset.y-(safe.y+180*safe.height/272))<.01f,"Safe-area offsets preserve PSP anchoring");
        }finally{GL.PopMatrix();GUI.matrix=matrix;GUI.color=color;RenderTexture.active=prior;}
    }
    private IEnumerator Run(){
        var options=host.GameOptions;
        Check(!options.Current.storyVoices&&options.Current.storyVoiceSubtitles&&options.Current.storyVoiceSubtitleLanguage==0,"Non-disruptive defaults");
        options.BeginEdit();options.Draft.storyVoices=true;options.Draft.storyVoiceSubtitles=false;options.Draft.storyVoiceSubtitleLanguage=1;
        Check(!options.Current.storyVoices&&options.HasUnsavedChanges,"Voice settings remain draft");
        options.BeginEdit();Check(!options.Draft.storyVoices&&options.Draft.storyVoiceSubtitles&&options.Draft.storyVoiceSubtitleLanguage==0,"Cancel discards voice and language draft");
        options.Draft.storyVoices=true;options.Draft.customMenuLanguage=2;
        Check(options.ApplyDraft(),"Apply voice settings");
        options.BeginEdit();options.ResetDraft();
        Check(!options.Draft.storyVoices&&options.Current.storyVoices,"Reset only changes voice draft");
        options.BeginEdit();
        host.PauseMenu.OpenAttractOptions();host.PauseMenu.SelectTab(0);
        yield return Frames(3);yield return Capture("audio-settings-parent-child");
        for(int i=0;i<8;++i)host.PauseMenu.Navigate(1);
        host.PauseMenu.Activate();
        Check(host.PauseMenu.SubtitleLanguageVisible,"Language box opens through production menu input");
        yield return Frames(2);yield return Capture("subtitle-language-dropdown");
        host.PauseMenu.Navigate(1);host.PauseMenu.Back();
        Check(!host.PauseMenu.SubtitleLanguageVisible&&options.Draft.storyVoiceSubtitleLanguage==0,"Back cancels language popup");
        host.PauseMenu.Activate();host.PauseMenu.Navigate(1);host.PauseMenu.Activate();
        Check(options.Draft.storyVoiceSubtitleLanguage==1&&options.Current.storyVoiceSubtitleLanguage==0&&options.HasUnsavedChanges,"Language choice stays in shared draft");
        host.PauseMenu.SetOpen(false);
        options.BeginEdit();Check(options.Draft.storyVoiceSubtitleLanguage==0,"Leaving settings discards language choice");
        options.Draft.customMenuLanguage=0;Check(options.ApplyDraft(),"English menu fixture");
        Screen.SetResolution(1024,768,FullScreenMode.Windowed);yield return Frames(4);
        host.PauseMenu.OpenAttractOptions();host.PauseMenu.SelectTab(0);
        for(int i=0;i<8;++i)host.PauseMenu.Navigate(1);
        host.PauseMenu.Activate();yield return Frames(2);yield return Capture("subtitle-language-english-menu-4x3");
        host.PauseMenu.SetOpen(false);
        Screen.SetResolution(1280,720,FullScreenMode.Windowed);yield return Frames(4);
        options.BeginEdit();options.Draft.customMenuLanguage=2;Check(options.ApplyDraft(),"Restore Chinese menu fixture");
        Check(voices.LoadMedia(),"Load imported speech");
        var media=voices.Media;Check(media.banks.Length==30,"30 PSP opponent banks");
        foreach(var bank in media.banks){
            Check(bank.clips.Length>0&&bank.rules.Length>0,"Non-empty bank "+bank.id);
            foreach(var clip in bank.clips){
                ++clips;Check(clip.glyphs.Length>0&&clip.englishGlyphs.Length>0&&clip.seconds>0,"Bilingual subtitle for "+clip.id);
                foreach(var translated in new[]{clip.glyphs,clip.englishGlyphs})
                    foreach(int glyph in translated)Check(glyph>=-2&&glyph<media.advances.Length,"Subtitle glyph range");
            }
            var first=bank.clips[0];
            string assets=Application.isEditor?Path.GetFullPath(Path.Combine(Application.dataPath,"../Native")):Path.Combine(Idas3AssetPaths.Root,"IDAS3");
            string file=Path.Combine(assets,"data/story_voices",first.file);
            using(var request=UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri,AudioType.MPEG)){
                yield return request.SendWebRequest();Check(request.result==UnityWebRequest.Result.Success,"Decode bank "+bank.id);
                var audio=DownloadHandlerAudioClip.GetContent(request);Check(audio!=null&&audio.samples>0,"PCM frames bank "+bank.id);
                var samples=new float[audio.samples*audio.channels];Check(audio.GetData(samples,0),"Read decoded PCM");
                bool signal=false;foreach(float sample in samples)if(Math.Abs(sample)>.001f){signal=true;break;}
                Check(signal,"Non-silent PCM bank "+bank.id);Destroy(audio);++decodedBanks;
            }
        }
        Check(clips==1331,"All source voice entries imported");
        presentationRequested=true;
        while(!presentationVerified&&!finished)yield return null;
        Check(Idas3SceneModeFlowFixture(440)==1,"Private Legend countdown fixture");
        yield return Frames(1);
        var countdown=Read();Check(countdown.Eligible&&countdown.enemy==0&&countdown.phase==2,"Real countdown telemetry");
        Check(countdown.clockFrame==0&&voices.Current==null,"Countdown does not start PSP race clock or speech");
        int scheduled=voices.ScheduledFrames;
        ulong owner=countdown.ticks;frameSeconds=.1;yield return Frames(1);frameSeconds=1.0/60;
        var caughtUp=Read();
        Check(caughtUp.ticks>=owner+4&&voices.ScheduledFrames-scheduled==(int)(caughtUp.ticks/2-owner/2),
            "One render catch-up retains every original-rate speech input");
        for(int i=0;i<450&&voices.Current==null;++i)yield return null;
        Check(voices.Current!=null&&voices.PlayedCount>0,"Actual race starts production voice player");
        Check(voices.LastRequestClock==90&&Read().phase==3,"Original frame-90 opening after GO");
        Check(voices.ScheduledFrames>scheduled&&voices.LastScheduledTick%2==0,"Native 60Hz owner feeds PSP 30Hz scheduler");
        yield return Capture("race-start-voice-subtitle");
        Check(Idas3SceneSetPaused(1)==1,"Pause actual race");yield return Frames(2);
        Check(Read().Paused&&voices.IsPaused,"Pause holds voice and subtitle");
        Check(Idas3SceneSetPaused(0)==1,"Resume actual race");yield return Frames(2);
        host.PauseMenu.SetOpen(false);yield return Frames(2);
        Check(!voices.IsPaused,"Resume restores voice player");
        for(int i=0;i<300&&Read().phase!=3;++i)yield return null;
        Check(Read().phase==3,"Real countdown reaches Running");
        Check(Idas3SceneModeFlowFixture(441)==1,"Private snow fixture");yield return Frames(2);
        Check(!Read().Eligible&&voices.Current==null,"Snow does not borrow a bank");
        Check(Idas3SceneModeFlowFixture(428)==1,"Private time attack fixture");yield return Frames(2);
        Check(!Read().Eligible&&voices.Current==null,"Time attack stays silent");
        host.PauseMenu.SetOpen(false);voices.ManualPresentation=true;voices.StopPlayback();
        // Feed only the presentation layer, without mutating simulation state.
        var state=new Idas3StoryVoiceLogic.State{size=64,version=2,flags=1,enemy=0,course=0,phase=3,raceId=9000,
            ticks=360,clockFrame=90};
        voices.Present(state,true);
        for(int i=0;i<120&&voices.Current==null;++i)yield return null;
        Check(voices.Current!=null,"Controlled start decode");
        yield return Capture("subtitle-source-bitmap");
        var speaking=voices.Current;int played=voices.PlayedCount;
        options.BeginEdit();options.Draft.storyVoiceSubtitleLanguage=1;Check(options.ApplyDraft(),"Apply English subtitles");
        Check(voices.Current==speaking&&voices.PlayedCount==played,"Changing subtitle language does not restart speech");
        yield return Capture("subtitle-english-in-game");
        options.BeginEdit();options.ResetDraft();
        Check(options.Draft.storyVoiceSubtitleLanguage==0&&options.Current.storyVoiceSubtitleLanguage==1,"Reset edits language draft only");
        options.BeginEdit();
        state.advantage=100;state.progress=.1f;state.playerAhead=1;
        for(int i=0;i<160;++i){state.ticks+=2;++state.clockFrame;voices.Present(state,true);}
        Check(voices.Current==speaking&&voices.PlayedCount==played,"Low-priority far-gap line cannot interrupt original opening priority");
        int beforeRepeat=voices.ScheduledFrames;voices.Present(state,true);
        Check(voices.ScheduledFrames==beforeRepeat,"Repeated render sample cannot advance original scheduler");
        state.phase=4;state.outcome=1;voices.Present(state,true);
        options.BeginEdit();options.Draft.storyVoiceSubtitles=false;
        Check(options.Current.storyVoiceSubtitles,"Subtitle draft does not immediately apply");
        Check(options.ApplyDraft(),"Apply subtitle off");yield return Capture("subtitle-disabled");
        options.BeginEdit();options.Draft.storyVoices=false;Check(options.ApplyDraft(),"Apply voice off");
        voices.Present(state,true);Check(voices.Current==null&&!voices.Loading,"Voice off cancels speech and pending decode");
        options.BeginEdit();options.Draft.storyVoices=true;Check(options.ApplyDraft(),"Enable cancellation fixture");
        state.raceId++;state.enemy=1;state.phase=3;state.outcome=0;state.ticks=360;state.clockFrame=90;voices.Present(state,true);
        Check(voices.Loading,"Decode is asynchronous");
        options.BeginEdit();options.Draft.storyVoices=false;Check(options.ApplyDraft(),"Disable while decoding");
        voices.Present(state,true);yield return Frames(2);
        Check(!voices.Loading&&voices.Current==null,"Disabled pending request cannot start a stale voice");
        var reloaded=new Idas3GameOptions();reloaded.Initialize(Path.GetDirectoryName(options.FilePath));
        Check(!reloaded.Current.storyVoices&&!reloaded.Current.storyVoiceSubtitles&&reloaded.Current.storyVoiceSubtitleLanguage==1,"Switches and language persist independently");
        Finish(true,null);
    }
    private void Finish(bool passed,string error){
        if(finished)return;finished=true;
        bool stopped=false;
        try{host.DiagnosticFocusOverride=null;host.StopNative();stopped=!host.Ready;}catch(Exception e){passed=false;error+="\n"+e;}
        File.WriteAllText(Path.Combine(root,"report.json"),JsonUtility.ToJson(new Report{
            passed=passed&&stopped,error=error,shutdownComplete=stopped,checks=checks,decodedBanks=decodedBanks,clips=clips},true));
        Debug.Log((passed?"PASS":"FAIL")+" story voices: "+error);
        Application.Quit(passed?0:1);
    }
}
