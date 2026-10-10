using System;
using System.IO;
using UnityEngine;

internal static class Idas3DxtTextureChecks
{
    internal static void Run(Action<bool,string> check)
    {
        // BC1 red/green endpoints and all four indices, followed by a blue mip.
        byte[] data={0,248,224,7,228,228,228,228,31,0,0,0,0,0,0,0};
        int offset=0;var pixels=Idas3DxtTexture.DecodeMip(data,ref offset,4,4,false);
        Color32[] expected={new Color32(255,0,0,255),new Color32(0,255,0,255),
            new Color32(170,85,0,255),new Color32(85,170,0,255)};
        for(int i=0;i<16;++i){var actual=pixels[i];var colour=expected[i%4];
            check(actual.r==colour.r&&actual.g==colour.g&&actual.b==colour.b&&actual.a==255,"BC1 authored colour/index "+i);}
        pixels=Idas3DxtTexture.DecodeMip(data,ref offset,2,2,false);
        check(offset==16&&pixels.Length==4,"DDS mip chain offset and extent");
        foreach(var colour in pixels)check(colour.b==255&&colour.r==0&&colour.g==0&&colour.a==255,"DDS second mip retains blue");
        byte[] transparent={0,0,255,255,255,255,255,255};offset=0;
        pixels=Idas3DxtTexture.DecodeMip(transparent,ref offset,3,2,false);
        check(pixels.Length==6&&Array.TrueForAll(pixels,c=>c.a==0),"BC1 cutout alpha and partial block extent");
        bool rejected=false;offset=0;
        try{Idas3DxtTexture.DecodeMip(new byte[7],ref offset,4,4,false);}catch(InvalidDataException){rejected=true;}
        check(rejected,"Truncated compressed mip is rejected");
        // BC3 both alpha interpolation modes, independent of RGB endpoints.
        foreach(bool seven in new[]{false,true}){
            var block=new byte[16];block[0]=seven?(byte)255:(byte)0;block[1]=seven?(byte)0:(byte)255;
            block[8]=block[10]=31;ulong indices=0;
            for(int i=0;i<16;++i)indices|=(ulong)(i%8)<<(3*i);
            for(int i=0;i<6;++i)block[2+i]=(byte)(indices>>(8*i));
            byte[] alpha=seven?new byte[]{255,0,218,182,145,109,72,36}:new byte[]{0,255,51,102,153,204,0,255};
            offset=0;pixels=Idas3DxtTexture.DecodeMip(block,ref offset,4,4,true);
            for(int i=0;i<16;++i)check(pixels[i].b==255&&pixels[i].a==alpha[i%8],"BC3 alpha palette "+seven+" / "+i);
        }
    }
}
