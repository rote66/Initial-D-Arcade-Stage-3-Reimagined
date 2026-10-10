#include "original_audio.h"
#include "music_catalog.h"
#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <fstream>
#include <iostream>
#include <stdexcept>

namespace fs=std::filesystem;
unsigned checks=0;
void check(bool ok,const std::string& why){++checks;if(!ok)throw std::runtime_error(why);}
template<class F>void rejects(F operation,const char* why){bool rejected=false;try{operation();}catch(const std::runtime_error&){rejected=true;}check(rejected,why);}

int main(int argc,char** argv){
    if(argc!=3)return 2;
    const auto temp=fs::temp_directory_path()/("idas3-mp3-check-"+std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
    fs::create_directories(temp);
    try{
        const auto original=fs::path(argv[1])/"data/original_audio/streams",compressed=fs::path(argv[2])/"data/original_audio/streams";
        for(const auto& track:idas3::raceMusicCatalog){
            const std::string label=track.id;auto mp3=compressed/track.relativePath;mp3.replace_extension(".mp3");
            check(fs::is_regular_file(mp3)&&!fs::exists(compressed/track.relativePath),label+": package must replace the original full song");
            const auto source=idas3::loadOriginalSpsd(original/track.relativePath);
            const auto clip=idas3::loadOriginalSpsd(compressed/track.relativePath);
            check(clip.sampleRate==44100&&clip.channels==source.channels,label+": MP3 rate/channels");
            check(std::abs(double(clip.frames())-double(source.frames())*44100/source.sampleRate)<=2,label+": encoder delay/padding or source duration changed");
            check(clip.looping==source.looping,label+": source loop enable changed");
            const auto expectedStart=source.looping?std::size_t(std::llround(double(source.loopStart)*44100/source.sampleRate)):0;
            const auto sourceEnd=source.loopEnd?source.loopEnd:source.frames();
            const auto expectedEnd=sourceEnd==source.frames()?clip.frames():std::size_t(std::llround(double(sourceEnd)*44100/source.sampleRate));
            check(clip.loopStart==expectedStart&&clip.loopEnd==expectedEnd,label+": loop time changed after resampling");
            const auto bounds=std::minmax_element(clip.samples.begin(),clip.samples.end());
            check(*bounds.second-*bounds.first>1024,label+": MP3 is silent");
            const auto direct=idas3::loadOriginalSpsd(mp3);
            check(direct.samples==clip.samples&&direct.loopStart==clip.loopStart,label+": direct MP3 and legacy catalog resolution differ");
        }
        auto first=compressed/idas3::raceMusicCatalog.front().relativePath;first.replace_extension(".mp3");
        std::ifstream input(first,std::ios::binary);std::vector<std::uint8_t> data((std::istreambuf_iterator<char>(input)),{});
        const auto decoded=idas3::decodeOriginalSpsd(data);
        check(decoded.frames()>0,"MP3 signature dispatch without a fixed extension");
        fs::copy_file(first,temp/"song.mp3");std::ofstream(temp/"song.mp3.idasaudio",std::ios::binary)<<"invalid";
        rejects([&]{idas3::loadOriginalSpsd(temp/"song.wav");},"Malformed loop sidecar accepted");
        rejects([&]{idas3::decodeOriginalSpsd(std::span(data).first(data.size()/2));},"Truncated MP3 accepted");
        const std::array<std::uint8_t,6> invalid{'I','D','3',0,0,0};
        rejects([&]{idas3::decodeOriginalSpsd(invalid);},"Invalid MP3 header accepted");
        std::cout<<"PASS compressed music: "<<checks<<" checks; all 117 original/MP3 pairs, gapless lengths, loop times, same-stem resolution and malformed/truncated input\n";
        fs::remove_all(temp);return 0;
    }catch(const std::exception& error){std::cerr<<error.what()<<'\n';fs::remove_all(temp);return 1;}
}
