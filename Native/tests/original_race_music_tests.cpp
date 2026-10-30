#include "race_music_selection.h"
#include <iostream>

using namespace idas3;
namespace {
unsigned checks=0;
void check(bool ok,const char* reason){++checks;if(!ok)throw std::runtime_error(reason);}
struct PrivateSave {
    std::filesystem::path path=std::filesystem::temp_directory_path()/
        ("idas3-default-music-"+std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
    ~PrivateSave(){std::error_code error;std::filesystem::remove_all(path,error);}
};
}
int main()try{
    AutomaticRaceMusic music(1234);
    auto profile=original::makeOriginalFreshBattleProfile();
    constexpr std::array<unsigned,31> rivalSongs{
        1,1,1,2,2,2,3,3,3,4,4,4,5,5,6,6,6,5,7,7,7,8,8,8,9,9,10,10,11,11,5};
    for(unsigned rival=0;rival<31;++rival)for(unsigned weather=0;weather<2;++weather){
        original::selectOriginalRival(profile,rival);profile.setu(32,weather);
        check(music.select(originalRaceMusicSelection,profile,true)==int(rivalSongs[rival]),"Wrong original rival song");
        check(original::originalLegendRaceMusic(rival)+1==rivalSongs[rival],"Legend last-song mapping changed");
    }
    constexpr std::array<unsigned,9> day{1,2,3,4,7,6,8,9,5},night{1,2,3,5,7,6,8,10,5};
    for(unsigned mode:{1u,2u,4u})for(unsigned course=0;course<9;++course)
        for(unsigned time=0;time<2;++time)for(unsigned direction=0;direction<2;++direction)
            for(unsigned weather=0;weather<2;++weather){
                profile.setu(0,mode);profile.setu(4,course);profile.setu(8,time);
                profile.setu(12,direction);profile.setu(32,weather);
                check(music.select(originalRaceMusicSelection,profile,true)==int((time?night:day)[course]),"Wrong original course/time song");
                check(music.select(26,profile,true)==26&&music.select(-2,profile,true)==-2,"Original default replaced explicit music");
            }
    // Card OFF is still interpreted by EngineAudio, not rewritten by selection.
    profile.setByte(1184,1);profile.setu(4,3);profile.setu(8,1);
    check(music.select(originalRaceMusicSelection,profile,true)==5&&profile.byte(1184)==1,"Default rewrote card OFF");
    profile.setByte(1184,2);
    for(unsigned song=0;song<12;++song){
        profile.setu(1136,song);
        check(music.select(originalRaceMusicSelection,profile,true)==int(song+1),"Default lost original card FIX");
    }
    profile.setu(1136,0xffffffffu);
    check(music.select(originalRaceMusicSelection,profile,true)==5,"Invalid card song did not fall back to course");
    profile.setByte(1184,0);
    AutomaticRaceMusic imported(345),random(345);
    for(unsigned i=0;i<100;++i)
        check(imported.select(originalRaceMusicSelection,profile,false)==random.select(-1),"Imported course falsely used Akina default");
    AutomaticRaceMusic untouched(456),control(456);
    for(unsigned i=0;i<100;++i)untouched.select(originalRaceMusicSelection,profile,true);
    check(untouched.select(-1)==control.select(-1),"Default consumed AUTO random state");
    PrivateSave save;
    for(int selected:{26,-1,originalRaceMusicSelection,116,originalRaceMusicSelection}){
        saveRaceMusicSelection(save.path,selected);
        check(loadRaceMusicSelection(save.path,7)==selected,"Music policy did not survive save/reload");
    }
    {std::ofstream old(save.path/"race_music.txt");old<<"IDAS3_RACE_MUSIC_V1\ndefault\n";}
    check(loadRaceMusicSelection(save.path,7)==-1,"Existing AUTO preference silently changed");
    check(validRaceMusicMetadata(originalRaceMusicSelection)&&validRaceMusicSelection(originalRaceMusicSelection),"Original default not accepted");
    for(int invalid:{0,-2,-4,117}){
        check(!validRaceMusicSelection(invalid),"Invalid music choice accepted");
        bool rejected=false;
        try{saveRaceMusicSelection(save.path,invalid);}catch(const std::invalid_argument&){rejected=true;}
        check(rejected&&loadRaceMusicSelection(save.path,7)==-1,"Invalid choice overwrote saved policy");
    }
    std::cout<<"PASS "<<checks<<" original music checks: 31 rivals, 9 courses, mode/time/direction/weather, FIX/OFF, imported fallback, independent AUTO and persisted policies\n";
}catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}
