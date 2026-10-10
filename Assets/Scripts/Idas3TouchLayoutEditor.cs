using System;
using System.Runtime.InteropServices;
using UnityEngine;

// The preview is the real safe area fitted without changing its aspect ratio.
// Touches use the inverse transform, so dragging edits exactly the shown bounds.
public sealed class Idas3TouchLayoutEditor : MonoBehaviour
{
    private Idas3VirtualControls controls;
    private Idas3PauseMenu parent;
    private Idas3TouchLayout.Values working;
    private Rect preview,safe,sizeSlider;
    private float ui,previewScale;
    private Vector2 grab;
    private int finger=-1;
    private bool sizing,resetConfirmation,selecting;
    private string selected="STICK",notice="";
    private GUIStyle label,title;
    public bool IsOpen {get;private set;}
    internal Idas3TouchLayout.Values Draft=>working;
    internal string Selected=>selected;
    internal bool SelectingImport=>selecting;
    internal string Notice=>notice;
    internal Rect PreviewRect {get{Geometry();return preview;}}
    internal Rect CommandRect(int index){Geometry();return Top(index);}
#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern int Idas3ShareTouchLayout(string path);
    [DllImport("__Internal")] private static extern void Idas3PickTouchLayout(string receiver);
    [DllImport("__Internal")] private static extern void Idas3ReleaseTouchLayout();
#endif
    public void Open(Idas3VirtualControls owner,Idas3PauseMenu menu,bool defaults=false){
        if(IsOpen)return;controls=owner;parent=menu;working=(defaults?Idas3TouchLayout.Defaults():owner.LayoutStore.Current.Clone());
        selected="STICK";finger=-1;resetConfirmation=false;selecting=false;notice="";IsOpen=true;owner.ResetInput();owner.EditingLayout=working;owner.InvalidateLayout();Geometry();
    }
    public void Close(bool save){
        if(!IsOpen||(selecting&&save))return;
        selecting=false;
        if(save&&!controls.LayoutStore.Save(working)){notice=controls.LayoutStore.LastError;return;}
        controls.EditingLayout=null;controls.InvalidateLayout();controls.ResetInput();IsOpen=false;finger=-1;parent.TouchLayoutClosed(save);
    }
    public void Back(){if(resetConfirmation)resetConfirmation=false;else Close(false);}
    internal void Select(string id){if(Array.IndexOf(Idas3TouchLayout.Ids,id)>=0)selected=id;}
    internal void ResizeSelected(int size){working.Get(selected).size=Mathf.Clamp(size,50,180);controls.InvalidateLayout();}
    internal void MoveSelected(Vector2 center,bool snap){Idas3TouchLayout.Position(working,selected,center,controls.SafeRect,controls.LayoutUnit,snap);controls.InvalidateLayout();}
    internal void ResetDraft(){working=Idas3TouchLayout.Defaults();controls.EditingLayout=working;controls.InvalidateLayout();notice="Defaults restored. SAVE to keep, CANCEL to discard.";}
    internal string Export(){
        try{string file=controls.LayoutStore.Export(working);notice="Exported to Touch Layouts / "+System.IO.Path.GetFileName(file);Share(file,controls.DiagnosticInput);return file;}
        catch(Exception e){notice="Could not export layout: "+e.Message;return null;}
    }
    internal void BeginImport(){
        if(!IsOpen||selecting)return;
        selecting=true;finger=-1;controls.ResetInput();notice="Choose a touch layout JSON in Files.";
#if UNITY_IOS && !UNITY_EDITOR
        if(!controls.DiagnosticInput)Idas3PickTouchLayout(gameObject.name);
#endif
    }
    [UnityEngine.Scripting.Preserve]
    public void SelectedTouchLayout(string path){
        bool pending=selecting;selecting=false;
        try{
            if(!pending||!IsOpen||!parent.IsOpen)return;
            if(string.IsNullOrEmpty(path)){notice="Import cancelled. Your touch layout is unchanged.";return;}
            var imported=Idas3TouchLayout.ReadImport(path);
            working=imported;controls.EditingLayout=working;controls.ResetInput();controls.InvalidateLayout();
            finger=-1;resetConfirmation=false;notice="Layout imported for preview. SAVE to keep, CANCEL to discard.";
        }catch(Exception e){
            notice="Could not import layout. Choose an exported touch layout JSON.";
            Debug.LogWarning("Touch layout import: "+e.Message);
        }finally{
#if UNITY_IOS && !UNITY_EDITOR
            if(controls!=null&&!controls.DiagnosticInput)Idas3ReleaseTouchLayout();
#endif
        }
    }
    internal static void Share(string path,bool diagnostic){
#if UNITY_IOS && !UNITY_EDITOR
        if(!diagnostic)Idas3ShareTouchLayout(path);
#endif
    }
    // Controller/keyboard navigation remains a way out even if controls overlap.
    public void Navigate(int delta){int i=Array.IndexOf(Idas3TouchLayout.Ids,selected);Select(Idas3TouchLayout.Ids[(i+Math.Sign(delta)+Idas3TouchLayout.Ids.Length)%Idas3TouchLayout.Ids.Length]);}
    public void NavigateHorizontal(int delta){ResizeSelected(working.Get(selected).size+Math.Sign(delta)*5);}
    public void Activate(){Close(true);}
    private void Geometry(){
        safe=controls.SafeRect;ui=Mathf.Clamp(Mathf.Min(safe.width/1280,safe.height/720),.45f,3);
        Rect area=new Rect(safe.x+16*ui,safe.y+86*ui,safe.width-32*ui,Mathf.Max(80*ui,safe.height-222*ui));
        previewScale=Mathf.Min(area.width/safe.width,area.height/safe.height);
        Vector2 size=safe.size*previewScale;preview=new Rect(area.center-size/2,size);
        sizeSlider=new Rect(safe.x+300*ui,safe.yMax-109*ui,safe.width-510*ui,28*ui);
    }
    internal Vector2 ToPreview(Vector2 point){Geometry();return preview.position+(point-safe.position)*previewScale;}
    internal Vector2 FromPreview(Vector2 point){Geometry();return safe.position+(point-preview.position)/previewScale;}
    private void Update(){
        if(!IsOpen)return;if(!parent.IsOpen){selecting=false;Close(false);return;}Geometry();if(controls.DiagnosticInput||selecting)return;
        for(int i=0;i<Input.touchCount;++i){var t=Input.GetTouch(i);ProcessTouch(t.fingerId,t.phase,new Vector2(t.position.x,Screen.height-t.position.y));}
        if(finger>=0&&Input.touchCount==0)finger=-1;
    }
    private Rect Top(int index){float[] x={0,114,248,476,590,362};float[] w={104,124,104,104,174,104};return new Rect(safe.x+x[index]*ui,safe.y+12*ui,w[index]*ui,46*ui);}
    internal void ProcessTouch(int id,TouchPhase phase,Vector2 point){
        if(!IsOpen||selecting)return;Geometry();
        if(phase==TouchPhase.Ended||phase==TouchPhase.Canceled){if(finger==id)finger=-1;return;}
        if(phase==TouchPhase.Began){
            if(resetConfirmation){
                if(ResetYes.Contains(point)){ResetDraft();resetConfirmation=false;}else if(ResetNo.Contains(point))resetConfirmation=false;
                return;
            }
            for(int n=0;n<6;++n)if(Top(n).Contains(point)){
                if(n==0)Close(false);else if(n==1)Close(true);else if(n==2)Export();else if(n==3)resetConfirmation=true;else if(n==4)working.snap=!working.snap;else BeginImport();
                return;
            }
            if(SizeMinus.Contains(point)){ResizeSelected(working.Get(selected).size-5);return;}
            if(SizePlus.Contains(point)){ResizeSelected(working.Get(selected).size+5);return;}
            if(OpacityMinus.Contains(point)){working.opacity=Mathf.Max(20,working.opacity-5);return;}
            if(OpacityPlus.Contains(point)){working.opacity=Mathf.Min(100,working.opacity+5);return;}
            if(finger>=0)return;
            if(sizeSlider.Contains(point)){finger=id;sizing=true;SetSize(point);return;}
            if(preview.Contains(point)){
                Vector2 gamePoint=FromPreview(point);string hit=controls.HitControl(gamePoint,true);
                if(hit!=null){selected=hit;finger=id;sizing=false;grab=gamePoint-controls.ControlRect(hit).center;}
            }
        }else if(id==finger){
            if(sizing)SetSize(point);else MoveSelected(FromPreview(point)-grab,working.snap);
        }
    }
    private void SetSize(Vector2 point){ResizeSelected(Mathf.RoundToInt(Mathf.Lerp(50,180,Mathf.Clamp01((point.x-sizeSlider.x)/sizeSlider.width))));}
    private Rect SizeMinus=>new Rect(sizeSlider.x-60*ui,sizeSlider.y,44*ui,sizeSlider.height);
    private Rect SizePlus=>new Rect(sizeSlider.xMax+16*ui,sizeSlider.y,44*ui,sizeSlider.height);
    private Rect OpacityMinus=>new Rect(safe.x+236*ui,safe.yMax-70*ui,44*ui,28*ui);
    private Rect OpacityPlus=>new Rect(safe.x+370*ui,safe.yMax-70*ui,44*ui,28*ui);
    private Rect ResetYes=>new Rect(safe.center.x-160*ui,safe.center.y+24*ui,150*ui,42*ui);
    private Rect ResetNo=>new Rect(safe.center.x+10*ui,safe.center.y+24*ui,150*ui,42*ui);
    private static void Fill(Rect r,Color c){GUI.DrawTexture(r,Texture2D.whiteTexture,ScaleMode.StretchToFill,true,0,c,0,0);}
    private void Text(Rect r,string value,GUIStyle style){Idas3MenuLocalization.Label(r,value,style);}
    private void Button(Rect r,string value){Fill(r,new Color(.10f,.13f,.18f,.97f));GUI.DrawTexture(r,Texture2D.whiteTexture,ScaleMode.StretchToFill,true,0,new Color(.65f,.71f,.79f),1.2f*ui,7*ui);Text(r,value,label);}
    private static string Name(string id){switch(id){case "STICK":return "STEERING STICK";case "L2":return "BRAKE";case "R2":return "ACCELERATOR";case "L1":return "SHIFT DOWN";case "R1":return "SHIFT UP";case "A":return "CONFIRM";case "B":return "BACK";default:return id;}}
    private void OnGUI(){
        if(!IsOpen)return;Geometry();GUI.depth=-25000;Matrix4x4 matrix=GUI.matrix;Color color=GUI.color;GUI.matrix=Matrix4x4.identity;GUI.color=Color.white;
        if(label==null){label=new GUIStyle(GUI.skin.label){alignment=TextAnchor.MiddleCenter,fontStyle=FontStyle.Bold};title=new GUIStyle(label);}
        label.fontSize=Mathf.Max(12,Mathf.RoundToInt(17*ui));label.normal.textColor=new Color(.85f,.89f,.95f);title.fontSize=Mathf.RoundToInt(23*ui);title.normal.textColor=Color.white;
        Fill(new Rect(0,0,Screen.width,Screen.height),new Color(.025f,.035f,.05f));
        Button(Top(0),"CANCEL");Button(Top(1),"SAVE / DONE");Button(Top(2),"EXPORT");Button(Top(5),"IMPORT");Button(Top(3),"RESET");Button(Top(4),working.snap?"SNAP TO GRID: ON":"SNAP TO GRID: OFF");
        Text(new Rect(safe.x+780*ui,safe.y+12*ui,safe.width-780*ui,46*ui),"TOUCH LAYOUT",title);
        Text(new Rect(safe.x,safe.y+62*ui,safe.width,22*ui),"Drag a control to move it. Tap to select, then adjust its size.",label);
        Fill(preview,new Color(.045f,.063f,.09f));
        GUI.matrix=Matrix4x4.TRS(new Vector3(preview.x-safe.x*previewScale,preview.y-safe.y*previewScale,0),Quaternion.identity,new Vector3(previewScale,previewScale,1));
        if(working.snap){float grid=8*controls.LayoutUnit;for(int i=0;i*grid<safe.width;i++)Fill(new Rect(safe.x+i*grid,safe.y,1/previewScale,safe.height),new Color(1,1,1,i%4==0 ? .1f : .04f));for(int i=0;i*grid<safe.height;i++)Fill(new Rect(safe.x,safe.y+i*grid,safe.width,1/previewScale),new Color(1,1,1,i%4==0 ? .1f : .04f));}
        controls.DrawControls(selected,true);GUI.matrix=Matrix4x4.identity;
        Button(SizeMinus,"−");Button(SizePlus,"+");
        Text(new Rect(safe.x,safe.yMax-109*ui,230*ui,28*ui),Name(selected),label);
        Fill(new Rect(sizeSlider.x,sizeSlider.center.y-3*ui,sizeSlider.width,6*ui),new Color(.2f,.25f,.33f));float x=Mathf.Lerp(sizeSlider.x,sizeSlider.xMax,(working.Get(selected).size-50)/130f);
        Fill(new Rect(x-6*ui,sizeSlider.y,12*ui,sizeSlider.height),new Color(1,.75f,.41f));
        Text(new Rect(sizeSlider.xMax+66*ui,sizeSlider.y,120*ui,sizeSlider.height),working.Get(selected).size+"%",label);
        Text(new Rect(safe.x,safe.yMax-70*ui,230*ui,28*ui),"OPACITY",label);Button(OpacityMinus,"−");Button(OpacityPlus,"+");Text(new Rect(safe.x+284*ui,safe.yMax-70*ui,80*ui,28*ui),working.opacity+"%",label);
        Text(new Rect(safe.x+440*ui,safe.yMax-70*ui,safe.width-440*ui,28*ui),"SAVE keeps changes. CANCEL returns to the saved layout.",label);
        Text(new Rect(safe.x,safe.yMax-34*ui,safe.width,24*ui),notice,label);
        if(resetConfirmation){Fill(new Rect(0,0,Screen.width,Screen.height),new Color(0,0,0,.8f));Fill(new Rect(safe.center.x-190*ui,safe.center.y-80*ui,380*ui,166*ui),new Color(.06f,.08f,.12f));Text(new Rect(safe.center.x-180*ui,safe.center.y-60*ui,360*ui,70*ui),"Restore the default touch layout?\nSAVE keeps it; CANCEL discards it.",label);Button(ResetYes,"RESTORE");Button(ResetNo,"BACK");}
        GUI.matrix=matrix;GUI.color=color;
    }
    private void OnApplicationFocus(bool focused){if(!focused)finger=-1;}
    private void OnApplicationPause(bool paused){if(paused)finger=-1;}
    private void OnDisable(){if(IsOpen)Close(false);}
}
