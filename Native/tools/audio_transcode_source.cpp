// Build-time source export and production-decoder inspection. No game saves.
#include "original_audio.h"
#include <algorithm>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>

int main(int argc,char** argv){try{
    if(argc!=3&&argc!=4)throw std::runtime_error("Usage: audio_transcode_source inspect INPUT | decode INPUT OUTPUT.wav");
    const std::string mode=argv[1];
    const auto clip=idas3::loadOriginalSpsd(argv[2]);
    if(mode=="decode"&&argc==4){
        std::ofstream out(argv[3],std::ios::binary);if(!out)throw std::runtime_error("Cannot write PCM export");
        const auto word=[&](std::uint32_t v,int size){for(int i=0;i<size;++i)out.put(char((v>>(8*i))&255));};
        const auto bytes=std::uint32_t(clip.samples.size()*2);
        out.write("RIFF",4);word(bytes+36,4);out.write("WAVEfmt ",8);word(16,4);word(1,2);word(clip.channels,2);
        word(clip.sampleRate,4);word(clip.sampleRate*clip.channels*2,4);word(clip.channels*2,2);word(16,2);
        out.write("data",4);word(bytes,4);for(auto sample:clip.samples)word(std::uint16_t(sample),2);
        if(!out)throw std::runtime_error("PCM export failed");
    }else if(mode!="inspect"||argc!=3)throw std::runtime_error("Invalid audio export arguments");
    const auto [lo,hi]=std::minmax_element(clip.samples.begin(),clip.samples.end());
    std::cout<<"{\"sampleRate\":"<<clip.sampleRate<<",\"channels\":"<<clip.channels<<",\"frames\":"<<clip.frames()
        <<",\"looping\":"<<(clip.looping?"true":"false")<<",\"loopStart\":"<<clip.loopStart
        <<",\"loopEnd\":"<<(clip.loopEnd?clip.loopEnd:clip.frames())<<",\"minSample\":"<<*lo<<",\"maxSample\":"<<*hi<<"}\n";
    return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
