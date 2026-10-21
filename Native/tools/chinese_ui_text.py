"""Chinese display copy and typography, authored for this game's controls."""
import json
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FONT_ROOT = ROOT/"Native/localization/zh-Hans/fonts"


def font_reference(path):
    """Keep host-specific font locations out of generated metadata."""
    try:
        return Path(path).resolve().relative_to(ROOT).as_posix()
    except ValueError:
        return "$IDAS3_BRUSH_FONT"


def brush_font():
    supplied = os.environ.get("IDAS3_BRUSH_FONT")
    if supplied:
        return Path(supplied)
    available = sorted(Path("/System/Library/AssetsV2").glob(
        "com_apple_MobileAsset_Font*/*/AssetData/Xingkai.ttc"))
    if not available:
        raise RuntimeError("Set IDAS3_BRUSH_FONT to an installed Xingkai face for authoring")
    return available[0]


FONTS = {
    "title": FONT_ROOT/"SourceHanSerifSC-Heavy.otf",
    "mode": FONT_ROOT/"SourceHanSerifSC-Heavy.otf",
    "caption": FONT_ROOT/"SourceHanSerifSC-Heavy.otf",
    "brush": brush_font(),
    "label": FONT_ROOT/"SourceHanSerifSC-Heavy.otf",
    "body": ROOT/"Assets/Resources/Fonts/NotoSansSC-Regular.otf",
    "latin": ROOT/"Assets/Resources/Fonts/NotoSansSC-Regular.otf",
}
GLYPH_ALIASES = {"brush": {"・": "·"}}
COURSES = ("妙义", "碓冰", "赤城", "秋名", "伊吕波坂", "八方原", "正丸", "土坂")
IMPORTED_COURSES = (
    ("HAKONE", "箱根", "初级"),
    ("SADAMINE", "定峰", "初级"),
    ("ENNA", "塩那", "高级"),
    ("MYOGI_SPECIAL", "真・妙义", "高级"),
    ("USUI_SPECIAL", "真・碓冰", "高级"),
    ("MOMIJI", "枫树线", "高级"),
    ("TSUBAKI", "筑波", "超高级"),
    ("GUNSAI", "群赛", "中级"),
    ("ODAWARA", "小田原", "中级"),
)


def rival_names():
    entries = json.loads((ROOT/"Native/localization/zh-Hans/names.json").read_text())["characters"]
    names = {entry["en"]: entry["zh"] for entry in entries}
    order = ("IGGY", "KENJI", "SHINGO", "TORU", "KAWAI", "MAYA", "OTAKU 1",
             "DANNY", "K.T.", "COLE", "ZACK", "KYLE", "RY", "TAK", "HAWK",
             "KYLIE", "CAINE", "MIKI", "DICE", "SMILEY", "TOUCH", "NOBU",
             "SID", "AKI", "KYLIE", "RY", "EVO.V", "EVO.VI", "K.T.", "TAK",
             "BUNTA", "BUNTA")
    result = [names[key] for key in order]
    result[5], result[6] = "佐藤真子＆沙雪", "东京二人组"
    return result


