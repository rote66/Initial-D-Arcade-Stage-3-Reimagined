#include "unity_bridge.h"
#include "unity_scene_capture.h"
#include "scene_matrix.h"
#include "race.h"
#include <filesystem>
#include <fstream>
#include <iostream>
#include <bit>
#include <sstream>
#include <chrono>

extern "C" int Idas3SceneGetFrame(Idas3SceneFrame*);
extern "C" int Idas3SceneModeFlowFixture(int);
namespace fs=std::filesystem;
void check(bool ok,const char* message){
    if(ok)return;
    char error[2048]{};Idas3UnityCopyError(error,sizeof(error));
    throw std::runtime_error(std::string(message)+": "+error);
}
float projectDepth(const idas3::portable::Matrix& p,float z){return (z*p[10]+p[14])/(z*p[11]);}
int main(int argc,char** argv){
    if(argc!=2)return 2;
    const auto temp=fs::temp_directory_path()/("idas3-ios-portable-"+std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
    fs::create_directories(temp/"userdata");
    try{
        for(bool lh:{false,true}){
            auto p=idas3::portable::perspective(1.f,16.f/9.f,.1f,2000.f,lh);
            const float sign=lh?1.f:-1.f;
            check(std::abs(projectDepth(p,.1f*sign))<.00001f,"Near plane must map to D3D zero depth");
            check(std::abs(projectDepth(p,2000.f*sign)-1)<.00001f,"Far plane must map to D3D one depth");
            auto view=idas3::portable::lookAt({1,2,3},{1,2,4},{0,1,0},lh);
            check(std::abs(view[0]+view[4]*2+view[8]*3+view[12])<.00001f,"View must center eye X");
            check(std::abs(view[1]+view[5]*2+view[9]*3+view[13])<.00001f,"View must center eye Y");
        }
        idas3::Replay replay;
        const float speed=std::bit_cast<float>(0x42f6e979u),yaw=-.123456789f;
        replay.record(1,{12345.678f,-.0000001234f,99.00001f},yaw,speed,3);
        const auto file=temp/"roundtrip.csv";check(replay.save(file.string()),"Replay save");
        std::ifstream csv(file);std::string line;std::getline(csv,line);std::getline(csv,line);
        std::replace(line.begin(),line.end(),',',' ');std::istringstream values(line);values.imbue(std::locale::classic());
        unsigned tick,gear;float rs,ry,x,y,z;values>>tick>>rs>>ry>>x>>y>>z>>gear;
        check(rs==speed&&ry==yaw&&x==12345.678f&&y==-.0000001234f&&z==99.00001f&&gear==3,"iOS-compatible replay floats must round trip");
        std::ofstream(temp/"ISOLATED_MODE_FLOW_TEST.txt")<<"portable iOS core check\n";
        check(Idas3SceneInitialize(argv[1],(temp/"userdata").c_str(),1280,720)==1,"Scene initialize");
        Idas3RaceMusicState music{};music.size=sizeof(music);
        check(Idas3SceneGetRaceMusicState(&music)==1,"Music policy state");
        const int previousMusic=music.activeIndex;
        check(Idas3SceneSetRaceMusicTrack(26,1)==1&&Idas3SceneSetRaceMusicTrack(-3,1)==1,"Restore default after explicit song");
        check(Idas3SceneGetRaceMusicState(&music)==1&&music.selectedIndex==-3&&music.activeIndex==previousMusic,"Default policy changed current audio");
        char musicText[64]{};
        check(Idas3SceneCopyRaceMusicText(-3,0,musicText,sizeof(musicText))==8&&std::string(musicText)=="original","Original policy metadata");
        check(Idas3SceneCopyRaceMusicText(-2,0,musicText,sizeof(musicText))==-1,"Custom PCM became a catalog choice");
        check(Idas3SceneShutdown()==1,"Shutdown before default restore");
        check(Idas3SceneInitialize(argv[1],(temp/"userdata").c_str(),1280,720)==1,"Reinitialize after default restore");
        check(Idas3SceneGetRaceMusicState(&music)==1&&music.selectedIndex==-3,"Original policy did not survive native restart");
        Idas3UnityInput input{};input.size=sizeof(input);input.flags=1;input.deltaSeconds=1./60.;input.width=1280;input.height=720;
        for(int i=0;i<3;++i)check(Idas3SceneStep(&input)==1,"Title step");
        check(Idas3SceneModeFlowFixture(240)==1,"Time Attack driving fixture");
        input.padConnected=1;input.rightTrigger=255;
        for(int i=0;i<420;++i)check(Idas3SceneStep(&input)==1,"Touch-compatible gas input");
        Idas3UnityStatus status{};status.size=sizeof(status);check(Idas3UnityGetStatus(&status)==1,"Status");
        std::cout<<"Driving status: speed="<<status.speedMetresPerSecond<<" phase="<<status.racePhase<<" flags="<<status.flags<<" ticks="<<status.simulationTicks<<"\n";
        check(status.speedMetresPerSecond>0&&std::isfinite(status.speedMetresPerSecond),"Gas must drive the car");
        check(Idas3SceneGetRaceMusicState(&music)==1&&music.selectedIndex==-3&&music.activeIndex>0&&music.activeIndex<13,"Default race did not resolve to D3 music");
        check(Idas3SceneSetRaceMusicTrack(-3,0)==0,"Default policy bypassed opponent race guard");
        const int activeMusic=music.activeIndex;
        check(Idas3SceneSetRaceMusicTrack(26,1)==1&&Idas3SceneGetRaceMusicState(&music)==1&&music.selectedIndex==26&&music.activeIndex==activeMusic,"Covered idle lobby changed active race music");
        check(Idas3SceneSetRaceMusicTrack(-3,1)==1&&Idas3SceneGetRaceMusicState(&music)==1&&music.selectedIndex==-3&&music.activeIndex==activeMusic,"Queued default changed active race music");
        Idas3SceneFrame frame{};frame.size=sizeof(frame);check(Idas3SceneGetFrame(&frame)==1,"Captured scene");
        check(frame.rangeCount>0&&frame.vertexCount>0&&frame.textureCount>0,"Scene must publish meshes and textures");
        for(unsigned i=0;i<16;++i)check(std::isfinite(frame.frameConstants[i]),"Portable projection must be finite");
        for(unsigned i=0;i<frame.rangeCount;++i)check(frame.ranges[i].count%3==0,"Metal primitive stream requires triangles");
        check(Idas3SceneSetPaused(1)==1,"Background pause");
        check(Idas3SceneFlushSaves()==1,"Background save flush");
        check(fs::is_regular_file(temp/"userdata/settings.txt"),"Background settings must reach disk before shutdown");
        check(Idas3SceneStep(&input)==1,"Paused step");
        check(Idas3SceneSetPaused(0)==1,"Resume");
        check(Idas3SceneShutdown()==1,"Shutdown/save");
        std::cout<<"PASS: projection, replay float round trip, resource startup, original music ABI/restart/race guard, driving input, scene triangles, pause and save\n";
        fs::remove_all(temp);return 0;
    }catch(const std::exception& error){
        Idas3SceneShutdown();std::cerr<<error.what()<<"\nFixtures retained: "<<temp<<"\n";return 1;
    }
}
