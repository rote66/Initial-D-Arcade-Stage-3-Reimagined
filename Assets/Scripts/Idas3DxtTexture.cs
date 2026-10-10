using System;
using System.IO;
using UnityEngine;

// Preserve authored DDS colours, alpha and mipmaps on GPUs without BC1/BC3.
internal static class Idas3DxtTexture
{
    private static Color32 Rgb565(ushort value)=>new Color32(
        (byte)(((value>>11)*255+15)/31),
        (byte)((((value>>5)&63)*255+31)/63),
        (byte)(((value&31)*255+15)/31),255);
    private static Color32 Mix(Color32 a,Color32 b,int wa,int wb,int divisor)=>new Color32(
        (byte)((a.r*wa+b.r*wb)/divisor),(byte)((a.g*wa+b.g*wb)/divisor),
        (byte)((a.b*wa+b.b*wb)/divisor),255);

    internal static Color32[] DecodeMip(byte[] data,ref int offset,int width,int height,bool dxt5)
    {
        var pixels=new Color32[checked(width*height)];
        var colours=new Color32[4];var alpha=new byte[8];
        int blockSize=dxt5?16:8;
        for(int by=0;by<height;by+=4)for(int bx=0;bx<width;bx+=4){
            if(offset<0||offset>data.Length-blockSize)throw new InvalidDataException("Truncated DDS mipmap.");
            ulong alphaIndices=0;int colourOffset=offset;
            if(dxt5){
                alpha[0]=data[offset];alpha[1]=data[offset+1];
                if(alpha[0]>alpha[1])for(int i=1;i<=6;++i)alpha[i+1]=(byte)(((7-i)*alpha[0]+i*alpha[1])/7);
                else {
                    for(int i=1;i<=4;++i)alpha[i+1]=(byte)(((5-i)*alpha[0]+i*alpha[1])/5);
                    alpha[6]=0;alpha[7]=255;
                }
                for(int i=0;i<6;++i)alphaIndices|=(ulong)data[offset+2+i]<<(8*i);
                colourOffset+=8;
            }
            ushort c0=BitConverter.ToUInt16(data,colourOffset),c1=BitConverter.ToUInt16(data,colourOffset+2);
            colours[0]=Rgb565(c0);colours[1]=Rgb565(c1);
            if(dxt5||c0>c1){colours[2]=Mix(colours[0],colours[1],2,1,3);colours[3]=Mix(colours[0],colours[1],1,2,3);}
            else {colours[2]=Mix(colours[0],colours[1],1,1,2);colours[3]=new Color32(0,0,0,0);}
            uint indices=BitConverter.ToUInt32(data,colourOffset+4);
            for(int i=0;i<16;++i){
                int x=bx+(i&3),y=by+(i>>2);
                if(x>=width||y>=height)continue;
                Color32 colour=colours[(indices>>(2*i))&3];
                if(dxt5)colour.a=alpha[(alphaIndices>>(3*i))&7];
                pixels[y*width+x]=colour;
            }
            offset+=blockSize;
        }
        return pixels;
    }
}
