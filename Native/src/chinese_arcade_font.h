#pragma once
#include "menu_font.h"
#include <memory>
#include <mutex>
#include <stdexcept>

namespace idas3 {
inline std::shared_ptr<const MenuFont> loadChineseArcadeFont(const std::filesystem::path& root){
    static std::mutex mutex;
    static std::filesystem::path cachedRoot;
    static std::weak_ptr<const MenuFont> cached;
    const std::lock_guard lock(mutex);
    if(cachedRoot==root)if(auto font=cached.lock())return font;
    auto font=std::make_shared<MenuFont>(MenuFont::load(root,"data/localization/zh-Hans/font.bin"));
    if(!font->ready())throw std::runtime_error("Simplified Chinese arcade font is missing");
    cachedRoot=root;cached=font;return font;
}
}
