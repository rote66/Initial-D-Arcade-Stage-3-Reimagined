using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

// Explicit private-save diagnostic. Samples enter the production touch owner
// and frame mapper; these are synthetic contacts, not OS touch/sensor events.
public sealed class Idas3MobileControlsSmoke : MonoBehaviour
{
    [DllImport(Idas3Native.Library,CallingConvention=CallingConvention.Cdecl)] private static extern int Idas3SceneModeFlowFixture(int scene);
    [DllImport(Idas3Native.Library,CallingConvention=CallingConvention.Cdecl)] private static extern int Idas3SceneModeFlowValue(int field);
    private static string pendingRoot;
    private static Idas3MobileControlsSmoke active;
    private Idas3SceneGame host;
    private Idas3VirtualControls controls;
    private string root;
    private int checks,pulse;
    private bool finished;
    private double began;
    [Serializable] private sealed class Report {
        public string schema="idas3-ios-mobile-controls-v1",scope="Production touch/frame mapper, live Gameplay toggle, persistence, native race/pause. Synthetic touch and gravity samples; no physical sensor or OS multi-touch coverage.",error;
        public bool passed,shutdownComplete;public int checks;public double seconds;
    }
    private sealed class OptionsTestPlatform : Idas3GameOptions.IPlatform {
        public int Width=>1280;public int Height=>720;public int DisplayMode=>0;public double Now=>0;
        public Idas3GameOptions.ResolutionChoice[] Resolutions=>new[]{new Idas3GameOptions.ResolutionChoice(1280,720)};
        public void Apply(Idas3GameOptions.Values previous,Idas3GameOptions.Values next,bool displayChanged){}
    }
    public static bool Configure(ref string saves){
        pendingRoot=null;active=null;var args=Idas3LaunchArguments.GetArguments();int at=Array.IndexOf(args,"-idas3-mobile-controls-smoke");
        if(at<0)return false;if(at+1>=args.Length)throw new ArgumentException("A private diagnostic root is required.");
        pendingRoot=Path.GetFullPath(args[at+1]);Directory.CreateDirectory(pendingRoot);saves=Path.Combine(pendingRoot,"userdata");
        File.WriteAllText(Path.Combine(pendingRoot,"ISOLATED_MODE_FLOW_TEST.txt"),"Private mobile UI checks; source menu fixtures never access ordinary saves.\n");return true;
    }
    public static void Attach(Idas3SceneGame host){
        if(pendingRoot==null)return;active=host.gameObject.AddComponent<Idas3MobileControlsSmoke>();active.host=host;active.root=pendingRoot;
        active.controls=host.GetComponent<Idas3VirtualControls>();active.began=Time.realtimeSinceStartupAsDouble;active.StartCoroutine(active.Run());
    }
    internal static void PrepareFrame(ref Idas3Native.FrameInput frame){if(active!=null&&!active.finished&&active.pulse!=0){frame.SetKey(active.pulse);active.pulse=0;}}
    private void Check(bool ok,string reason){++checks;if(!ok)throw new InvalidOperationException(reason);}
    private Idas3Native.FrameInput Packet(){var f=new Idas3Native.FrameInput{size=(uint)Marshal.SizeOf<Idas3Native.FrameInput>(),flags=1};controls.Apply(ref f,host);return f;}
    private static bool Key(Idas3Native.FrameInput f,int key){uint word=key<32?f.key0:key<64?f.key1:key<96?f.key2:key<128?f.key3:0;return (word&(1u<<(key&31)))!=0;}
    private void Touch(int id,TouchPhase phase,Vector2 p){controls.ProcessTouch(id,phase,p);}
    private IEnumerator Frames(int count){while(count-->0)yield return null;}
    private IEnumerator Capture(string name){
        yield return new WaitForEndOfFrame();string path=Path.Combine(root,name+".png");
        var capture=ScreenCapture.CaptureScreenshotAsTexture();try{File.WriteAllBytes(path,capture.EncodeToPNG());}finally{Destroy(capture);}
        Check(File.Exists(path)&&new FileInfo(path).Length>1000,"Missing screen capture "+name);
    }
    private IEnumerator Run(){
        // Catch exceptions while advancing each nested iterator.
        var stack=new System.Collections.Generic.Stack<IEnumerator>();stack.Push(Checks());
        while(stack.Count>0&&!finished){object next=null;Exception error=null;
            try{if(!stack.Peek().MoveNext()){stack.Pop();continue;}next=stack.Peek().Current;}catch(Exception e){error=e;}
            if(error!=null){Finish(false,error.ToString());yield break;}if(next is IEnumerator child)stack.Push(child);else yield return next;
        }
    }
    private IEnumerator Checks(){
        yield return Frames(6);Check(host.Ready&&controls!=null,"iOS controls initialized");host.DiagnosticFocusOverride=true;controls.DiagnosticInput=true;controls.ResetInput();
        foreach(int fps in new[]{30,60,90,120,240}){
            Check(Idas3FramePacing.UnityTarget(fps,true,120)==fps,"Explicit iOS cap preserved: "+fps);
        }
        Check(Idas3FramePacing.UnityTarget(0,true,60)==60,"Unlimited iOS maps to a 60 Hz display, not Unity's default 30");
        Check(Idas3FramePacing.UnityTarget(0,true,120)==120,"Unlimited iOS follows a 120 Hz display");
        Check(Idas3FramePacing.UnityTarget(0,true,0)==60&&Idas3FramePacing.UnityTarget(0,true,double.NaN)==60,"Unknown iOS refresh falls back to 60");
        Check(Idas3FramePacing.UnityTarget(0,false,60)==-1,"Desktop unlimited behavior preserved");
        Idas3FramePacing.Configure(false,0);
        Check(Application.targetFrameRate==Idas3FramePacing.UnityTarget(0,true,Screen.currentResolution.refreshRateRatio.value),"Actual iOS unlimited configuration avoids default 30");
        Idas3FramePacing.Configure(host.GameOptions.Current.vSync,host.GameOptions.Current.frameRateLimit);
        Check(!Idas3Updates.FeatureEnabled&&host.PauseMenu.Updates==null&&Idas3Updates.StartupFinished,"Updater must have no owner on iOS");
        Check(!host.GameOptions.Current.gravitySteering,"Gravity default off");
        Check(controls.ButtonRect("L2").yMin>controls.SafeRect.center.y&&controls.ButtonRect("R2").yMin>controls.SafeRect.center.y,"Both pedals in lower half");
        Check(controls.ButtonRect("R2").center.x<controls.ButtonRect("L2").center.x&&controls.ButtonRect("R2").center.x>controls.SafeRect.center.x,"NFS3 gas inside, brake outside at lower right");
        Check(Mathf.Abs(controls.ButtonRect("R2").yMax-controls.ButtonRect("L2").yMax)<.1f,"Pedals share bottom edge");
        Check(controls.StickRect.center.x<controls.SafeRect.center.x&&controls.StickRect.center.y>controls.SafeRect.center.y,"Stick at lower left");
        for(int i=0;i<Idas3TouchLayout.Ids.Length;++i)for(int j=i+1;j<Idas3TouchLayout.Ids.Length;++j)
            Check(!controls.ControlRect(Idas3TouchLayout.Ids[i]).Overlaps(controls.ControlRect(Idas3TouchLayout.Ids[j])),"Default controls do not overlap: "+Idas3TouchLayout.Ids[i]+"/"+Idas3TouchLayout.Ids[j]);
        // Circular hit test, proportional axes, radial clamp, no finger stealing.
        Vector2 center=controls.StickRect.center;float travel=controls.StickTravel;
        Touch(1,TouchPhase.Began,center);Check(Packet().thumbLX==0,"Touch starts neutral");
        Touch(1,TouchPhase.Moved,center+new Vector2(travel*.4f,0));Check(Math.Abs(Packet().thumbLX-13107)<3,"Stick proportional analog X");
        Touch(2,TouchPhase.Began,center);Touch(2,TouchPhase.Moved,center+new Vector2(-travel,0));Check(Packet().thumbLX>0,"Second finger cannot steal stick");
        Touch(1,TouchPhase.Moved,center+new Vector2(travel*4,travel*4));var diagonal=Packet();
        Check(diagonal.thumbLX>23000&&diagonal.thumbLX<23300&&diagonal.thumbLY< -23000,"Diagonal clamps radially");
        Touch(1,TouchPhase.Canceled,center);Check(!controls.HasTouchStick&&Packet().thumbLX==0,"Canceled stick resets");Touch(2,TouchPhase.Ended,center);
        Touch(3,TouchPhase.Began,controls.StickRect.position+Vector2.one);Check(!controls.HasTouchStick,"Outside circle cannot capture stick");
        foreach(string name in new[]{"L2","R2"}){
            Rect r=controls.ButtonRect(name);Touch(10,TouchPhase.Began,new Vector2(r.center.x,r.y+1));var top=Packet();
            Check((name=="L2"?top.leftTrigger:top.rightTrigger)==255,"Pedal begins at full strength even at its top: "+name);
            Touch(10,TouchPhase.Moved,r.center);var mid=Packet();Check((name=="L2"?mid.leftTrigger:mid.rightTrigger)==255,"Dragging within pedal keeps full strength: "+name);
            Touch(10,TouchPhase.Moved,new Vector2(r.center.x,r.yMax-1));var full=Packet();Check((name=="L2"?full.leftTrigger:full.rightTrigger)==255,"Pedal bottom remains full strength: "+name);
            Touch(10,TouchPhase.Moved,r.position-Vector2.one);Check(Packet().leftTrigger==0&&Packet().rightTrigger==0,"Trigger releases outside bounds");
            Touch(10,TouchPhase.Moved,r.center);Check((name=="L2"?Packet().leftTrigger:Packet().rightTrigger)==255,"Re-entering captured pedal restores full strength: "+name);
            Touch(10,TouchPhase.Ended,r.center);Check(Packet().leftTrigger==0&&Packet().rightTrigger==0,"Releasing pedal neutralizes both triggers");
        }
        Touch(11,TouchPhase.Began,controls.ButtonRect("L1").center);Touch(12,TouchPhase.Began,controls.ButtonRect("R1").center);
        Check(Key(Packet(),81)&&Key(Packet(),69)&&(Packet().padButtons&0x300)==0x300,"Shoulders map to both shift actions");controls.ResetInput();
        // Hold the actual virtual View control through the normal title owner.
        yield return OpenTitleOptions();host.PauseMenu.SelectTab(2);
        Check(host.PauseMenu.DiagnosticRows==8,"Gameplay contains upstream FOV and mobile gravity");
        host.PauseMenu.Navigate(1);host.PauseMenu.NavigateHorizontal(1);
        Check(host.GameOptions.Draft.cameraFovPercent==101,"Mobile menu adjusts upstream FOV");
        host.PauseMenu.NavigateHorizontal(-1);
        for(int i=0;i<6;++i)host.PauseMenu.Navigate(1);
        host.GameOptions.Draft.musicVolume=.7f;host.PauseMenu.Activate();
        Check(host.GameOptions.Current.gravitySteering&&host.GameOptions.Draft.gravitySteering,"Live gravity toggle on");
        Check(host.PauseMenu.DiagnosticRows==10,"Gravity response rows follow upstream gameplay settings");
        Check(host.GameOptions.Current.musicVolume==1&&host.GameOptions.Draft.musicVolume==.7f,"Live switch preserves unrelated draft");
        var reload=new Idas3GameOptions();reload.Initialize(Path.Combine(root,"userdata"));Check(reload.Current.gravitySteering&&reload.Current.musicVolume==1,"Live switch persists alone");
        host.PauseMenu.Navigate(1);host.PauseMenu.NavigateHorizontal(1);
        Check(Mathf.Abs(host.GameOptions.Current.gravityDeadzone-.06f)<.0001f,"Menu adjusts live gravity deadzone");
        host.PauseMenu.Navigate(1);host.PauseMenu.NavigateHorizontal(1);
        Check(Mathf.Abs(host.GameOptions.Current.gravitySensitivity-1.05f)<.0001f,"Menu adjusts live gravity sensitivity");
        reload.Initialize(Path.Combine(root,"userdata"));
        Check(reload.Current.gravityDeadzone==host.GameOptions.Current.gravityDeadzone&&reload.Current.gravitySensitivity==host.GameOptions.Current.gravitySensitivity&&reload.Current.musicVolume==1,"Gravity response saves independently");
        Check(host.GameOptions.SetGravityResponse(.05f,1),"Restore default gravity response");
        yield return Capture("mobile-options");host.PauseMenu.Navigate(-1);host.PauseMenu.Navigate(-1);host.PauseMenu.Activate();Check(!host.GameOptions.Current.gravitySteering,"Live toggle off");host.PauseMenu.SetOpen(false);yield return Frames(3);
        pulse=116;double deadline=Time.realtimeSinceStartupAsDouble+40;
        while(!host.MobileSteeringAllowed&&Time.realtimeSinceStartupAsDouble<deadline)yield return null;
        Check(host.MobileSteeringAllowed,"Native race started");yield return Frames(10);
        var nativeOptions=Idas3Native.ReadOptions();nativeOptions.cameraView=1;
        Check(Idas3Native.Idas3SceneApplyOptions(ref nativeOptions)==1,"Set current player view");
        Check(host.GameOptions.SetGravitySteering(true),"Enable during race");
        Check(Idas3Native.ReadOptions().cameraView==1,"Gravity toggle preserves current player view");
        var model=new Idas3GravitySteering();Check(Mathf.Abs(model.Sample(0,.35,4)-.72f)<.0001f,"UIKit landscape left (4) gravity sign/filter");
        Check(Mathf.Abs(model.Sample(0,.35,3)+.72f)<.0001f,"UIKit landscape right (3) resets and reverses sign");model.Reset();
        Check(model.Sample(0,.01,3)==0,"Gravity deadzone");Check(model.Sample(.35,0,1)>.7f,"Portrait transform");Check(model.Sample(.35,0,2)<-.7f,"Upside-down transform");
        model.Reset();for(int i=0;i<20;++i)model.Sample(0,-9,4);Check(model.Axis>=-1&&model.Axis<-.999f,"Gravity clamp and convergence");
        model.Reset();Check(model.Sample(0,.07,4,.25f,1)==0,"Custom deadzone ignores small tilt");
        model.Reset();float low=model.Sample(0,.07,4,.05f,.5f);model.Reset();float high=model.Sample(0,.07,4,.05f,2);
        Check(high>low*3,"Sensitivity changes tilt needed for steering");
        var invalid=Idas3GameOptions.Normalize(new Idas3GameOptions.Values{gravityDeadzone=float.NaN,gravitySensitivity=float.PositiveInfinity});
        Check(invalid.gravityDeadzone==.05f&&invalid.gravitySensitivity==1,"Invalid response values use defaults");
        var legacyRoot=Path.Combine(root,"legacy-options");Directory.CreateDirectory(legacyRoot);
        string legacy=System.Text.RegularExpressions.Regex.Replace(JsonUtility.ToJson(host.GameOptions.Current),"\"gravity(?:Deadzone|Sensitivity)\"\\s*:\\s*[^,}]+,?","");
        File.WriteAllText(Path.Combine(legacyRoot,"game-options.json"),legacy);
        reload.Initialize(legacyRoot);Check(reload.Current.gravityDeadzone==.05f&&reload.Current.gravitySensitivity==1,"Old options retain gravity defaults");
        controls.InjectGravity(0,.35,4);Check(Packet().thumbLX>23000&&(Packet().padButtons&15)==0,"Gravity steers without menu bits");
        Check(host.GameOptions.SetGravityResponse(.25f,1),"Set touch-owner gravity deadzone");controls.ResetInput();controls.InjectGravity(0,.07,4);Check(Packet().thumbLX==0,"Production mapper uses saved gravity deadzone");
        Check(host.GameOptions.SetGravityResponse(.05f,1),"Restore touch-owner response");controls.InjectGravity(0,.35,4);
        Touch(30,TouchPhase.Began,center);Check(Packet().thumbLX==0,"Neutral touch suppresses gravity");
        Touch(30,TouchPhase.Moved,center-new Vector2(travel*.3f,0));Check(Packet().thumbLX<0&&(Packet().padButtons&15)==0,"Touch overrides tilt in race");
        Touch(31,TouchPhase.Began,new Vector2(controls.ButtonRect("R2").center.x,controls.ButtonRect("R2").yMax-1));
        Touch(32,TouchPhase.Began,controls.ButtonRect("R1").center);
        Check(Packet().thumbLX<0&&Packet().rightTrigger>=250&&Key(Packet(),69)&&(Packet().padButtons&0x1000)==0,"Steer, gas and shoulder simultaneously without confirm during race");
        yield return Frames(3);
        var submitted=host.DiagnosticSubmittedInput;
        Check(submitted.thumbLX<0&&submitted.rightTrigger>=250&&Key(submitted,69),"Combined touch input reaches actual native submission");
        yield return Capture("mobile-driving");Touch(30,TouchPhase.Ended,center);Check(Packet().thumbLX>23000,"Gravity resumes after release");
        Check(host.GameOptions.SetGravitySteering(false)&&Packet().thumbLX==0,"Turning off neutralizes gravity immediately");
        controls.ResetInput();Check(Packet().padButtons==0&&Packet().rightTrigger==0&&Packet().thumbLX==0,"Lifecycle reset clears contacts and motion");
        Touch(40,TouchPhase.Began,controls.ButtonRect("START").center);yield return Frames(3);Touch(40,TouchPhase.Ended,Vector2.zero);yield return Frames(3);
        Check(host.PauseMenu.IsOpen,"START touch routes actual race pause");host.PauseMenu.SelectTab(2);for(int i=0;i<7;++i)host.PauseMenu.Navigate(1);host.PauseMenu.Activate();
        Check(host.GameOptions.Current.gravitySteering,"Switch usable while race paused");controls.InjectGravity(0,.35,3);Check(Packet().thumbLX==0,"Tilt cannot navigate pause options");
        host.PauseMenu.Activate();controls.ResetInput();host.DiagnosticFocusOverride=false;Touch(41,TouchPhase.Began,controls.ButtonRect("R2").center);Check(Packet().rightTrigger==0,"Focus gate blocks input");
        controls.ResetInput();host.DiagnosticFocusOverride=true;reload.Initialize(Path.Combine(root,"userdata"));Check(!reload.Current.gravitySteering,"Final off state saved");
        yield return LayoutChecks();yield return TitleMenuChecks();yield return StoryAndTuningChecks();yield return LocalizationChecks();Finish(true,null);
    }
    private IEnumerator StoryAndTuningChecks(){
        host.PauseMenu.SetOpen(false);controls.ResetInput();
        Check(Idas3SceneModeFlowFixture(425)==1,"Source story fixture");yield return Frames(3);
        Check((host.Status.flags&512u)!=0,"Source story is active");
        Touch(90,TouchPhase.Began,controls.ButtonRect("A").center);Check((Packet().padButtons&0x1000)!=0&&(Packet().padButtons&16)==0,"Checkmark is A, separate from Start");
        yield return Frames(3);Touch(90,TouchPhase.Ended,Vector2.zero);
        Touch(91,TouchPhase.Began,controls.ButtonRect("START").center);
        Check((Packet().padButtons&16)!=0&&!Key(Packet(),13),"Story Start emits real pad Start, not menu Enter");
        yield return Frames(3);Check((host.DiagnosticSubmittedInput.padButtons&16)!=0,"Story Start reaches native submission");
        for(int i=0;i<300&&(host.Status.flags&512u)!=0;++i)yield return null;
        Check((host.Status.flags&512u)==0&&!host.PauseMenu.IsOpen,"Held Start skips real story without opening settings");
        Touch(91,TouchPhase.Ended,Vector2.zero);controls.ResetInput();yield return Frames(3);
        Check(Idas3SceneModeFlowFixture(426)==1,"Source tuning-route fixture");yield return Frames(4);
        Check(host.Status.frontendStage==12,"Real tuning route is active");yield return Capture("mobile-tuning-route");
        Touch(92,TouchPhase.Began,controls.ButtonRect("R2").center);
        Check(Packet().rightTrigger==255&&(Packet().padButtons&0x1000)!=0,"Accelerator maps to full trigger and arcade Confirm in tuning");
        yield return Frames(3);Touch(92,TouchPhase.Ended,Vector2.zero);
        for(int i=0;i<250&&host.Status.frontendStage==12;++i)yield return null;
        Check(host.Status.frontendStage==5,"Accelerator confirms actual tuning route");controls.ResetInput();
        Check(Idas3SceneModeFlowFixture(427)==1,"Source optional-parts fixture: "+Idas3Native.Error());yield return Frames(4);
        Check(Idas3SceneModeFlowValue(23)==1&&Idas3SceneModeFlowValue(26)==0,"Optional part waits for confirmation");
        int before=Idas3SceneModeFlowValue(25);Vector2 center=controls.StickRect.center;
        Touch(93,TouchPhase.Began,center);Touch(93,TouchPhase.Moved,center-Vector2.right*controls.StickTravel);yield return Frames(5);
        Touch(94,TouchPhase.Began,controls.ButtonRect("R2").center);yield return Frames(4);Touch(94,TouchPhase.Ended,Vector2.zero);Touch(93,TouchPhase.Ended,Vector2.zero);
        for(int i=0;i<250&&Idas3SceneModeFlowValue(25)==before;++i)yield return null;
        Check(Idas3SceneModeFlowValue(25)<before,"Accelerator purchases actual optional part");yield return Capture("mobile-tuning-confirmed");
        controls.ResetInput();Check(Idas3SceneModeFlowFixture(207)==1,"Return to isolated course screen");yield return Frames(3);
    }
    private IEnumerator LocalizationChecks(){
        int previous=Idas3MenuLocalization.Language;var menu=host.PauseMenu;
        try{
            foreach(string code in new[]{"zh","zh-CN","zh-SG","zh-Hans-CN","zh-TW","zh-HK","zh-Hant-TW"})Check(Idas3MenuLocalization.ResolveDeviceLanguage(code)==2,"Chinese device tag uses upstream language: "+code);
            Check(Idas3MenuLocalization.ResolveDeviceLanguage("ja-JP")==1,"Japanese device tag");
            foreach(string code in new[]{"en-US","fr-FR",""})Check(Idas3MenuLocalization.ResolveDeviceLanguage(code)==0,"English fallback: "+code);
            for(int language=0;language<3;++language){
                Idas3MenuLocalization.SetLanguage(language);
                Check(Idas3Native.Idas3SceneSetCustomMenuLanguage(language)==1,"Native custom-menu language");
                string code=new[]{"en","ja","zh-Hans"}[language];var font=Idas3MenuLocalization.Font;
                Check(font!=null,"Upstream bundled font: "+code);
                var chars=new System.Collections.Generic.HashSet<char>();
                foreach(var entry in Idas3MenuLocalization.Entries)foreach(char c in language==0?entry.Key:entry.Value[language-1])if(c>=0x3000)chars.Add(c);
                foreach(char c in chars)Check(font.HasCharacter(c),"Catalog glyph: "+code+" / "+c);
                Check(Idas3MenuLocalization.Format("Layout exported: {0}","layout-中文.json").EndsWith("layout-中文.json",StringComparison.Ordinal),"Opaque export filename: "+code);
                Check(Idas3MenuLocalization.T("NIGHT OF FIRE")=="NIGHT OF FIRE","Song metadata stays intact: "+code);
                if(language==2)Check(Idas3MenuLocalization.T("EDIT TOUCH LAYOUT")!="EDIT TOUCH LAYOUT","Mobile copy extends upstream Chinese catalog");
                Check(Idas3SceneModeFlowFixture(350)==1,"Private localized Save Select fixture");yield return Frames(5);
                yield return Capture("locale-"+code+"-save");yield return OpenTitleOptions();menu.SelectTab(2);yield return Frames(3);
                Check(host.GameOptions.SetGravitySteering(true),"Enable localized gravity settings");yield return Frames(3);
                yield return Capture("locale-"+code+"-gameplay");menu.SelectTab(8);yield return Frames(3);
                yield return Capture("locale-"+code+"-touch");menu.Activate();Check(menu.TouchLayoutEditing,"Localized layout editor opens");yield return Frames(3);
                yield return Capture("locale-"+code+"-layout");menu.TouchEditor.Close(false);menu.SetOpen(false);
                Check(host.GameOptions.SetPerformanceOsd(true),"Enable localized OSD");yield return Frames(5);
                var osd=host.GetComponent<Idas3PerformanceOsd>();Rect safe=controls.SafeRect;
                Check(osd.DisplayRect.height>=4*osd.LineHeight&&osd.LineHeight>0,"Four complete measured font lines: "+code);
                Check(safe.Contains(osd.DisplayRect.min)&&safe.Contains(osd.DisplayRect.max),"OSD stays inside safe area: "+code);
                foreach(string id in Idas3TouchLayout.Ids)Check(!osd.DisplayRect.Overlaps(controls.ControlRect(id)),"OSD does not cover "+id+": "+code);
                yield return Capture("locale-"+code+"-osd");Check(host.GameOptions.SetPerformanceOsd(false)&&host.GameOptions.SetGravitySteering(false),"Restore isolated overlay settings");
            }
        }finally{Idas3MenuLocalization.SetLanguage(previous);Idas3Native.Idas3SceneSetCustomMenuLanguage(previous);menu.SetOpen(false);}
    }
    private IEnumerator LayoutChecks(){
        var menu=host.PauseMenu;menu.SelectTab(8);Check(menu.SelectedTab==8,"Touch layout settings category reachable");yield return Frames(3);menu.Activate();
        Check(menu.TouchLayoutEditing&&controls.EditingLayout!=null,"Layout editor opens through settings");var editor=menu.TouchEditor;
        Rect original=controls.ButtonRect("R2");Vector2 start=editor.ToPreview(original.center),end=editor.ToPreview(original.center+new Vector2(-160,80)*controls.LayoutUnit);
        Check((editor.FromPreview(start)-original.center).magnitude<.1f,"Preview pointer maps to actual game bounds");
        editor.ProcessTouch(60,TouchPhase.Began,start);editor.ProcessTouch(60,TouchPhase.Moved,end);editor.ProcessTouch(60,TouchPhase.Ended,end);
        Check(editor.Selected=="R2"&&(controls.ButtonRect("R2").center-original.center).magnitude>100,"Drag moves selected accelerator in preview");
        var entry=editor.Draft.Get("R2");Check(entry.positioned&&entry.fromRight&&entry.fromBottom,"Layout stores lower-right edge anchors");
        editor.ResizeSelected(150);Check(Mathf.Abs(controls.ButtonRect("R2").width-original.width*1.5f)<.1f,"Resize affects actual control bounds");
        Check(Packet().padButtons==0&&Packet().rightTrigger==0,"Editing does not drive game controls");
        yield return Capture("touch-layout-editor");editor.Close(false);
        Check(!menu.TouchLayoutEditing&&Mathf.Abs(controls.ButtonRect("R2").width-original.width)<.1f&&(controls.ButtonRect("R2").center-original.center).magnitude<.1f,"Cancel restores saved layout");
        yield return Frames(3);menu.Activate();editor.Select("R2");editor.MoveSelected(original.center+new Vector2(-160,80)*controls.LayoutUnit,true);editor.ResizeSelected(150);
        Rect saved=controls.ButtonRect("R2");editor.ProcessTouch(61,TouchPhase.Began,editor.CommandRect(1).center);
        Check(!menu.TouchLayoutEditing&&File.Exists(controls.LayoutStore.FilePath),"SAVE toolbar commits and closes");
        var layout=new Idas3TouchLayout();layout.Initialize(Path.Combine(root,"userdata"),Path.Combine(root,"export-check"));
        Check(layout.Current.Get("R2").size==150&&layout.Current.Get("R2").positioned,"Layout persists across reload");
        var wider=new Rect(0,0,1800,720);var narrower=new Rect(0,0,1280,720);Rect a=Idas3TouchLayout.Place(layout.Current,"R2",new Rect(1180,100,82,116),narrower,1),b=Idas3TouchLayout.Place(layout.Current,"R2",new Rect(1700,100,82,116),wider,1);
        Check(Mathf.Abs((wider.xMax-b.center.x)-(narrower.xMax-a.center.x))<.1f,"Edge distance preserved across different aspect ratios");
        Touch(62,TouchPhase.Began,new Vector2(saved.center.x,saved.yMax-1));Check(Packet().rightTrigger>=250,"Saved enlarged bounds operate trigger");Touch(62,TouchPhase.Ended,saved.center);
        Touch(63,TouchPhase.Began,original.center);Check(Packet().rightTrigger==0,"Previous position no longer operates moved control");Touch(63,TouchPhase.Ended,original.center);
        yield return Frames(3);menu.Activate();editor=menu.TouchEditor;string export=editor.Export();
        Check(export!=null&&File.Exists(export),"JSON export created");var parsed=Idas3TouchLayout.Parse(File.ReadAllText(export));
        Check(parsed.Get("R2").size==150&&parsed.Get("R2").dx==layout.Current.Get("R2").dx,"Export contains saved size and anchor");
        bool rejected=false;try{Idas3TouchLayout.Parse("{\"schema\":\"wrong\",\"version\":1,\"controls\":[]}");}catch{rejected=true;}Check(rejected,"Unsupported layout rejected");
        var legacy=parsed.Clone();legacy.snap=false;legacy.opacity=42;
        Array.Resize(ref legacy.controls,legacy.controls.Length+1);legacy.controls[legacy.controls.Length-1]=new Idas3TouchLayout.Control{id="MENU",positioned=true,dx=25,dy=30,size=140};
        var migrated=Idas3TouchLayout.Parse(JsonUtility.ToJson(legacy));
        Check(migrated.controls.Length==Idas3TouchLayout.Ids.Length&&!migrated.snap&&migrated.opacity==42,"Legacy MENU entry is dropped without resetting layout preferences");
        foreach(var c in parsed.controls){var kept=migrated.Get(c.id);Check(JsonUtility.ToJson(c)==JsonUtility.ToJson(kept),"Legacy layout preserves "+c.id);}
        string legacyRoot=Path.Combine(root,"legacy-touch-layout");Directory.CreateDirectory(legacyRoot);File.WriteAllText(Path.Combine(legacyRoot,"touch-layout.json"),JsonUtility.ToJson(legacy));
        var legacyStore=new Idas3TouchLayout();legacyStore.Initialize(legacyRoot);
        Check(legacyStore.LastError==null&&JsonUtility.ToJson(legacyStore.Current)==JsonUtility.ToJson(migrated),"Production startup loads old MENU layout without resetting controls");
        legacy.controls[legacy.controls.Length-1].id="UNKNOWN";
        rejected=false;try{Idas3TouchLayout.Parse(JsonUtility.ToJson(legacy));}catch{rejected=true;}Check(rejected,"Migration still rejects unknown controls");
        legacy.controls[legacy.controls.Length-1].id="R2";
        rejected=false;try{Idas3TouchLayout.Parse(JsonUtility.ToJson(legacy));}catch{rejected=true;}Check(rejected,"Migration still rejects duplicate controls");
        editor.ResetDraft();Check(editor.Draft.Get("R2").size==100&&!editor.Draft.Get("R2").positioned,"Reset restores defaults in draft");editor.Close(false);
        Check(controls.LayoutStore.Current.Get("R2").size==150,"Cancel after reset preserves saved layout");
        Check(controls.LayoutStore.Save(Idas3TouchLayout.Defaults()),"Restore isolated default layout");controls.InvalidateLayout();
        yield return Frames(3);menu.SelectTab(8);menu.Navigate(1);menu.Activate();editor=menu.TouchEditor;
        Check(menu.TouchLayoutEditing&&editor.SelectingImport,"Settings IMPORT row opens the editor and file selection");
        string savedJson=File.ReadAllText(controls.LayoutStore.FilePath),exportJson=File.ReadAllText(export);
        editor.SelectedTouchLayout(export);yield return Frames(3);
        Check(!editor.SelectingImport&&editor.Draft.Get("R2").size==150,"Exported JSON imports through production callback");
        Check(controls.LayoutStore.Current.Get("R2").size==100&&File.ReadAllText(controls.LayoutStore.FilePath)==savedJson,"Imported preview does not overwrite saved layout");
        Check(Mathf.Abs(controls.ButtonRect("R2").width-original.width*1.5f)<.1f,"Imported preview updates actual control bounds");
        yield return Capture("touch-layout-import-preview");editor.Close(false);yield return Frames(3);
        Check(controls.LayoutStore.Current.Get("R2").size==100&&Mathf.Abs(controls.ButtonRect("R2").width-original.width)<.1f,"CANCEL discards imported preview");
        menu.SelectTab(8);menu.Activate();editor=menu.TouchEditor;editor.ResizeSelected(125);
        string draftJson=JsonUtility.ToJson(editor.Draft),badFile=Path.Combine(root,"invalid-touch-layout.json");
        foreach(string invalid in new[]{"{", "{\"schema\":\"wrong\",\"version\":1,\"controls\":[]}", "{\"schema\":\"idas3-touch-layout\",\"version\":2,\"controls\":[]}","{\"schema\":\"idas3-touch-layout\",\"version\":1,\"controls\":[{\"id\":\"R2\"},{\"id\":\"R2\"}]}",new string(' ',65537)}){
            File.WriteAllText(badFile,invalid);editor.ProcessTouch(64,TouchPhase.Began,editor.CommandRect(5).center);
            Check(editor.SelectingImport,"Editor IMPORT toolbar starts selection");editor.SelectedTouchLayout(badFile);
            Check(!editor.SelectingImport&&JsonUtility.ToJson(editor.Draft)==draftJson&&File.ReadAllText(controls.LayoutStore.FilePath)==savedJson,"Invalid import preserves draft and saved layout");
        }
        editor.BeginImport();editor.SelectedTouchLayout(Path.Combine(root,"missing-layout.json"));
        Check(JsonUtility.ToJson(editor.Draft)==draftJson,"Missing import file preserves draft");
        editor.BeginImport();editor.SelectedTouchLayout("");
        Check(!editor.SelectingImport&&JsonUtility.ToJson(editor.Draft)==draftJson,"File picker cancellation preserves draft");
        var importPreferences=parsed.Clone();importPreferences.opacity=42;importPreferences.snap=false;
        if(Array.IndexOf(Idas3TouchLayout.Ids,"MENU")<0){Array.Resize(ref importPreferences.controls,importPreferences.controls.Length+1);importPreferences.controls[importPreferences.controls.Length-1]=new Idas3TouchLayout.Control{id="MENU"};}
        string importFile=Path.Combine(root,"import-preferences.json");File.WriteAllText(importFile,JsonUtility.ToJson(importPreferences));
        editor.BeginImport();editor.SelectedTouchLayout(importFile);
        Check(editor.Draft.controls.Length==Idas3TouchLayout.Ids.Length&&editor.Draft.opacity==42&&!editor.Draft.snap,"Old exported MENU layout imports with preferences intact");
        editor.ProcessTouch(65,TouchPhase.Began,editor.CommandRect(1).center);
        layout.Initialize(Path.Combine(root,"userdata"),Path.Combine(root,"export-check"));
        Check(!menu.TouchLayoutEditing&&layout.Current.Get("R2").size==150&&layout.Current.opacity==42&&!layout.Current.snap,"SAVE persists imported layout across reload");
        Check(File.ReadAllText(export)==exportJson,"Import preserves original export file");
        menu.SelectTab(8);menu.Activate();editor=menu.TouchEditor;
        for(int i=0;i<6;++i)for(int j=i+1;j<6;++j)Check(!editor.CommandRect(i).Overlaps(editor.CommandRect(j)),"Editor toolbar buttons remain distinct: "+i+"/"+j);
        editor.BeginImport();editor.Close(false);editor.SelectedTouchLayout(export);
        Check(!editor.IsOpen&&controls.EditingLayout==null,"Late picker callback cannot reopen a closed editor");
        Check(controls.LayoutStore.Save(Idas3TouchLayout.Defaults()),"Restore layout after import checks");controls.InvalidateLayout();
        yield return Frames(3);menu.SelectTab(8);yield return Capture("touch-layout-options");menu.SetOpen(false);
    }
    private IEnumerator OpenTitleOptions(){
        var menu=host.PauseMenu;menu.SetOpen(false);controls.ResetInput();yield return Frames(4);
        Check(Idas3SceneModeFlowFixture(10)==1,"Private ranking/title fixture");yield return Frames(4);
        Check(host.Status.frontendStage==0&&(host.Status.flags&1u)!=0,"Original title owner is active");
        Touch(80,TouchPhase.Began,controls.ButtonRect("VIEW").center);
        double deadline=Time.realtimeSinceStartupAsDouble+4;
        while(!menu.IsOpen&&Time.realtimeSinceStartupAsDouble<deadline)yield return null;
        Check(menu.IsOpen&&menu.AttractOptions&&menu.OptionsVisible,"Long VIEW opens original title settings");
        Touch(80,TouchPhase.Ended,Vector2.zero);yield return Frames(4);
        Check(host.Status.frontendStage==0&&(host.Status.flags&2u)==0,"Title settings do not pause or leave original title");
    }
    private IEnumerator TitleMenuChecks(){
        var menu=host.PauseMenu;menu.SetOpen(false);controls.ResetInput();yield return Frames(4);
        Check(controls.ButtonRect("MENU")==Rect.zero&&Array.IndexOf(Idas3TouchLayout.Ids,"MENU")<0,"No independent MENU control or layout entry");
        Check(Idas3SceneModeFlowFixture(10)==1,"Private title tap fixture");yield return Frames(4);
        Touch(81,TouchPhase.Began,controls.ButtonRect("VIEW").center);yield return Frames(2);
        Touch(81,TouchPhase.Ended,Vector2.zero);yield return Frames(4);
        Check(!menu.IsOpen&&host.Status.frontendStage==0,"Short VIEW tap does not open settings");
        yield return OpenTitleOptions();yield return Capture("mobile-title-options");
        menu.SelectTab(8);for(int i=0;i<4;++i)menu.Navigate(1);
        host.GameOptions.Draft.musicVolume=.6f;menu.Activate();
        Check(host.GameOptions.Current.showOsd&&host.GameOptions.Draft.showOsd,"OSD toggle applies immediately");
        var reloaded=new Idas3GameOptions();reloaded.Initialize(Path.Combine(root,"userdata"));
        Check(reloaded.Current.showOsd&&reloaded.Current.musicVolume!=.6f,"OSD persists independently of unrelated draft");
        double until=Time.realtimeSinceStartupAsDouble+.8;while(Time.realtimeSinceStartupAsDouble<until)yield return null;
        var osd=host.GetComponent<Idas3PerformanceOsd>();
        Check(osd.Visible&&osd.Text.Split('\n').Length==4&&osd.Text.Contains("CPU ")&&osd.Text.Contains("GPU ")&&osd.Text.Contains("RAM ")&&osd.Text.Contains("MiB"),"Four-line measured OSD");
        Check(osd.Text.Split('\n')[0]=="FPS "+menu.FrameRateText.Split(' ')[0],"OSD and original FPS share the same render sample");
        File.WriteAllText(Path.Combine(root,"osd-sample.txt"),osd.Text);yield return Capture("mobile-osd-options");
        menu.Activate();Check(!host.GameOptions.Current.showOsd&&!osd.Visible,"OSD off hides immediately");
        yield return DiagnosticLoggingChecks();
        yield return ThermalFrameRateChecks();
        // Closing via the actual virtual Start must consume the press, leaving
        // the title intact and not reopening settings while it is held.
        Touch(82,TouchPhase.Began,controls.ButtonRect("START").center);yield return Frames(4);
        Check(menu.IsOpen&&menu.DiagnosticSelection==0,"First START returns to the category row");
        Touch(82,TouchPhase.Ended,Vector2.zero);yield return Frames(4);
        Touch(82,TouchPhase.Began,controls.ButtonRect("START").center);yield return Frames(8);
        Check(!menu.IsOpen&&host.Status.frontendStage==0&&(host.Status.flags&8u)!=0,"Second START closes title settings without starting or quitting");
        Touch(82,TouchPhase.Ended,Vector2.zero);yield return Frames(5);controls.ResetInput();
        Check(Idas3SceneModeFlowFixture(207)==1,"Course owner for default layout capture");yield return Frames(4);
        Check(host.GameOptions.SetPerformanceOsd(true),"Enable OSD for layout capture");
        until=Time.realtimeSinceStartupAsDouble+.8;while(Time.realtimeSinceStartupAsDouble<until)yield return null;
        yield return Capture("mobile-default-layout-osd");Check(host.GameOptions.SetPerformanceOsd(false),"Restore isolated OSD default");
        yield return OpenTitleOptions();menu.SelectTab(9);yield return Frames(3);yield return Capture("mobile-network-options");
        menu.Activate();yield return Frames(6);
        var lan=host.GetComponent<Idas3.Multiplayer.Idas3MultiplayerMenu>();
        Check(!menu.IsOpen&&lan.IsOpen&&host.MultiplayerSession.Available&&host.MultiplayerSession.TransportIndex==1,"Title settings open the working LAN menu");
        host.MultiplayerSession.SelectTransport(0);Check(host.MultiplayerSession.TransportIndex==1,"iOS cannot select EOS internet matchmaking");
        yield return Capture("mobile-lan-menu");lan.SetOpen(false);yield return Frames(3);
    }
    private IEnumerator DiagnosticLoggingChecks(){
        var menu=host.PauseMenu;var diagnostics=host.GetComponent<Idas3IosDiagnostics>();
        Check(diagnostics!=null&&!host.GameOptions.Current.diagnosticLogging&&!diagnostics.Recording,"Diagnostic logging defaults off");
        Check(!Directory.Exists(diagnostics.Folder),"Disabled logging creates no directory");
        Check(menu.DiagnosticRows==7,"iOS menu includes seven rows");
        menu.Navigate(1);Check(menu.DiagnosticSelection==6,"Logging has its own iOS row");
        host.GameOptions.Draft.musicVolume=.6f;menu.Activate();
        Check(host.GameOptions.Current.diagnosticLogging&&diagnostics.Recording,"Logging starts immediately");
        var saved=new Idas3GameOptions();saved.Initialize(Path.Combine(root,"userdata"));
        Check(saved.Current.diagnosticLogging&&saved.Current.musicVolume!=.6f,"Logging saves without applying the music draft");
        Debug.Log("IDAS3 DIAGNOSTIC FILE SMOKE");
        double until=Time.realtimeSinceStartupAsDouble+2.5;while(Time.realtimeSinceStartupAsDouble<until)yield return null;
        string runtime=Path.Combine(diagnostics.Folder,"runtime.log"),performance=Path.Combine(diagnostics.Folder,"performance.jsonl");
        Check(File.Exists(runtime)&&File.ReadAllText(runtime).Contains("IDAS3 DIAGNOSTIC FILE SMOKE"),"Unity logs reach the background file");
        Check(File.Exists(performance)&&File.ReadAllText(performance).Contains("\"thermalState\"")&&File.ReadAllText(performance).Contains("\"targetFps\"")&&
            File.ReadAllText(performance).Contains("\"displayLinkPreferredFps\"")&&File.ReadAllText(performance).Contains("\"thermalFrameRateAdjustment\""),"Performance files contain system, pacing and display-link metrics");
        File.Copy(runtime,Path.Combine(root,"diagnostic-runtime-observed.log"));
        File.Copy(performance,Path.Combine(root,"diagnostic-performance-observed.jsonl"));
        Check(host.GetComponent<Idas3SceneGame>().NativeStepMilliseconds>=0,"Native frame timings available");
        yield return Capture("mobile-diagnostic-logging");
        menu.Activate();Check(!host.GameOptions.Current.diagnosticLogging&&!diagnostics.Recording,"Logging stops immediately");
        Check(!Directory.Exists(diagnostics.Folder),"Turning logging off deletes its generated files");
        saved.Initialize(Path.Combine(root,"userdata"));Check(!saved.Current.diagnosticLogging,"Logging off persists");
        yield return Frames(5);
        Check(!Directory.Exists(diagnostics.Folder),"Closed background writer cannot recreate deleted logs");
        menu.Navigate(-1);
    }
    private IEnumerator ThermalFrameRateChecks(){
        var options=host.GameOptions;var menu=host.PauseMenu;
        Check(options.Current.iosThermalFrameRateAdjustment&&Idas3IosThermalFrameRate.Enabled,"Thermal adjustment defaults on");
        string legacyRoot=Path.Combine(root,"thermal-legacy");Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot,"game-options.json"),"{\"version\":1,\"musicVolume\":0.7}");
        var isolated=new Idas3GameOptions(new OptionsTestPlatform());isolated.Initialize(legacyRoot);
        Check(isolated.Current.iosThermalFrameRateAdjustment,"Old saved options retain the enabled thermal default");
        isolated.BeginEdit();isolated.Draft.width=1920;
        Check(isolated.ApplyDraft()&&isolated.DisplayConfirmationPending,"Private display rollback prepared");
        Check(isolated.SetIosThermalFrameRateAdjustment(false)&&Idas3IosThermalFrameRate.Enabled,"Fake platform does not alter native thermal state");
        isolated.RevertDisplay();Check(!isolated.Current.iosThermalFrameRateAdjustment,"Display rollback retains independently saved thermal switch");
        isolated.Initialize(legacyRoot);Check(!isolated.Current.iosThermalFrameRateAdjustment,"Thermal off reloads from disk");
        Directory.CreateDirectory(isolated.FilePath+".tmp");
        Check(!isolated.SetIosThermalFrameRateAdjustment(true)&&!isolated.Current.iosThermalFrameRateAdjustment&&
            !isolated.Draft.iosThermalFrameRateAdjustment&&Idas3IosThermalFrameRate.Enabled,"Save failure preserves current, draft and native thermal state");
        Idas3MenuLocalization.SetLanguage(options.Current.customMenuLanguage);
        menu.SelectTab(8);for(int i=0;i<6;++i)menu.Navigate(1);
        Check(menu.DiagnosticSelection==7,"Thermal switch has its own iOS row");
        bool simulated=Idas3IosThermalFrameRate.DiagnosticThermalState(2);
        if(simulated){
            Idas3FramePacing.Configure(false,60);
            Check(Application.targetFrameRate==60&&Math.Abs(Idas3IosThermalFrameRate.PreferredFps-30)<.01,"Serious caps the actual display link at 30 while Unity target remains 60");
            yield return Capture("mobile-thermal-on");
        }
        options.Draft.musicVolume=.6f;menu.Activate();
        Check(!options.Current.iosThermalFrameRateAdjustment&&!options.Draft.iosThermalFrameRateAdjustment&&!Idas3IosThermalFrameRate.Enabled,"Menu disables native thermal cap immediately");
        var reloaded=new Idas3GameOptions();reloaded.Initialize(Path.Combine(root,"userdata"));
        Check(!reloaded.Current.iosThermalFrameRateAdjustment&&reloaded.Current.musicVolume!=.6f&&
            options.Draft.musicVolume==.6f,"Thermal switch persists independently and preserves unrelated draft");
        if(simulated){
            Check(Math.Abs(Idas3IosThermalFrameRate.PreferredFps-60)<.01,"Disabling thermal adjustment immediately restores 60 without leaving the race");
            yield return Capture("mobile-thermal-off");
            menu.NavigateHorizontal(1);
            Check(options.Current.iosThermalFrameRateAdjustment&&Idas3IosThermalFrameRate.Enabled&&
                Math.Abs(Idas3IosThermalFrameRate.PreferredFps-30)<.01,"Horizontal control re-enables the Serious cap immediately");
            Check(Idas3IosThermalFrameRate.DiagnosticThermalState(3)&&Math.Abs(Idas3IosThermalFrameRate.PreferredFps-15)<.01,"Critical caps the display link at 15");
            menu.Activate();Check(!Idas3IosThermalFrameRate.Enabled&&Math.Abs(Idas3IosThermalFrameRate.PreferredFps-60)<.01,"Disabling also removes the Critical cap");
            Idas3FramePacing.Configure(false,30);Check(Math.Abs(Idas3IosThermalFrameRate.PreferredFps-30)<.01,"Thermal off preserves the explicit 30 FPS setting");
            Idas3FramePacing.Configure(false,0);
            Check(Math.Abs(Idas3IosThermalFrameRate.PreferredFps-Application.targetFrameRate)<.01,"Thermal off preserves the unlimited display-refresh mapping");
            Check(Idas3IosThermalFrameRate.DiagnosticThermalState(-1),"Restore actual Simulator thermal state");
        }
        Check(options.SetIosThermalFrameRateAdjustment(true)&&Idas3IosThermalFrameRate.Enabled,"Restore isolated thermal default");
        Idas3FramePacing.Configure(options.Current.vSync,options.Current.frameRateLimit);
        menu.Navigate(1);Check(menu.DiagnosticSelection==8,"Back follows the new thermal row");menu.Back();
        Check(menu.DiagnosticSelection==0,"Back returns from iOS options to categories");
        menu.Activate();
    }
    private void Update(){if(finished)return;if(host?.Failure!=null)Finish(false,host.Failure);else if(Time.realtimeSinceStartupAsDouble-began>160)Finish(false,"Mobile diagnostic timeout");}
    private void Finish(bool passed,string error){
        if(finished)return;finished=true;controls?.ResetInput();host.DiagnosticFocusOverride=null;bool stopped=false;
        try{host.StopNative();stopped=!host.Ready;}catch(Exception e){passed=false;error=(error??"")+e;}
        File.WriteAllText(Path.Combine(root,"report.json"),JsonUtility.ToJson(new Report{passed=passed&&stopped,error=error,shutdownComplete=stopped,checks=checks,seconds=Time.realtimeSinceStartupAsDouble-began},true));
        Debug.Log((passed?"PASS":"FAIL")+" mobile controls: "+error);Application.Quit(passed?0:1);
    }
}
