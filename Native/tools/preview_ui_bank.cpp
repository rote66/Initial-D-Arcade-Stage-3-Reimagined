#include "native_assets.h"
#include <algorithm>
#include <cmath>
#include <fstream>
#include <iostream>
#include <limits>

int main(int argc,char** argv)try{
    if(argc!=4&&argc!=5)throw std::runtime_error("Usage: preview_ui_bank model textures output-prefix [language]");
    if(argc==5)idas3::setOriginalUiLanguage(std::stoi(argv[4]));
    const auto model=idas3::NativeModel::load(argv[1]);
    const auto textures=idas3::NativeTextureBank::load(argv[2]);
    constexpr int w=1250,tileW=250,tileH=100;
    for(unsigned first=0;first<model.chunks.size();first+=60){
        const unsigned count=std::min(60u,unsigned(model.chunks.size())-first);
        const int h=int((count+4)/5)*tileH;
        std::vector<std::uint32_t> image(w*h,0xff303238);
        for(unsigned n=0;n<count;++n){
            const auto& chunk=model.chunks[first+n];
            float l=1e9f,r=-1e9f,t=1e9f,b=-1e9f;
            for(const auto& batch:chunk.batches)for(const auto& v:batch.vertices){
                l=std::min(l,v.position.x);r=std::max(r,v.position.x);
                t=std::min(t,-v.position.y);b=std::max(b,-v.position.y);
            }
            if(r<=l||b<=t)continue;
            idas3::SpritePlacement p;p.softwareOnly=true;p.invertY=true;p.authoredHeight=0;
            p.defaultOriginalUiColors=true;
            p.scale=std::min(240.f/(r-l),74.f/(b-t));
            p.offsetX=float((n%5)*tileW)+5.f-l*p.scale;
            p.offsetY=float((n/5)*tileH)+22.f-t*p.scale;
            idas3::compositeOriginalMenuChunk(image,w,h,textures,chunk,p);
        }
        const auto file=std::string(argv[3])+"-"+std::to_string(first/60)+".bmp";
        std::ofstream out(file,std::ios::binary);
        const auto u16=[&](unsigned v){out.put(char(v));out.put(char(v>>8));};
        const auto u32=[&](std::uint32_t v){u16(v&65535);u16(v>>16);};
        u16(0x4d42);u32(54+w*h*4);u32(0);u32(54);u32(40);u32(w);u32(std::uint32_t(-h));
        u16(1);u16(32);u32(0);u32(w*h*4);u32(0);u32(0);u32(0);u32(0);
        out.write(reinterpret_cast<const char*>(image.data()),std::streamsize(image.size()*4));
        if(!out)throw std::runtime_error("Cannot write bank preview");
    }
    return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
