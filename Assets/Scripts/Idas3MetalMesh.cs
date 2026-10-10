using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

internal static class Idas3MetalMesh
{
    // Imported paired leaf/guardrail faces need a constant authored face normal
    // for the direct Metal fragment pass. Split their shared vertex indices.
    internal static void PrepareImported(Mesh mesh)
    {
        if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Metal)return;
        var tags=mesh.uv3;bool paired=false;
        foreach(var tag in tags)if(tag.x>.5f){paired=true;break;}
        if(!paired)return;
        var source=mesh.vertices;var normals=mesh.normals;var colors=mesh.colors32;
        var uv=mesh.uv;var uv2=mesh.uv2;var indices=mesh.triangles;
        int count=indices.Length;
        var vertices=new Vector3[count];var ns=new Vector3[count];var cs=new Color32[count];
        var tex=new Vector2[count];var tex2=new Vector2[count];var flags=new Vector2[count];
        var faces=new List<Vector3>(count);var triangles=new int[count];
        for(int i=0;i<count;i+=3){
            Vector3 face=Vector3.Cross(source[indices[i+1]]-source[indices[i]],source[indices[i+2]]-source[indices[i]]);
            for(int j=0;j<3;++j){int at=i+j,index=indices[at];
                vertices[at]=source[index];ns[at]=normals[index];cs[at]=colors[index];
                tex[at]=uv[index];tex2[at]=uv2[index];flags[at]=tags[index];
                faces.Add(face);triangles[at]=at;
            }
        }
        mesh.vertices=vertices;mesh.normals=ns;mesh.colors32=cs;mesh.uv=tex;mesh.uv2=tex2;mesh.uv3=flags;
        mesh.SetUVs(3,faces);mesh.triangles=triangles;
    }
}
