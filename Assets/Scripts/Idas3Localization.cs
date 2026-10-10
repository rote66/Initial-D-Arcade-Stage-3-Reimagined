using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityEngine;

// Reviewed UI copy, keyed by the existing English source. Dynamic messages use
// anchored templates; names, paths and track titles can explicitly bypass it.
public static class Idas3Localization
{
    public enum Language { English, SimplifiedChinese, TraditionalChinese }
    private sealed class Entry { public string source,hans,hant; public Regex pattern; public string[] slots; }
    private static readonly Dictionary<string,Entry> exact=new Dictionary<string,Entry>(StringComparer.Ordinal);
    private static readonly List<Entry> templates=new List<Entry>();
    private static bool initialized;
    private static Font fontHans,fontHant;
    public static Language Current {get;private set;}
    public static bool Chinese=>Current!=Language.English;
    public static string LanguageCode=>Current==Language.SimplifiedChinese?"zh-Hans":Current==Language.TraditionalChinese?"zh-Hant":"en";
    public static Font ChineseFont=>Current==Language.TraditionalChinese?
        (fontHant?fontHant:fontHant=Resources.Load<Font>("Fonts/NotoSansCJKtc-Regular")):
        (fontHans?fontHans:fontHans=Resources.Load<Font>("Fonts/NotoSansCJKsc-Regular"));
    public static int EntryCount {get{Initialize();return exact.Count+templates.Count;}}
#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern int Idas3PreferredUiLanguage();
#endif
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    public static void Initialize(){
        if(initialized)return;initialized=true;exact.Clear();templates.Clear();
#if UNITY_IOS && !UNITY_EDITOR
        Current=(Language)Mathf.Clamp(Idas3PreferredUiLanguage(),0,2);
#else
        Current=Application.systemLanguage==SystemLanguage.ChineseTraditional?Language.TraditionalChinese:
            Application.systemLanguage==SystemLanguage.Chinese||Application.systemLanguage==SystemLanguage.ChineseSimplified?Language.SimplifiedChinese:Language.English;
#endif
        var asset=Resources.Load<TextAsset>("Localization/Ui");
        if(!asset)throw new InvalidOperationException("The reviewed UI language catalog is missing.");
        foreach(var line in asset.text.Split('\n')){
            if(string.IsNullOrWhiteSpace(line)||line.StartsWith("#",StringComparison.Ordinal))continue;
            var cells=line.TrimEnd('\r').Split('|');
            if(cells.Length!=3||string.IsNullOrEmpty(cells[1])||string.IsNullOrEmpty(cells[2]))throw new InvalidOperationException("Invalid UI catalog row: "+line);
            var entry=new Entry{source=cells[0].Replace("\\n","\n"),hans=cells[1].Replace("\\n","\n"),hant=cells[2].Replace("\\n","\n")};
            var placeholders=Regex.Matches(entry.source,@"\{(\d+)\}");
            if(placeholders.Count==0){exact.Add(entry.source,entry);continue;}
            var slots=new List<string>();string expression=Regex.Escape(entry.source);
            foreach(Match match in placeholders){string token=match.Value;if(slots.Contains(token))continue;
                slots.Add(token);expression=expression.Replace(Regex.Escape(token),"(?<v"+match.Groups[1].Value+">.+?)");}
            entry.slots=slots.ToArray();entry.pattern=new Regex("\\A"+expression+"\\z",RegexOptions.Singleline|RegexOptions.CultureInvariant|RegexOptions.IgnoreCase);
            templates.Add(entry);
        }
        // Specific messages win over more general prefix/suffix templates.
        templates.Sort((a,b)=>b.source.Length.CompareTo(a.source.Length));
        Debug.Log("IDAS3 UI language: "+LanguageCode);
    }
    public static Language Resolve(string code){
        code=(code??"").Replace('_','-').ToLowerInvariant();
        if(!code.StartsWith("zh",StringComparison.Ordinal))return Language.English;
        if(code.Contains("hant")||code=="zh-tw"||code=="zh-hk"||code=="zh-mo")return Language.TraditionalChinese;
        return Language.SimplifiedChinese;
    }
    internal static Language UseDiagnosticLanguage(Language language){Initialize();var previous=Current;Current=language;return previous;}
    public static string Text(string source){
        Initialize();source=source??"";if(!Chinese||source.Length==0)return source;
        if(exact.TryGetValue(source,out var entry)||exact.TryGetValue(source.ToUpperInvariant(),out entry))return Current==Language.SimplifiedChinese?entry.hans:entry.hant;
        foreach(var template in templates){
            var match=template.pattern.Match(source);if(!match.Success)continue;
            string translated=Current==Language.SimplifiedChinese?template.hans:template.hant;
            foreach(var slot in template.slots){string value=match.Groups["v"+slot.Substring(1,slot.Length-2)].Value;
                if(exact.TryGetValue(value,out var leaf)||exact.TryGetValue(value.ToUpperInvariant(),out leaf))value=Current==Language.SimplifiedChinese?leaf.hans:leaf.hant;
                translated=translated.Replace(slot,value);}
            return translated;
        }
        return source;
    }
    public static string Format(string source,params object[] values)=>string.Format(CultureInfo.CurrentCulture,Text(source),values);
    public static GUIStyle Style(GUIStyle style){Initialize();return Chinese?new GUIStyle(style){font=ChineseFont}:style;}
    public static void LayoutLabel(string source,params GUILayoutOption[] options)=>GUILayout.Label(Text(source),Style(GUI.skin.label),options);
    public static void LayoutLabel(string source,GUIStyle style,params GUILayoutOption[] options)=>GUILayout.Label(Text(source),Style(style),options);
    public static bool LayoutButton(string source,params GUILayoutOption[] options)=>GUILayout.Button(Text(source),Style(GUI.skin.button),options);
    public static bool LayoutButtonRaw(string source,params GUILayoutOption[] options)=>GUILayout.Button(source,Style(GUI.skin.button),options);
    public static bool LayoutToggle(bool value,string source,GUIStyle style,params GUILayoutOption[] options)=>GUILayout.Toggle(value,Text(source),Style(style),options);
    public static void Label(Rect bounds,string source,GUIStyle style)=>LabelRaw(bounds,Text(source),style);
    public static void LabelRaw(Rect bounds,string value,GUIStyle style){
        Initialize();var oldFont=style.font;int oldSize=style.fontSize;
        try{
            if(Chinese)style.font=ChineseFont;
            // Short labels must remain inside the same visible/touch bounds.
            if(Chinese&&!style.wordWrap&&oldSize>0){var content=new GUIContent(value);
                while(style.fontSize>Math.Max(10,oldSize*2/3)&&style.CalcSize(content).x>bounds.width-4)--style.fontSize;}
            GUI.Label(bounds,value,style);
        }finally{style.font=oldFont;style.fontSize=oldSize;}
    }
    public static IEnumerable<string> ChineseCopy(){Initialize();foreach(var entry in exact.Values){yield return entry.hans;yield return entry.hant;}foreach(var entry in templates){yield return entry.hans;yield return entry.hant;}}
}
