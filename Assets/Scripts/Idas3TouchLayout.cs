using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Like the supplied NFS3 Android overlay, centers are stored as distances from
// their nearest edges in layout units. Size controls both artwork and hit bounds.
public sealed class Idas3TouchLayout
{
    internal static readonly string[] Ids={"STICK","L2","R2","L1","R1","VIEW","LIGHT","START","B"};
    [Serializable] public sealed class Control {
        public string id;public bool positioned,fromRight,fromBottom;public float dx,dy;public int size=100;
        public Control Clone()=>(Control)MemberwiseClone();
    }
    [Serializable] public sealed class Values {
        public string schema="idas3-touch-layout";public int version=1;public bool snap=true;public int opacity=65;public Control[] controls;
        public Values Clone(){var v=(Values)MemberwiseClone();v.controls=Array.ConvertAll(controls,c=>c.Clone());return v;}
        public Control Get(string id){foreach(var c in controls)if(c.id==id)return c;throw new ArgumentException("Unknown touch control: "+id);}
    }
    public Values Current {get;private set;}=Defaults();
    public string FilePath {get;private set;}
    public string ExportDirectory {get;private set;}
    public string LastError {get;private set;}
    internal static Values Defaults(){return new Values{controls=Array.ConvertAll(Ids,id=>new Control{id=id})};}
    public void Initialize(string saveRoot,string exportRoot=null){
        FilePath=Path.Combine(saveRoot,"touch-layout.json");ExportDirectory=exportRoot??Path.Combine(Application.persistentDataPath,"Touch Layouts");Current=Defaults();LastError=null;
        if(File.Exists(FilePath))try{Current=Parse(File.ReadAllText(FilePath));}catch(Exception e){LastError="Could not load touch layout: "+e.Message;}
    }
    private static bool Finite(float x)=>!float.IsNaN(x)&&!float.IsInfinity(x);
    internal static Values Parse(string json){
        if(json==null||json.Length>65536)throw new InvalidDataException("Invalid touch layout file.");
        var value=JsonUtility.FromJson<Values>(json);
        if(value==null||value.schema!="idas3-touch-layout"||value.version!=1||value.controls==null||value.controls.Length>64)throw new InvalidDataException("Unsupported touch layout format.");
        return Normalize(value);
    }
    internal static Values ReadImport(string file){
        using(var stream=File.OpenRead(file)){
            if(stream.Length>65536)throw new InvalidDataException("Touch layout file is too large.");
            using(var reader=new StreamReader(stream))return Parse(reader.ReadToEnd());
        }
    }
    internal static Values Normalize(Values value){
        var result=Defaults();result.snap=value.snap;result.opacity=Mathf.Clamp(value.opacity,20,100);var seen=new HashSet<string>();
        foreach(var c in value.controls??Array.Empty<Control>()){
            if(c==null||(c.id!="MENU"&&c.id!="A"&&Array.IndexOf(Ids,c.id)<0)||!seen.Add(c.id)||!Finite(c.dx)||!Finite(c.dy))throw new InvalidDataException("Invalid or duplicate touch control.");
            // Older saved/exported layouts include retired menu/confirm buttons.
            // Drop that entry while preserving every surviving control.
            if(c.id=="MENU"||c.id=="A")continue;
            var to=result.Get(c.id);to.positioned=c.positioned;to.fromRight=c.fromRight;to.fromBottom=c.fromBottom;
            to.dx=Mathf.Clamp(c.dx,0,10000);to.dy=Mathf.Clamp(c.dy,0,10000);to.size=Mathf.Clamp(c.size,50,180);
        }
        return result;
    }
    internal static Rect Place(Values values,string id,Rect original,Rect safe,float unit){
        var entry=values.Get(id);Vector2 size=original.size*(entry.size/100f);size.x=Mathf.Min(size.x,safe.width);size.y=Mathf.Min(size.y,safe.height);
        Vector2 center=original.center;
        if(entry.positioned)center=new Vector2(entry.fromRight?safe.xMax-entry.dx*unit:safe.x+entry.dx*unit,entry.fromBottom?safe.yMax-entry.dy*unit:safe.y+entry.dy*unit);
        center.x=Mathf.Clamp(center.x,safe.x+size.x/2,safe.xMax-size.x/2);center.y=Mathf.Clamp(center.y,safe.y+size.y/2,safe.yMax-size.y/2);
        return new Rect(center-size/2,size);
    }
    internal static void Position(Values values,string id,Vector2 center,Rect safe,float unit,bool snap){
        var entry=values.Get(id);var point=(center-safe.position)/unit;
        if(snap){point.x=Mathf.Round(point.x/8)*8;point.y=Mathf.Round(point.y/8)*8;}
        entry.positioned=true;entry.fromRight=point.x>safe.width/unit/2;entry.fromBottom=point.y>safe.height/unit/2;
        entry.dx=Mathf.Max(0,entry.fromRight?safe.width/unit-point.x:point.x);entry.dy=Mathf.Max(0,entry.fromBottom?safe.height/unit-point.y:point.y);
    }
    public bool Save(Values draft){
        LastError=null;try{var value=Normalize(draft);AtomicWrite(FilePath,JsonUtility.ToJson(value,true));Current=value;return true;}
        catch(Exception e){LastError="Could not save touch layout: "+e.Message;return false;}
    }
    public string Export(Values layout){
        var value=Normalize(layout);string stem="touch-layout-"+DateTime.Now.ToString("yyyyMMdd-HHmmss");string file=Path.Combine(ExportDirectory,stem+".json");
        for(int i=1;File.Exists(file);++i)file=Path.Combine(ExportDirectory,stem+"-"+i+".json");
        AtomicWrite(file,JsonUtility.ToJson(value,true));return file;
    }
    private static void AtomicWrite(string file,string json){
        Directory.CreateDirectory(Path.GetDirectoryName(file));string temporary=file+".tmp";File.WriteAllText(temporary,json);
        if(File.Exists(file))File.Replace(temporary,file,null);else File.Move(temporary,file);
    }
}
