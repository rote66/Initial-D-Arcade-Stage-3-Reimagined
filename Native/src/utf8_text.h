#pragma once
#include <cstdint>
#include <string>
#include <string_view>

namespace idas3 {
inline std::uint32_t nextUtf8Code(std::string_view text,std::size_t& at){
    if(at>=text.size())return 0;
    const auto first=static_cast<unsigned char>(text[at++]);
    if(first<128)return first;
    const unsigned extra=first>=0xc2&&first<=0xdf?1:first>=0xe0&&first<=0xef?2:first>=0xf0&&first<=0xf4?3:0;
    if(!extra||at+extra>text.size())return '?';
    std::uint32_t code=first&((1u<<(6-extra))-1u);
    for(unsigned n=0;n<extra;++n){
        const auto c=static_cast<unsigned char>(text[at+n]);
        if((c&0xc0)!=0x80)return '?';
        code=(code<<6)|(c&63);
    }
    if(code<(extra==1?128u:extra==2?2048u:65536u)||code>0x10ffff||(code>=0xd800&&code<=0xdfff))return '?';
    at+=extra;return code;
}
inline std::string utf8Code(std::uint32_t code){
    std::string out;
    if(code<0x80)out+=char(code);
    else if(code<0x800){out+=char(0xc0|(code>>6));out+=char(0x80|(code&63));}
    else if(code<0x10000){out+=char(0xe0|(code>>12));out+=char(0x80|((code>>6)&63));out+=char(0x80|(code&63));}
    else{out+=char(0xf0|(code>>18));out+=char(0x80|((code>>12)&63));out+=char(0x80|((code>>6)&63));out+=char(0x80|(code&63));}
    return out;
}
}
