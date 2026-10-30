#pragma once
#include "original_battle_profile.h"
#include <array>
#include <stdexcept>

namespace idas3::original {
// Source music IDs are zero-based; the host catalog keeps attract music at0.
// Shared with0F0760's last-song update instead of duplicating its rival table.
inline unsigned originalLegendRaceMusic(unsigned enemy){
    constexpr std::array<unsigned,31> songs{
        0,0,0,1,1,1,2,2,2,3,3,3,4,4,5,5,5,4,6,6,6,7,7,7,8,8,9,9,10,10,4};
    return songs.at(enemy);
}
inline unsigned originalDefaultRaceMusic(const OriginalBattleProfile& profile){
    if(profile.byte(1184)==2&&profile.u(1136)<12)return profile.u(1136);
    if(profile.u(0)==0)return originalLegendRaceMusic(profile.u(24));
    constexpr std::array<unsigned,9> courses{0,1,2,3,6,5,7,8,4};
    const auto course=profile.u(4);
    if(course==3&&profile.u(8))return 4;
    if(course==7&&profile.u(8))return 9;
    return courses.at(course);
}
}
