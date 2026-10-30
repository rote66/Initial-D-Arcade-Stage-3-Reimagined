#include "native_assets.h"
#include "original_rival_dialog.h"
#include "original_rival_dialog_scene.h"
#include "original_gasstand_attract.h"
#include "original_vs_banner.h"
#include "original_battle_names.h"
#include "original_legend_menu.h"
#include "chinese_arcade_font.h"
#include "frontend.h"
#include "unity_ui_capture.h"
#include "utf8_text.h"
#include <algorithm>
#include <cmath>
#include <fstream>
#include <iostream>
#include <set>
#include <stdexcept>

using namespace idas3;
using namespace idas3::original;
namespace fs=std::filesystem;
namespace {
void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
void bitmap(const fs::path& path,const std::vector<std::uint32_t>& pixels){
    std::ofstream out(path,std::ios::binary);
    const auto u16=[&](unsigned n){out.put(char(n));out.put(char(n>>8));};
    const auto u32=[&](std::uint32_t n){u16(n&65535);u16(n>>16);};
    u16(0x4d42);u32(54+640*480*4);u32(0);u32(54);u32(40);u32(640);u32(std::uint32_t(-480));
    u16(1);u16(32);u32(0);u32(640*480*4);u32(0);u32(0);u32(0);u32(0);
    out.write(reinterpret_cast<const char*>(pixels.data()),pixels.size()*4);
}
void sameRecord(const OriginalRivalDialogRecord& a,const OriginalRivalDialogRecord& b){
    require(a.sourceRecord==b.sourceRecord&&a.sourceScript==b.sourceScript&&
        a.portraitCoordinates==b.portraitCoordinates&&a.portraitKinds==b.portraitKinds&&
        a.tokens.size()==b.tokens.size(),"Chinese changed script record provenance");
    for(unsigned i=0;i<a.tokens.size();++i){
        require(a.tokens[i].sourceAddress==b.tokens[i].sourceAddress&&a.tokens[i].bytes==b.tokens[i].bytes,
            "Chinese changed an authored command or source address");
        require(!b.tokens[i].localizedDisplay,"Chinese changed source-token annotations");
    }
    std::vector<std::pair<unsigned,std::string>> source,localized;
    for(const auto& token:a.tokens)if(token.bytes.front()!='*')
        source.emplace_back(token.sourceAddress,token.bytes);
    for(const auto& token:b.playback()){
        if(token.bytes.front()!='*')localized.emplace_back(token.sourceAddress,token.bytes);
        if(token.localizedDisplay){
            std::size_t at=0;
            while(at<token.localizedText.size()){
                const auto code=nextUtf8Code(token.localizedText,at);
                require(code<0xe000||code>0xf8ff,"Dialogue still uses an extracted private-use glyph");
            }
        }
    }
    require(!b.localizedPlayback.empty(),"Untranslated Chinese record");
    require(source==localized,"Translation changed an arcade speaker, wait, dynamic value or page command");
}
bool sameArtwork(const NativeModelChunk& a,const NativeModelChunk& b){
    if(a.batches.size()!=b.batches.size())return false;
    for(unsigned i=0;i<a.batches.size();++i){
        const auto& x=a.batches[i];const auto& y=b.batches[i];
        if(x.ich!=y.ich||x.material!=y.material||x.indices!=y.indices||x.vertices.size()!=y.vertices.size())return false;
        for(unsigned j=0;j<x.vertices.size();++j){
            const auto& p=x.vertices[j];const auto& q=y.vertices[j];
            if(p.header!=q.header||p.position.x!=q.position.x||p.position.y!=q.position.y||p.position.z!=q.position.z||
                p.u!=q.u||p.v!=q.v||p.color0!=q.color0||p.color1!=q.color1)return false;
        }
    }
    return true;
}
}
int main(int argc,char** argv)try{
    require(argc==3,"Usage: chinese_arcade_tests nativeRoot outputDirectory");
    const fs::path root=argv[1],output=argv[2],base=root/"data/original_assets";
    fs::create_directories(output);
    {
        // Typeset name glyphs contain their own dark stroke/shadow. Software
        // snapshots must retain RGB just like the production Unity atlas.
        std::ofstream out(output/"colored-font.bin",std::ios::binary);
        out.write("IDMF",4);
        const std::uint32_t header[]{1,1,1,1,1,'A'};
        out.write(reinterpret_cast<const char*>(header),sizeof(header));
        const float glyph[]{0,0,1,1,1};
        out.write(reinterpret_cast<const char*>(glyph),sizeof(glyph));
        const std::uint32_t black=0xff000000;
        out.write(reinterpret_cast<const char*>(&black),sizeof(black));out.close();
        const auto face=MenuFont::load(output,"colored-font.bin");
        std::vector<std::uint32_t> sample(1,0xffffffff);
        face.paint(sample,1,1,"A",0,0,1,0xffffffff);
        require(sample[0]==black,"Software font dropped reference glyph RGB");
    }
    for(const auto code:{0x41u,0x4e2du,0x20000u}){
        const auto encoded=utf8Code(code);std::size_t at=0;
        require(nextUtf8Code(encoded,at)==code&&at==encoded.size(),"UTF-8 round trip failed");
    }
    for(const std::string encoded:{"\xc0\xaf","\xed\xa0\x80","\xf4\x90\x80\x80","\xe4\xb8"}){
        std::size_t at=0;require(nextUtf8Code(encoded,at)=='?'&&at<=encoded.size(),"Invalid UTF-8 accepted");
    }
    setOriginalUiLanguage(0);const auto english=OriginalRivalDialogData::load(root);
    setOriginalUiLanguage(1);const auto japanese=OriginalRivalDialogData::load(root);
    require(!english.simplifiedChinese()&&!japanese.simplifiedChinese(),"Existing languages changed dialogue mode");
    setOriginalUiLanguage(2);const auto chinese=OriginalRivalDialogData::load(root);
    require(chinese.simplifiedChinese(),"Chinese script mode unavailable");
    {
        const auto fixture=output/"script-validation";
        const auto originals=fixture/"data/original_assets/rival_dialog";
        const auto translations=fixture/"data/localization/zh-Hans";
        fs::create_directories(originals);fs::create_directories(translations);
        for(const auto name:{"dialog.idasdialog","bunta.idasdialog"})
            fs::copy_file(base/"rival_dialog"/name,originals/name,fs::copy_options::overwrite_existing);
        fs::copy_file(root/"data/localization/zh-Hans/font.bin",translations/"font.bin",
                      fs::copy_options::overwrite_existing);
        struct Entry{std::uint32_t source;std::string command,text;};
        std::vector<Entry> valid;
        const auto& source=english.record(0,0);
        unsigned textIndex=~0u;
        for(unsigned i=0;i<source.tokens.size();++i){
            if(source.tokens[i].bytes.front()=='*'){
                textIndex=i;valid.push_back({~0u,"*","test"});
            }else valid.push_back({i,"",""});
        }
        require(textIndex!=~0u,"Missing source text fixture");
        const auto check=[&](const std::vector<Entry>& entries,bool accepted){
            std::ofstream out(translations/"dialog.bin",std::ios::binary);
            const auto word=[&](std::uint32_t value){
                for(unsigned shift=0;shift<32;shift+=8)out.put(char(value>>shift));
            };
            const auto text=[&](const std::string& value){word(value.size());out.write(value.data(),value.size());};
            out.write("IDASZH2\0",8);word(2);word(1);
            word(0);word(0);word(source.tokens.size());word(entries.size());
            for(const auto& entry:entries){word(entry.source);text(entry.command);text(entry.text);}
            out.close();
            bool loaded=false;
            try{OriginalRivalDialogData::load(fixture);loaded=true;}catch(const std::runtime_error&){}
            require(loaded==accepted,"Localized loader accepted modified arcade commands");
        };
        check(valid,true);
        auto injected=valid;injected.insert(injected.begin(),{~0u,"W_1",""});check(injected,false);
        auto reused=valid;
        const auto textAt=std::find_if(reused.begin(),reused.end(),[](const Entry& e){return e.command=="*";});
        *textAt={textIndex,"",""};check(reused,false);
        auto omitted=valid;
        const auto commandAt=std::find_if(omitted.begin(),omitted.end(),[](const Entry& e){
            return e.source!=~0u;
        });
        require(commandAt!=omitted.end(),"Missing source command fixture");
        omitted.erase(commandAt);check(omitted,false);
    }
    unsigned records=0;
    for(unsigned rival=0;rival<31;++rival)for(unsigned kind=0;kind<24;++kind){
        sameRecord(english.record(rival,kind),chinese.record(rival,kind));
        require(english.record(rival,kind).tokens.size()==japanese.record(rival,kind).tokens.size(),"Japanese script changed");
        ++records;
    }
    for(unsigned course=0;course<9;++course)for(unsigned kind=0;kind<51;++kind){
        sameRecord(english.buntaRecord(course,kind),chinese.buntaRecord(course,kind));++records;
    }
    unsigned banks=0;
    const auto localized=root/"data/localization/zh-Hans/original_assets";
    for(const auto& entry:fs::recursive_directory_iterator(localized)){
        if(entry.path().extension()!=".idasmesh")continue;
        const auto path=base/fs::relative(entry.path(),localized);
        setOriginalUiLanguage(0);const auto original=NativeModel::load(path);
        const auto en=NativeTextureBank::load(path.parent_path()/"textures/textures.idastex");
        setOriginalUiLanguage(2);const auto model=NativeModel::load(path);
        const auto zh=NativeTextureBank::load(path.parent_path()/"textures/textures.idastex");
        for(unsigned i=0;i<zh.size();++i)
            require(zh.at(i).width<=2048&&zh.at(i).height<=2048,"Chinese bank exceeds Unity UI texture limits");
        require(original.chunks.size()==model.chunks.size()&&zh.sourceSize()==en.size(),"Chinese changed bank source IDs");
        for(unsigned i=0;i<en.size();++i)require(en.at(i).argb==zh.at(i).argb,"Chinese changed fallback texture pixels");
        for(const auto& chunk:model.chunks)for(const auto& batch:chunk.batches)
            require(batch.material[9]==~0u||batch.material[9]<zh.size(),"Chinese texture index is out of bounds");
        std::vector<unsigned> preserved;
        if(path.stem()=="v3sS07tune")preserved={65};
        if(path.stem()=="v3sS00common")preserved={20,54};
        if(path.stem()=="game2d")preserved={3,4,5,7,8,10,60,91,95,96,100,101,123,136,138,140,142,143,145,182,183,184,185};
        for(auto index:preserved)
            require(sameArtwork(original.chunks[index],model.chunks[index]),
                    "Chinese changed artwork untranslated by the PSP patch");
        if(path.stem()=="v3sS05cars"){
            const auto& a=en.at(2);
            const auto& b=zh.at(model.chunks[37].batches[1].material[9]);
            require(a.width==64&&a.height==128&&b.width==a.width&&b.height==a.height,"Color label atlas changed size");
            for(unsigned y=2;y<=116;++y)for(unsigned x=24;x<=53;++x)
                if(x==24||x==53||y==2||y==116)
                    require(a.argb[(127-y)*64+x]==b.argb[(127-y)*64+x],"Chinese color hint changed its original frame");
        }
        const unsigned vehicleFirst=path.stem()=="game2d"?147:path.stem()=="v3sS05cars"?1:0;
        const unsigned vehicleLast=path.stem()=="game2d"?181:path.stem()=="v3sS05cars"?35:0;
        if(vehicleFirst)for(unsigned i=vehicleFirst;i<=vehicleLast;++i){
            const auto& a=original.chunks[i];const auto& b=model.chunks[i];
            require(a.batches.size()==b.batches.size(),"Vehicle label changed");
            for(unsigned n=0;n<a.batches.size();++n){
                const auto& x=a.batches[n];const auto& y=b.batches[n];
                require(x.material==y.material&&x.ich==y.ich&&x.indices==y.indices&&x.vertices.size()==y.vertices.size(),
                    "Vehicle name was translated");
                for(unsigned v=0;v<x.vertices.size();++v){
                    const auto& p=x.vertices[v];const auto& q=y.vertices[v];
                    require(p.header==q.header&&p.position.x==q.position.x&&p.position.y==q.position.y&&p.position.z==q.position.z&&
                        p.u==q.u&&p.v==q.v&&p.color0==q.color0&&p.color1==q.color1,"Vehicle glyph changed");
                }
            }
        }
        ++banks;
    }
    require(banks>=15,"Incomplete PSP-matched UI package");
    for(unsigned kind=0;kind<24;++kind){
        const auto& record=chinese.record(17,kind);
        require(!record.localizedPlayback.empty(),"Arcade-only snow story is untranslated");
        for(const auto& token:record.localizedPlayback)if(token.localizedDisplay)
            for(unsigned char c:token.localizedText)
                require(c>=128||c=='\n'||c=='C'||c=='E'||c=='L'||c=='I'||c=='A',
                    "Snow story contains untranslated English or an unexpected vehicle name");
    }
    require(!chinese.record(0,0).localizedPlayback.empty(),"Independent story paging is unavailable");
    unsigned scenes=0,pages=0,overflows=0;
    const auto font=loadChineseArcadeFont(root);
    for(unsigned code=0xe000;code<0xf100;++code)
        require(!font->hasGlyph(code),"Extracted legacy font slots remain in the production atlas");
    std::ofstream layout(output/"dialogue-layout.txt");
    std::set<std::array<unsigned,4>> reported;
    for(unsigned variant=0;variant<6;++variant){
        auto profile=makeOriginalFreshBattleProfile();
        for(unsigned i=0;i<31;++i)profile.setByte(116+i,variant&1?0x11:0);
        profile.setByte(164,variant*8);profile.setByte(1194,variant%3);
        profile.setByte(1195,variant*20);profile.setByte(1196,variant*5);
        for(unsigned rival=0;rival<40;++rival){
            const bool bunta=rival>=31;
            for(unsigned kind=0;kind<(bunta?51u:24u);++kind){
                OriginalRivalDialogSetup setup;setup.enemy=bunta?0:rival;setup.kind=kind;
                setup.playerCar=(variant*7+rival)%35;setup.cheer=variant;
                setup.weather=variant%2;setup.buntaChallenge=bunta;setup.buntaCourse=bunta?rival-31:0;
                OriginalRivalDialogState state;resetOriginalRivalDialog(state,chinese,profile,setup);
                unsigned ticks=0,previous=~0u;
                while(!originalRivalDialogReady(state)&&ticks++<16384){
                    stepOriginalRivalDialog(state,chinese,profile);
                    if(state.cursor==previous)continue;previous=state.cursor;++pages;
                    bool overflow=false;
                    for(const auto* line:{&state.name,&state.text})for(const auto& glyph:line->glyphs){
                        if(!line->simplifiedChinese)continue;
                        require(glyph.texture>=32&&glyph.texture<=0x10ffff,"Invalid Chinese glyph");
                        if(!font->hasGlyph(glyph.texture))
                            throw std::runtime_error("Chinese dialogue missing glyph "+
                                std::to_string(glyph.texture)+" at rival "+std::to_string(rival)+
                                " kind "+std::to_string(kind));
                        require(glyph.y>=416.25f,"Chinese dialogue escaped the original bottom black bar");
                        overflow|=glyph.x<0||glyph.x+line->cell>640||glyph.y<0||glyph.y+line->cell>480;
                    }
                    if(overflow&&reported.insert({rival,kind,state.cursor,variant}).second){
                        ++overflows;layout<<rival<<' '<<kind<<' '<<state.cursor<<' '<<variant<<": ";
                        for(const auto& glyph:state.text.glyphs)layout<<utf8Code(glyph.texture);
                        layout<<'\n';
                    }
                }
                require(ticks<16384,"Chinese dialogue failed to reach the next source page");
                ++scenes;
            }
        }
    }
    std::vector<std::uint32_t> pixels(640*480,0xff20242b);
    auto profile=makeOriginalFreshBattleProfile();
    setOriginalUiLanguage(0);const auto englishNames=OriginalBattleNames::load(root);
    setOriginalUiLanguage(2);const auto chineseNames=OriginalBattleNames::load(root);
    std::vector<std::uint32_t> enHud(640*480),zhHud(enHud.size());
    auto enteredProfile=profile;enteredProfile.setu(76,5);
    for(unsigned i=0;i<5;++i)enteredProfile.setu(44+i*4,162); // Source name-entry A.
    for(unsigned rival=0;rival<31;++rival){
        std::fill(enHud.begin(),enHud.end(),0);std::fill(zhHud.begin(),zhHud.end(),0);
        englishNames.paint(enHud,640,480,rival,0,rival%35,(rival+7)%35,&enteredProfile);
        chineseNames.paint(zhHud,640,480,rival,0,rival%35,(rival+7)%35,&enteredProfile);
        for(unsigned y=130;y<200;++y)for(unsigned x=574;x<640;++x)
            require(enHud[y*640+x]==zhHud[y*640+x],"Chinese HUD changed adjacent vehicle artwork");
        for(unsigned y=182;y<200;++y)for(unsigned x=510;x<574;++x)
            require(enHud[y*640+x]==zhHud[y*640+x],"Chinese HUD changed the player name");
        for(unsigned y=158;y<164;++y)for(unsigned x=510;x<574;++x)
            require(zhHud[y*640+x]==0,"Chinese rival name exceeds the player's 12px row height");
        const auto whiteHeight=[&](unsigned top){
            unsigned first=480,last=0;
            for(unsigned y=top;y<top+12;++y)for(unsigned x=510;x<572;++x){
                const auto pixel=zhHud[y*640+x];
                if(std::min({(pixel>>16)&255,(pixel>>8)&255,pixel&255})>160u){
                    first=std::min(first,y);last=std::max(last,y);
                }
            }
            return first<=last?last-first+1:0u;
        };
        require(whiteHeight(146)>0&&std::abs(int(whiteHeight(146))-int(whiteHeight(182)))<=1,
            "Chinese rival and original player have different visible name heights");
        if(rival==9||rival==19||rival==20||rival==21)
            bitmap(output/("battle-names-"+std::to_string(rival)+".bmp"),zhHud);
    }
    bitmap(output/"battle-names.bmp",zhHud);
    require(chineseNames.onlineName("LANPLAYER",false,0).text==englishNames.onlineName("LANPLAYER",false,0).text,
        "Chinese HUD changed a network name");
    setOriginalUiLanguage(0);OriginalVsBanner originalBanner;originalBanner.load(root);
    setOriginalUiLanguage(2);OriginalVsBanner banner;banner.load(root);
    OriginalVsBannerSetup versus;versus.localNameUtf8="CHRIS";versus.drawBackdrop=false;
    versus.showBattleRecords=true;versus.battles={400,99};versus.wins={217,85};
    banner.begin(versus);
    require(banner.displayName(0)=="CHRIS"&&banner.displayName(1)=="\xe6\xad\xa6\xe5\x86\x85\xe6\xa0\x91",
        "Chinese VS names changed a player name or lost the canonical rival name");
    for(unsigned i=0;i<152;++i)banner.tick();
    banner.paint(pixels,640,480);bitmap(output/"versus.bmp",pixels);
    {
        const std::string kyoichi="\xe9\xa1\xbb\xe8\x97\xa4\xe4\xba\xac\xe4\xb8\x80";
        const std::string kyoko="\xe5\xb2\xa9\xe6\xbf\x91\xe6\x81\xad\xe5\xad\x90";
        OriginalVsBannerSetup setup;setup.course=5;setup.direction=1;setup.night=true;
        setup.localNameUtf8="CHRIS";setup.drawBackdrop=false;setup.compactHeader=true;
        std::array<std::vector<std::uint32_t>,3> banners,huds;
        unsigned index=0;
        for(unsigned enemy:{11u,15u,24u}){
            setup.enemy=enemy;banner.begin(setup);
            require(banner.displayName(1)==(enemy==24?kyoko:kyoichi),
                "Kyoichi in Akina/Irohazaka was confused with Kyoko");
            for(unsigned i=0;i<152;++i)banner.tick();
            banners[index].resize(640*480);banner.paint(banners[index],640,480);
            huds[index].resize(640*480);
            chineseNames.paint(huds[index],640,480,enemy,0,0,1,&enteredProfile);
            if(enemy==15){
                bitmap(output/"versus-irohazaka-kyoichi.bmp",banners[index]);
                bitmap(output/"battle-names-irohazaka-kyoichi.bmp",huds[index]);
            }
            ++index;
        }
        require(banners[0]==banners[1]&&banners[1]!=banners[2],
            "Kyoichi VS metadata and painted name disagree");
        require(huds[0]==huds[1]&&huds[1]!=huds[2],
            "Irohazaka HUD paints Kyoko instead of Kyoichi");
    }
    {
        std::ofstream roster(output/"rival-names.tsv");
        roster<<"enemy\tcourse\tchoice\tnameChunk\tvsName\n";
        auto unlocked=makeOriginalFreshBattleProfile();unlocked.setu(1180,0x0800c081);
        constexpr unsigned expectedNameChunks[]{
            81,84,94,100,75,87,98,85,83,80,89,96,91,97,93,96,
            82,88,77,95,99,90,92,101,86,91,78,79,83,97,76};
        unsigned count=0;
        for(unsigned course=0;course<9;++course){
            for(unsigned choice=0;choice<originalLegendChoices(unlocked,course).count;++choice){
                const auto enemy=originalLegendRivalId(course,choice);
                unsigned nameChunk=0;
                for(const auto& draw:originalLegendMenuDraws(unlocked,course,choice,0,0))
                    if(draw.selector>=0x5004b&&draw.selector<=0x50065){
                        require(nameChunk==0,"Legend selection has multiple opponent names");
                        nameChunk=draw.selector&0xffff;
                    }
                require(nameChunk!=0,"Legend selection has no opponent name");
                require(nameChunk==expectedNameChunks[enemy],
                    "Legend selection name chunk belongs to a different opponent");
                OriginalVsBannerSetup setup;setup.enemy=enemy;banner.begin(setup);
                roster<<enemy<<'\t'<<course<<'\t'<<choice<<'\t'<<nameChunk<<'\t'
                      <<banner.displayName(1)<<'\n';
                ++count;
            }
        }
        require(count==31,"Full roster audit did not visit every Legend opponent");
    }
    versus.opponentNameUtf8="LANPLAYER";banner.begin(versus);
    require(banner.displayName(1)=="LANPLAYER","Chinese translated a network player name");
    for(unsigned direction=0;direction<7;++direction){
        OriginalVsBannerSetup header;header.course=3;header.direction=direction;
        header.compactHeader=true;header.showVersus=false;header.drawBackdrop=false;
        header.night=direction%2;header.wet=direction%3==0;
        banner.begin(header);
        originalBanner.begin(header);
        const auto fields=banner.metadataPlacements();
        const auto originalFields=originalBanner.metadataPlacements();
        require(fields.size()==4,"Chinese header lost a course or condition field");
        for(unsigned i=1;i<fields.size();++i){
            require(std::abs(fields[i].top-originalFields[i].top)<.001f&&
                std::abs(fields[i].height-originalFields[i].height)<.001f,
                "Chinese header changed D3's projected baseline or word height");
            require(fields[i].left>=0&&fields[i].left+fields[i].width<=620.f,
                "Chinese header conditions exceed the source canvas");
            if(i>1)require(fields[i].left>fields[i-1].left+fields[i-1].width&&
                fields[i].left-fields[i-1].left-fields[i-1].width<4.f,
                "Chinese header word spacing is inconsistent");
        }
        std::fill(pixels.begin(),pixels.end(),0xff000000);
        banner.paint(pixels,640,480);bitmap(output/("header-"+std::to_string(direction)+".bmp"),pixels);
        std::fill(pixels.begin(),pixels.end(),0xff000000);
        originalBanner.paint(pixels,640,480);bitmap(output/("header-original-"+std::to_string(direction)+".bmp"),pixels);
    }
    for(const auto& imported:importedCourseDefinitions){
        OriginalVsBannerSetup header;header.course=5;header.direction=1;
        header.customCourseName=imported.name;header.compactHeader=true;
        header.showVersus=false;header.drawBackdrop=false;
        banner.begin(header);
        const auto title=banner.importedTitlePlacement();
        require(title.width>0&&title.left>=0&&title.left+title.width<620&&
            title.top>=0&&title.top+title.height<55,"Chinese extension intro title overflows");
        std::fill(pixels.begin(),pixels.end(),0xff000000);banner.paint(pixels,640,480);
        bitmap(output/("header-"+std::string(imported.slug)+".bmp"),pixels);
    }
    {
        OriginalVsBannerSetup header;header.course=8;header.direction=1;
        header.compactHeader=true;header.showVersus=false;header.drawBackdrop=false;
        header.snow=true;header.night=true;
        banner.begin(header);
        std::fill(pixels.begin(),pixels.end(),0xff000000);banner.paint(pixels,640,480);
        bitmap(output/"header-akina-snow.bmp",pixels);
        originalBanner.begin(header);
        std::fill(pixels.begin(),pixels.end(),0xff000000);
        originalBanner.paint(pixels,640,480);bitmap(output/"header-original-akina-snow.bmp",pixels);
    }
    OriginalRivalDialogScene scene;scene.load(root);
    for(unsigned rival=0;rival<31;++rival){
        OriginalRivalDialogSetup setup;setup.enemy=rival;scene.begin(profile,setup);
        unsigned ticks=0;
        do{scene.step(profile);++ticks;}while(ticks<4096&&
            (scene.state().backgroundWait||scene.state().text.glyphs.empty()||scene.state().name.glyphs.empty()));
        require(ticks<4096,"Dialogue screenshot has no visible Chinese text");
        std::fill(pixels.begin(),pixels.end(),0xff20242b);scene.paint(pixels,640,480);
        bitmap(output/("dialogue-"+std::to_string(rival)+".bmp"),pixels);
    }
    {
        OriginalRivalDialogSetup setup;setup.enemy=16;setup.playerCar=0;
        scene.begin(profile,setup);
        bool found=false;
        for(unsigned tick=0;tick<16384&&!scene.ready();++tick){
            scene.step(profile);
            std::string text;
            for(const auto& glyph:scene.state().text.glyphs)text+=utf8Code(glyph.texture);
            if(text.find("EIGHT\xe2\x88\x92SIX")!=std::string::npos){
                found=true;
                const auto& glyphs=scene.state().text.glyphs;
                for(unsigned i=0;i+1<glyphs.size();++i)if(glyphs[i].texture<128||glyphs[i].texture==0x2212)
                    require(std::abs(glyphs[i+1].x-glyphs[i].x-
                        font->width(utf8Code(glyphs[i].texture),scene.state().text.cell))<.001f,
                        "Dynamic car layout does not use its actual font advance");
                for(unsigned i=0;i<100;++i)scene.step(profile);
                std::fill(pixels.begin(),pixels.end(),0xff20242b);
                scene.paint(pixels,640,480);bitmap(output/"dialogue-kai-eight-six.bmp",pixels);
                break;
            }
        }
        require(found,"Kai story did not exercise the original EIGHT-SIX dynamic car name");
    }
    unsigned dynamicCarScenes=0;
    for(unsigned rival=0;rival<31;++rival){
        OriginalRivalDialogSetup setup;setup.enemy=rival;setup.playerCar=0;
        scene.begin(profile,setup);
        for(unsigned tick=0;tick<16384&&!scene.ready();++tick){
            scene.step(profile);
            if(!scene.state().text.simplifiedChinese)continue;
            std::string text;
            for(const auto& glyph:scene.state().text.glyphs)text+=utf8Code(glyph.texture);
            if(text.find("EIGHT\xe2\x88\x92SIX")==std::string::npos)continue;
            ++dynamicCarScenes;
            for(unsigned i=0;i<100;++i)scene.step(profile);
            std::fill(pixels.begin(),pixels.end(),0xff20242b);scene.paint(pixels,640,480);
            bitmap(output/("dialogue-eight-six-rival-"+std::to_string(rival)+".bmp"),pixels);
            break;
        }
    }
    require(dynamicCarScenes>=2,"Dynamic car typography was checked in only one rival scene");
    std::cout<<"EIGHT-SIX observed in "<<dynamicCarScenes<<" different rival intros\n";
    OriginalGasstandAttract gas;gas.load(root);
    for(unsigned script=0;script<3;++script){
        gas.reset(script);for(unsigned n=0;n<180;++n)gas.step();
        std::fill(pixels.begin(),pixels.end(),0xff20242b);gas.paint(pixels,640,480);
        bitmap(output/("gasstand-"+std::to_string(script)+".bmp"),pixels);
    }
    Idas3UiEnable(1);Idas3UiBeginFrame(640,480);
    font->paint(pixels,640,480,"\xe8\x97\xa4\xe5\x8e\x9f\xe6\x8b\x93\xe6\xb5\xb7 AE86",10,404,22,0xffffffff);
    unityUiSubmit(pixels.data(),640,480,false,false,true);
    UnityUiFrame frame{sizeof(UnityUiFrame)};
    require(Idas3UiGetFrame(&frame)&&frame.drawCount>0&&frame.textureCount>0,"Chinese font bypassed Unity UI capture");
    for(unsigned id=0;id<frame.textureCount;++id){
        UnityUiTextureInfo info{sizeof(UnityUiTextureInfo)};
        require(Idas3UiGetTextureInfo(id,&info)&&info.width>0&&info.height>0&&
            info.width<=2048&&info.height<=2048&&info.bytes==info.width*info.height*4,
            "Chinese font exceeds the production Unity UI texture contract");
    }
    Idas3UiEnable(0);
    std::cout<<"Records "<<records<<", banks "<<banks<<", scenes "<<scenes<<", page states "<<pages
        <<", layout overflows "<<overflows<<'\n';
    require(overflows==0,"Chinese dialogue exceeds the authored screen; inspect dialogue-layout.txt");
    {
        Frontend frontend;frontend.initialize(root);setCustomMenuLanguage(2);
        const auto capture=[&](FrontendStage stage,const std::string& name){
            frontend.stage=stage;
            for(unsigned i=0;i<180;++i)frontend.advance(1./60.);
            bitmap(output/("menu-"+name+".bmp"),frontend.paint(640,480));
        };
        capture(FrontendStage::SaveSelect,"empty-save");
        capture(FrontendStage::Make,"maker");
        capture(FrontendStage::Car,"car");
        capture(FrontendStage::Transmission,"at");
        frontend.automatic=false;capture(FrontendStage::Transmission,"mt");
        capture(FrontendStage::TuningCourse,"tune");
        frontend.gameMode=OriginalGameMode::LegendOfTheStreets;
        capture(FrontendStage::Mode,"mode");
        frontend.gameMode=OriginalGameMode::TimeAttack;capture(FrontendStage::Mode,"mode-timeattack");
        frontend.gameMode=OriginalGameMode::BuntaChallenge;capture(FrontendStage::Mode,"mode-bunta");
        frontend.gameMode=OriginalGameMode::LegendOfTheStreets;
        frontend.course=5;capture(FrontendStage::Course,"course");
        capture(FrontendStage::Route,"route");
        capture(FrontendStage::Weather,"weather");
        capture(FrontendStage::Time,"time");
        frontend.rivalChoice=0;capture(FrontendStage::Rival,"rival-day");
        frontend.rivalChoice=2;capture(FrontendStage::Rival,"rival-night");
        frontend.course=4;frontend.rivalChoice=1;capture(FrontendStage::Rival,"happo-sakai");
        frontend.rivalChoice=2;capture(FrontendStage::Rival,"happo-tomoyuki");
        frontend.gameMode=OriginalGameMode::TimeAttack;
        for(int course=0;course<9;++course){
            frontend.course=course;
            capture(FrontendStage::Course,"original-course-"+std::to_string(course));
        }
        for(const auto& imported:importedCourseDefinitions){
            frontend.enableHakoneCourse(root.parent_path()/"RuntimeAssets"/imported.folder,int(imported.id));
            frontend.course=int(imported.id);capture(FrontendStage::Course,imported.slug);
        }
        setCustomMenuLanguage(0);
    }
    for(const auto language:{0,1}){
        setOriginalUiLanguage(language);
        Frontend frontend;frontend.initialize(root);
        frontend.stage=FrontendStage::Mode;
        frontend.gameMode=OriginalGameMode::LegendOfTheStreets;
        for(unsigned i=0;i<180;++i)frontend.advance(1./60.);
        bitmap(output/("menu-mode-d3-"+std::to_string(language)+".bmp"),frontend.paint(640,480));
    }
    setOriginalUiLanguage(0);
    std::cout<<"PASS: Chinese dialogue provenance, branch playback, layout, banks, vehicle names and native composition\n";
    return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