def specs():
    result = {}

    def put(bank, chunk, text, role="label", **kwargs):
        result.setdefault(bank, {})[str(chunk)] = dict(text=text, role=role, **kwargs)

    headings = (
        ("v3sS04maker", 17, "请选择制造商"),
        ("v3sS05cars", 43, "请选择车型"),
        ("v3sS06mission", 5, "请选择变速箱"),
        ("v3sS07tune", 1, "请选择改装方案"),
        ("v3sS08name", 6, "请输入车手名"),
        ("v3sS11mode", 18, "请选择游戏模式"),
        ("v3sK01course", 26, "请选择赛道"),
        ("v3sK02rival", 47, "请选择对手"),
        ("v3sT02route", 31, "请选择路线"),
        ("v3sT03weather", 1, "请选择天气"),
        ("v3sT04time", 5, "请选择时间段"),
    )
    for bank, chunk, text in headings:
        hint = [dict(control="摇杆", action="选择"),
                dict(control="START", action="确认")]
        if bank == "v3sS05cars":
            hint[0]["action"] = "选车／换色"
        elif bank == "v3sK02rival":
            hint.append(dict(control="长按VIEW", action="选曲"))
        elif bank == "v3sS08name":
            hint[0]["action"] = "选字"
        put(bank, chunk, text, "title", heading=True, hint=hint,
            ink_aspect=1.24*len(text)-.32, tracking=.39, shadow=True)

    for chunk, text in ((0, "AT"), (1, "MT")):
        put("v3sS06mission", chunk, text, "latin", italic=True, metallic=True,
            subtitle="自动挡" if chunk == 0 else "手动挡", ink_aspect=2.15)
        put("v3sS06mission", chunk+2, "", glow_of=chunk,
            color="#ffae62" if chunk == 0 else "#6bc0ff")
    # Keep the native A/B/C/D shapes; replace the old OPTION caption.
    for chunk, text in ((2, "A"), (3, "B"), (4, "C"), (5, "D")):
        put("v3sS07tune", chunk, text, "latin", subtitle="方案",
            preserve_primary=True, trim_primary_top=.27)
        put("v3sS07tune", chunk+66, text, glow_of=chunk, color="#ffc65b")
    put("v3sS07tune", 6, "原厂", "label", subtitle="方案", metallic=True, italic=True)
    put("v3sS07tune", 72, "原厂", glow_of=6, color="#ffc65b")
    put("v3sS07tune", 50, "无装配部分", "body")
    result["v3sS07tune"]["7"] = dict(layers=[
        dict(text="装配部分 ▲", role="body", batch=1),
        dict(text="部件制造商 ▼", role="body", batch=2),
    ])

    descriptions = (
        "驾驶爱车挑战各地对手，争夺公路最速。\n每场比赛都可获得点数，用来改装车辆。",
        "独自挑战计时，刷新自己的最快成绩。\n记录车型排名，与其他车手比拼赛道纪录。",
        "与藤原文太进行一对一较量。\n消耗点数发起挑战，获胜即可赢取点数。",
    )
    for chunk, text in enumerate(descriptions, 1):
        put("v3sS11mode", chunk, text, "caption", font_size=12,
            horizontal_scale=1.16, paragraph_align="left", line_pitch=14,
            paragraph_left=112, paragraph_baseline=35)
    put("v3sS11mode", 5, "模式内容", "body", badge=True)
    for chunk, text, accent, aspect in (
        (6, "挑战文太!!", "战", 5.91),
        (7, "公路最速传说", "速", 7.27),
        (8, "计时赛", "时", 3.77),
    ):
        put("v3sS11mode", chunk, text, "mode", italic=True, accent=accent,
            ink_aspect=aspect, tracking=.028)
    for chunk, normal in ((9, 6), (10, 6), (11, 7), (12, 7), (13, 8), (14, 8)):
        put("v3sS11mode", chunk, "", glow_of=normal, color="#ff5c1b", spread=1)

    for chunk, course, level in (
        (20, 0, "初级"), (24, 1, "中级"), (15, 2, "高级"), (17, 3, "高级"),
        (19, 4, "超高级"), (18, 5, "超高级"), (22, 6, "超高级"), (23, 7, "超高级"),
    ):
        put("v3sK01course", chunk, COURSES[course], "brush", course_card=level)
    put("v3sK01course", 16, "秋名", "brush", course_card="超高级", snow=True)
    for bank, chunk in (("v3sK01course", 21), ("v3sK00common", 26)):
        put(bank, chunk, "无法选择", "body", unavailable=True)
    for chunk, name in ((31, "赤城"), (39, "碓冰"), (36, "妙义"), (33, "秋名"),
                        (37, "正丸"), (38, "土坂"), (34, "八方原"), (35, "伊吕波坂")):
        put("v3sK00common", chunk, name, "brush", course_title=True,
            bounds=[0, -1.58, 3.98, 0], texture_size=[398, 158])
    put("v3sK00common", 32, "秋名", "brush", course_title=True, snow=True,
        bounds=[0, -1.58, 3.98, 0], texture_size=[398, 158])

    conditions = {1:"去路", 2:"上坡", 4:"昼", 6:"下坡", 7:"晴", 8:"逆时针",
                  9:"夜", 11:"逆行", 12:"顺时针", 13:"雪", 14:"归路", 15:"雨"}
    for chunk, text in conditions.items():
        width = .38 if chunk in (7, 13, 15) else .82 if chunk in (8, 12) else .60
        put("v3sK02rival", chunk, text, italic=True, pixel_height=22,
            bounds=[0, -.32, width, 0])
    for chunk, number in enumerate("一二三四五", 16):
        put("v3sK02rival", chunk, f"第 {number} 战", italic=True)
    put("v3sK02rival", 21, "最终战", italic=True)
    names = rival_names()
    name_indices = (4, 30, 18, 26, 27, 9, 0, 16, 8, 1, 7, 15, 5, 17,
                    10, 21, 12, 22, 14, 2, 19, 11, 13, 6, 20, 3, 23)
    for chunk, index in enumerate(name_indices, 75):
        for bank, at in (("v3sK02rival", chunk), ("v3sK00rivalface", chunk-40)):
            put(bank, at, names[index], "brush", italic=True, stroke="#111111")

    choices = {
        "v3sT02route": {16:"归路", 17:"上坡", 18:"下坡", 19:"逆时针",
                        20:"逆行", 21:"顺时针", 22:"去路"},
        "v3sT04time": {1:"DAY 昼", 2:"夜 NIGHT"},
        "v3sT03weather": {2:"DRY 晴", 3:"雨 WET"},
    }
    for bank, rows in choices.items():
        for chunk, text in rows.items():
            put(bank, chunk, text, italic=True, metallic=True)
            selected = chunk+7 if bank == "v3sT02route" else chunk+2
            put(bank, selected, "", glow_of=chunk,
                color="#ffad59" if chunk % 2 == 0 else "#69bdff")
    put("game2d", 9, "逆行", "body", italic=True, color="#141414", stroke="#fb4828")
    for chunk, text, color in (
        (15, "综合", "#e9a52f"), (16, "车型", "#72cb24"), (17, "个人", "#22dd8a"),
        (18, "综合", "#e9a52f"), (19, "个人", "#22dd8a"),
        (21, "刷新了赛道纪录", "#ff3d24"), (22, "刷新了个人最佳纪录", "#ff3d24"),
        (23, "刷新了此车型的赛道纪录", "#ff3d24"),
    ):
        put("game2d_ta", chunk, text, "body", color=color)
    for chunk, text in enumerate(COURSES, 72):
        put("start2d", chunk, text, "brush", start_title=True)
    put("start2d", 80, "秋名", "brush", start_title=True, snow_intro=True)
    for chunk, text in {
        0:"雪 ]", 105:"· 昼 ·", 106:"晴 ]", 107:"[ 归路", 108:"[ 逆行",
        109:"[ 下坡", 110:"最终战", 111:"[ 逆时针", 112:"· 夜 ·",
        113:"[ 上坡", 114:"[ 去路", 115:"[ 顺时针", 118:"雨 ]",
    }.items():
        put("start2d", chunk, text, italic=True, start_condition=chunk != 110,
            fixed_baseline=chunk != 110,
            natural_width=chunk != 110)
    put("rival_scene", 19, "按START跳过", "body", color="#ef4444", pixel_height=12)
    return result
