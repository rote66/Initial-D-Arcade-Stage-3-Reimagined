using System.Collections.Generic;
using UnityEngine;

// ARMSX2-style circular stick and touch priority. Button colors, rounded outline
// and pedal grooves follow the supplied NFS3 Android TouchControlsOverlay.
[DefaultExecutionOrder(-200)]
public sealed class Idas3VirtualControls : MonoBehaviour
{
    private sealed class Button {public string name;public Rect rect;public float baseWidth;public bool trigger;}
    private sealed class Contact {public string owner;public Vector2 origin,point;}
    private readonly List<Button> buttons=new List<Button>();
    private readonly Dictionary<int,Contact> fingers=new Dictionary<int,Contact>();
    private readonly HashSet<string> held=new HashSet<string>();
    private readonly HashSet<int> seen=new HashSet<int>();
    private readonly List<int> removed=new List<int>();
    private readonly Idas3GravitySteering gravity=new Idas3GravitySteering();
    internal Idas3TouchLayout LayoutStore {get;}=new Idas3TouchLayout();
    internal Idas3TouchLayout.Values EditingLayout {get;set;}
    private int layoutRevision,lastRevision=-1;
    internal void InitializeLayout(string root,string exports=null){LayoutStore.Initialize(root,exports);InvalidateLayout();}
    internal void InvalidateLayout(){++layoutRevision;}
    internal Rect SafeRect {get{var r=Screen.safeArea;return new Rect(r.x,Screen.height-r.yMax,r.width,r.height);}}
    internal float LayoutUnit {get{Layout();return scale;}}
    internal Rect ControlRect(string id)=>id=="STICK"?StickRect:ButtonRect(id);
    private Idas3SceneGame host;
    private Rect stickRect;
    private Rect lastSafe;
    private int lastWidth,lastHeight;
    private Vector2 stick;
    private int stickFinger=-1;
    private float scale,travel,brake,gas;
    private bool paused;
    internal bool DiagnosticInput {get;set;}
    internal bool CameraHeld=>held.Contains("VIEW");
    internal Rect StickRect {get{Layout();return stickRect;}}
    internal float StickTravel {get{Layout();return travel;}}
    internal Rect ButtonRect(string name){Layout();foreach(var b in buttons)if(b.name==name)return b.rect;return Rect.zero;}
    internal bool HasTouchStick=>stickFinger>=0;
    private bool Focused=>!paused&&(host?.DiagnosticFocusOverride??Application.isFocused);
    private bool Driving=>host!=null&&host.Ready&&host.MobileSteeringAllowed;
    private void Awake(){host=GetComponent<Idas3SceneGame>();Input.simulateMouseWithTouches=false;Layout();}
    private void Layout()
    {
        Rect safe=Screen.safeArea;
        if(buttons.Count>0&&lastRevision==layoutRevision&&lastSafe==safe&&lastWidth==Screen.width&&lastHeight==Screen.height)return;
        if(buttons.Count>0)ResetInput();lastSafe=safe;lastWidth=Screen.width;lastHeight=Screen.height;
        lastRevision=layoutRevision;
        buttons.Clear();safe.y=Screen.height-safe.yMax;
        scale=Mathf.Clamp(Mathf.Min(safe.width/1280f,safe.height/720f),.45f,3f);
        float edge=20*scale,top=safe.y+18*scale,left=safe.x+edge,right=safe.xMax-edge;
        void Add(string name,float x,float y,float w,float h,bool trigger=false){buttons.Add(new Button{name=name,baseWidth=w,rect=new Rect(x,y,w*scale,h*scale),trigger=trigger});}
        // NFS3 Android: steering at lower left, gas inside/brake outside at
        // lower right, utility and shift keys in side columns. Reserve the
        // upper-left four-line OSD rather than covering the central picture.
        float bottom=safe.yMax-edge;
        Add("L2",right-92*scale,bottom-140*scale,92,140,true);
        Add("R2",right-216*scale,bottom-196*scale,100,196,true);
        Add("R1",left,top+184*scale,96,56);
        Add("L1",left,top+252*scale,96,56);
        Add("VIEW",right-104*scale,top+96*scale,104,56);
        Add("LIGHT",right-104*scale,top+164*scale,104,56);
        Add("START",safe.center.x-64*scale,safe.yMax-edge-48*scale,128,48);
        Add("A",right-72*scale,bottom-284*scale,72,72);
        Add("B",right-164*scale,bottom-284*scale,72,72);
        float diameter=180*scale;stickRect=new Rect(left,safe.yMax-edge-diameter,diameter,diameter);
        var layout=EditingLayout??LayoutStore.Current;
        foreach(var b in buttons)b.rect=Idas3TouchLayout.Place(layout,b.name,b.rect,safe,scale);
        stickRect=Idas3TouchLayout.Place(layout,"STICK",stickRect,safe,scale);
        travel=stickRect.width*(68-30)/68/2; // ARMSX2 base/knob diameter ratio.
    }
    private void Update()
    {
        Layout();if(EditingLayout!=null)return;if(DiagnosticInput)return;
        var multiplayer=host?.GetComponent<Idas3.Multiplayer.Idas3MultiplayerMenu>();
        if(multiplayer!=null&&multiplayer.IsOpen){ResetInput();return;}
        if(!Focused){ResetInput();return;}
        seen.Clear();
        for(int i=0;i<Input.touchCount;++i){Touch t=Input.GetTouch(i);seen.Add(t.fingerId);
            ProcessTouch(t.fingerId,t.phase,new Vector2(t.position.x,Screen.height-t.position.y));}
        removed.Clear();foreach(int id in fingers.Keys)if(!seen.Contains(id))removed.Add(id);
        foreach(int id in removed)Release(id);
        RebuildHeld();var settings=host?.GameOptions?.Current;
        gravity.Tick(settings?.gravitySteering==true&&Driving,settings?.gravityDeadzone??.05f,settings?.gravitySensitivity??1);
    }
    // One owner per finger; leaving a button releases it without activating its
    // neighbor. A second stick finger cannot steal the current drag.
    internal void ProcessTouch(int id,TouchPhase phase,Vector2 point)
    {
        if(phase==TouchPhase.Ended||phase==TouchPhase.Canceled){Release(id);RebuildHeld();return;}
        if(phase==TouchPhase.Began){
            if(fingers.ContainsKey(id))Release(id);
            string hit=HitControl(point,stickFinger<0);
            if(hit=="STICK"){
                fingers[id]=new Contact{owner="STICK",origin=point,point=point};stickFinger=id;stick=Vector2.zero;
            }else if(hit!=null)fingers[id]=new Contact{owner=hit,origin=point,point=point};
        }
        if(fingers.TryGetValue(id,out var contact)){
            contact.point=point;
            if(contact.owner=="STICK"){
                Vector2 delta=point-contact.origin;
                stick=delta.magnitude>4*(stickRect.width/68)?Vector2.ClampMagnitude(delta/travel,1):Vector2.zero;
            }
        }
        RebuildHeld();
    }
    internal string HitControl(Vector2 point,bool allowStick){
        Layout();string best=null;float distance=float.MaxValue;
        if(allowStick&&(point-stickRect.center).sqrMagnitude<=stickRect.width*stickRect.width/4){best="STICK";distance=(point-stickRect.center).sqrMagnitude/(stickRect.width*stickRect.width);}
        foreach(var b in buttons)if(b.rect.Contains(point)){Vector2 d=point-b.rect.center;float n=d.x*d.x/(b.rect.width*b.rect.width)+d.y*d.y/(b.rect.height*b.rect.height);if(n<distance){best=b.name;distance=n;}}
        return best;
    }
    private void Release(int id){fingers.Remove(id);if(stickFinger==id){stickFinger=-1;stick=Vector2.zero;}}
    private void RebuildHeld(){
        held.Clear();brake=gas=0;
        foreach(var c in fingers.Values)foreach(var b in buttons)if(c.owner==b.name&&b.rect.Contains(c.point)){
            held.Add(b.name);
            if(b.name=="L2")brake=1;
            if(b.name=="R2")gas=1;
        }
    }
    internal void InjectGravity(double x,double y,int orientation){var settings=host.GameOptions.Current;gravity.Sample(x,y,orientation,settings.gravityDeadzone,settings.gravitySensitivity);}
    internal void Apply(ref Idas3Native.FrameInput frame,Idas3SceneGame game)
    {
        if(!Focused||EditingLayout!=null)return;
        Vector2 axis=HasTouchStick?stick:game.GameOptions.Current.gravitySteering&&Driving?new Vector2(gravity.Axis,0):Vector2.zero;
        if(held.Count==0&&axis==Vector2.zero&&!HasTouchStick)return;
        frame.padConnected=1;
        // Touch neutral also overrides gravity and a physical stick while held.
        if(HasTouchStick||axis!=Vector2.zero){frame.thumbLX=Mathf.RoundToInt(axis.x*32767);frame.thumbLY=Mathf.RoundToInt(-axis.y*32767);}
        // The menus consume D-pad bits. Gravity only steers in gameplay.
        if(HasTouchStick&&!Driving){
            if(axis.y<-.45f)frame.padButtons|=1;if(axis.y>.45f)frame.padButtons|=2;
            if(axis.x<-.45f)frame.padButtons|=4;if(axis.x>.45f)frame.padButtons|=8;
        }
        if(held.Contains("R2")){
            frame.rightTrigger=255;
            // Arcade prompts call Confirm the accelerator. Non-driving owners
            // consume A, while the race consumes the full right trigger.
            if(!Driving)frame.padButtons|=0x1000;
        }
        if(held.Contains("L2"))frame.leftTrigger=(uint)Mathf.RoundToInt(brake*255);
        if(held.Contains("A"))frame.padButtons|=0x1000;
        if(held.Contains("B")){if(Driving)frame.SetKey(27);else frame.padButtons|=0x2000;}
        if(held.Contains("R1")){frame.padButtons|=0x200;frame.SetKey(69);}
        if(held.Contains("L1")){frame.padButtons|=0x100;frame.SetKey(81);}
        if(held.Contains("START")){
            frame.padButtons|=0x0010;
            // Managed race pause uses Escape; story owners use the real Start.
            if(Driving||game.PauseMenu.IsOpen)frame.SetKey(27);
        }
        if(CameraHeld)frame.SetKey(67);if(held.Contains("LIGHT"))frame.SetKey(72);
    }
    private void OnGUI()
    {
        if(host==null||!host.Ready||EditingLayout!=null||host.GetComponent<Idas3.Multiplayer.Idas3MultiplayerMenu>()?.IsOpen==true)return;Layout();
        Matrix4x4 matrix=GUI.matrix;Color color=GUI.color;
        GUI.matrix=Matrix4x4.identity;GUI.depth=-20000;GUI.color=Color.white;
        DrawControls(null,false);
        // Retain depth so this owner stays above the pause menu dimmer.
        GUI.matrix=matrix;GUI.color=color;
    }
    internal void DrawControls(string selected,bool preview)
    {
        Layout();
        float opacity=(EditingLayout??LayoutStore.Current).opacity/65f;
        foreach(var b in buttons){
            bool down=held.Contains(b.name);Color edge=down?new Color32(255,191,105,255):new Color32(203,216,232,166);
            Color fill=down?new Color32(88,64,30,137):new Color32(18,24,34,137);
            edge.a=Mathf.Clamp01(edge.a*opacity);fill.a=Mathf.Clamp01(fill.a*opacity);
            float size=b.rect.width/b.baseWidth;
            Rounded(b.rect,fill,16*size);Rounded(b.rect,edge,16*size,(down?2.2f:1.4f)*size);
            if(b.trigger){
                float value=b.name=="R2"?gas:brake;
                if(value>0)Rounded(new Rect(b.rect.x+4*scale,b.rect.yMax-(b.rect.height-8*scale)*value-4*scale,b.rect.width-8*scale,(b.rect.height-8*scale)*value),new Color(1,.65f,.25f,.22f),10*scale);
                DrawPedal(b.rect,edge);
            }
            else DrawGlyph(b.rect,b.name,edge);
            if(selected==b.name)Rounded(b.rect,new Color(1,.75f,.41f),16*size,3*size);
        }
        Vector2 display=preview?Vector2.zero:HasTouchStick?stick:host.GameOptions.Current.gravitySteering&&Driving?new Vector2(gravity.Axis,0):Vector2.zero;
        DrawCircle(stickRect,new Color(0,0,0,(HasTouchStick ? .26f : .18f)*opacity));
        DrawCircle(stickRect,new Color(1,1,1,(HasTouchStick ? .34f : .18f)*opacity),1.8f*scale);
        float knob=stickRect.width*30/68;
        Vector2 center=stickRect.center+display*travel;
        Rect knobRect=new Rect(center.x-knob/2,center.y-knob/2,knob,knob);
        DrawCircle(knobRect,new Color(1,1,1,(HasTouchStick ? .35f : .22f)*opacity));DrawCircle(knobRect,new Color(1,1,1,.55f*opacity),1.4f*scale);
        if(selected=="STICK")DrawCircle(stickRect,new Color(1,.75f,.41f),3*scale);
    }
    private static void Line(Vector2 a,Vector2 b,Color color,float width){var matrix=GUI.matrix;Vector2 d=b-a;GUI.matrix=matrix*Matrix4x4.TRS(new Vector3(a.x,a.y,0),Quaternion.Euler(0,0,Mathf.Atan2(d.y,d.x)*Mathf.Rad2Deg),Vector3.one);GUI.DrawTexture(new Rect(0,-width/2,d.magnitude,width),Texture2D.whiteTexture,ScaleMode.StretchToFill,true,0,color,0,width/2);GUI.matrix=matrix;}
    private static void DrawGlyph(Rect r,string id,Color color){
        Vector2 c=r.center;float s=Mathf.Min(r.width,r.height)*.24f,t=Mathf.Max(2,s*.13f);
        if(id=="L1"||id=="R1"){Line(c+new Vector2(-s,0),c+new Vector2(s,0),color,t);if(id=="R1")Line(c+new Vector2(0,-s),c+new Vector2(0,s),color,t);}
        else if(id=="A"){Line(c+new Vector2(-s,0),c+new Vector2(-s*.2f,s*.65f),color,t);Line(c+new Vector2(-s*.2f,s*.65f),c+new Vector2(s,-s*.65f),color,t);}
        else if(id=="B"){Line(c+new Vector2(-s,0),c+new Vector2(s,0),color,t);Line(c+new Vector2(-s,0),c+new Vector2(0,-s),color,t);Line(c+new Vector2(-s,0),c+new Vector2(0,s),color,t);}
        else if(id=="START"){
            var style=new GUIStyle(GUI.skin.label){fontSize=Mathf.RoundToInt(r.height*.35f),alignment=TextAnchor.MiddleCenter,padding=new RectOffset(0,0,0,0)};
            style.normal.textColor=color;GUI.Label(r,"START",style);
        }else if(id=="VIEW"){
            // NFS3's camera outline, lens and short raised top edge.
            Rounded(new Rect(c.x-s,c.y-s*.6f,s*2,s*1.3f),color,s*.3f,t);
            Rounded(new Rect(c.x-s*.4f,c.y-s*.4f,s*.8f,s*.8f),color,s*.4f,t);
            Line(c+new Vector2(-s*.4f,-s*.9f),c+new Vector2(s*.3f,-s*.9f),color,t);
        }else if(id=="LIGHT"){
            // NFS3's headlamp: half-round lens and three straight light rays.
            Arc(new Rect(c.x-s,c.y-s*.7f,s*1.3f,s*1.4f),90,180,color,t);
            Line(c+new Vector2(-s*.35f,-s*.7f),c+new Vector2(-s*.35f,s*.7f),color,t);
            for(int i=-1;i<=1;++i)Line(c+new Vector2(s*.1f,i*s*.5f),c+new Vector2(s,i*s*.5f),color,t);
        }
    }
    private static void Arc(Rect r,float start,float sweep,Color color,float width){
        Vector2 Point(float angle)=>r.center+new Vector2(Mathf.Cos(angle*Mathf.Deg2Rad)*r.width/2,Mathf.Sin(angle*Mathf.Deg2Rad)*r.height/2);
        Vector2 previous=Point(start);
        for(int i=1;i<=12;++i){Vector2 next=Point(start+sweep*i/12);Line(previous,next,color,width);previous=next;}
    }
    private static void DrawPedal(Rect r,Color color){
        // The touch-zone outline is the pedal. NFS3 adds only four grooves.
        float size=r.width/100f;
        for(int i=0;i<4;++i){float y=r.center.y+(i-1.5f)*9*size;Line(new Vector2(r.x+13*size,y),new Vector2(r.xMax-13*size,y),color,2*size);}
    }
    private static void Rounded(Rect r,Color c,float radius,float border=0){GUI.DrawTexture(r,Texture2D.whiteTexture,ScaleMode.StretchToFill,true,0,c,border,radius);}
    private void DrawCircle(Rect r,Color color,float border=0){
        // Unity's rounded rectangle renderer also handles circular outlines.
        Rounded(r,color,r.width/2,border);
    }
    internal void ResetInput(){fingers.Clear();held.Clear();stickFinger=-1;stick=Vector2.zero;brake=gas=0;gravity.Stop();}
    private void OnApplicationFocus(bool focused){if(!focused)ResetInput();}
    private void OnApplicationPause(bool value){paused=value;if(value)ResetInput();}
    private void OnDisable(){ResetInput();}
}
