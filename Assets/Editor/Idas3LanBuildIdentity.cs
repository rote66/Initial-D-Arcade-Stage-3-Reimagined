using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class Idas3LanBuildIdentity
{
    public static void Generate()
    {
        string root=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
        // Source identity works with static native code and IL2CPP. Include all
        // gameplay code and original simulation data, excluding music/visuals,
        // build number, signing, and the optional startup ownership check.
        var files=Directory.GetFiles(Path.Combine(root,"Assets/Scripts"),"*.cs",SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(root,"Native/src"),"*",SearchOption.AllDirectories)
                .Where(p=>new[]{".cpp",".h",".inl"}.Contains(Path.GetExtension(p))))
            .Concat(Directory.GetFiles(Path.Combine(root,"Native/data/original_physics"),"*",SearchOption.AllDirectories)
                .Where(p=>!p.EndsWith(".meta")&&!p.EndsWith(".md")&&!p.EndsWith(".json")))
            .Concat(new[]{Path.Combine(root,"Native/portable_plugin.cmake")})
            .OrderBy(p=>Path.GetRelativePath(root,p),StringComparer.Ordinal);
        using(var hash=SHA256.Create())using(var content=new MemoryStream()){
            foreach(string path in files){
                byte[] name=Encoding.UTF8.GetBytes(Path.GetRelativePath(root,path).Replace('\\','/')+"\0");
                content.Write(name,0,name.Length);
                using(var file=File.OpenRead(path)){var digest=hash.ComputeHash(file);content.Write(digest,0,digest.Length);}
            }
            string fingerprint=BitConverter.ToString(hash.ComputeHash(content.ToArray())).Replace("-","").ToLowerInvariant();
            const string asset="Assets/Resources/Idas3LanBuildIdentity.txt";
            File.WriteAllText(Path.Combine(root,asset),fingerprint+"\n");AssetDatabase.ImportAsset(asset);
            Debug.Log("iOS LAN source identity: "+fingerprint);
        }
    }
}
