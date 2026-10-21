#pragma once
#include <array>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <span>
#include <string>
#include <vector>

namespace idas3 { class MenuFont; }
namespace idas3::original {
// Authored dialogue data, resolved by the original startup constructors.
// Source addresses are provenance only; native playback never dereferences them.
struct OriginalRivalDialogToken {
    std::uint32_t sourceAddress{};
    std::string bytes;
    std::string localizedText;
    bool localizedDisplay=false;
};
struct OriginalRivalDialogRecord {
    std::uint32_t sourceRecord{},sourceScript{};
    std::array<float,6> portraitCoordinates{};
    std::uint32_t portraitKinds{};
    std::vector<OriginalRivalDialogToken> tokens;
    std::vector<OriginalRivalDialogToken> localizedPlayback;
    std::span<const OriginalRivalDialogToken> playback()const{
        return localizedPlayback.empty()?std::span<const OriginalRivalDialogToken>(tokens):
            std::span<const OriginalRivalDialogToken>(localizedPlayback);
    }
};
struct OriginalRivalDialogIdentity {
    std::uint32_t character{},backgroundCourse{},backgroundNight{};
    std::string portrait;
};
class OriginalRivalDialogData {
public:
    static OriginalRivalDialogData load(const std::filesystem::path& root);
    const OriginalRivalDialogIdentity& enemy(std::uint32_t index)const;
    const OriginalRivalDialogRecord& record(std::uint32_t character,std::uint32_t kind)const;
    bool hasBunta()const{return !buntaRecords_.empty();}
    bool simplifiedChinese()const{return simplifiedChinese_;}
    const std::shared_ptr<const MenuFont>& chineseFont()const{return chineseFont_;}
    const OriginalRivalDialogRecord& buntaRecord(std::uint32_t course,std::uint32_t kind)const;
private:
    std::array<OriginalRivalDialogIdentity,31> identities_;
    std::array<OriginalRivalDialogRecord,31*24> records_;
    std::vector<OriginalRivalDialogRecord> buntaRecords_;
    bool simplifiedChinese_=false;
    std::shared_ptr<const MenuFont> chineseFont_;
};
// Original0FA5C0 forwards to0F4640. This is a scene-kind gate, not a test
// for the presence of another sentence in a text buffer.
bool originalRivalDialogKindAvailable(std::uint32_t kind);
}
