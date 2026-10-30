using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class StoryVoiceDriver
{
    private static int checks;
    private static void Check(bool ok,string name){if(!ok)throw new Exception(name);++checks;Console.WriteLine("PASS\t"+name);}
    private static Idas3StoryVoiceLogic.Rule Rule(int id,int e,int priority=0,int count=255,int chain=255){
        var flags=new int[28];flags[0]=id;flags[1]=e;flags[2]=priority;flags[3]=count;
        for(int i=4;i<8;++i)flags[i]=1;flags[10]=chain;
        return new Idas3StoryVoiceLogic.Rule{@event=e,clip="line"+id,sourceFlags=flags};
    }
    private static byte[] Hex(string text){
        var bytes=new byte[text.Length/2];for(int i=0;i<bytes.Length;++i)bytes[i]=Convert.ToByte(text.Substring(i*2,2),16);return bytes;
    }
    private static void Reference(string path){
        Idas3StoryVoiceLogic logic=null;var rules=new List<Idas3StoryVoiceLogic.Rule>();
        ulong seed=0;int count=0,frames=0,cases=0,bank=0;
        foreach(string line in File.ReadLines(path)){
            var fields=line.Split('\t');
            if(fields[0]=="B"){
                bank=int.Parse(fields[1]);seed=ulong.Parse(fields[2]);count=int.Parse(fields[3]);
                rules.Clear();logic=new Idas3StoryVoiceLogic();++cases;
            }else if(fields[0]=="R"){
                var row=Hex(fields[1]);var flags=new int[28];for(int i=0;i<28;++i)flags[i]=row[i];
                string clip=Encoding.ASCII.GetString(row,28,32).TrimEnd('\0');
                rules.Add(new Idas3StoryVoiceLogic.Rule{@event=row[1],clip=clip,sourceFlags=flags});
                if(rules.Count==count)logic.Reset(rules.ToArray(),seed);
            }else{
                byte[] data=Hex(fields[1]);
                var input=new Idas3StoryVoiceLogic.Input{
                    sector=BitConverter.ToInt32(data,0),progress=BitConverter.ToSingle(data,4),
                    speedKmh=BitConverter.ToSingle(data,8),clockFrame=BitConverter.ToInt32(data,20),
                    gap=BitConverter.ToSingle(data,24),playerAhead=BitConverter.ToInt32(data,28)!=0,playing=data[36]!=0};
                var result=logic.Tick(input);
                int selected=result==null?-1:logic.ActiveIndex,expected=int.Parse(fields[2]);
                if(selected!=expected)throw new Exception($"bank {bank} seed {seed} frame {input.clockFrame}: rule {selected} != original {expected}");
                byte[] actual=logic.Snapshot(),original=Convert.FromBase64String(fields[3]);
                for(int i=0;i<actual.Length;++i)if(actual[i]!=original[i])
                    throw new Exception($"bank {bank} seed {seed} frame {input.clockFrame}: state byte {i:x}: {actual[i]} != original {original[i]}");
                ++frames;
            }
        }
        Check(cases==90&&frames==198000,"Original PSP CPU: 30 banks, 90 scenarios, 198000 exact frame snapshots and selections");
    }
    public static void Main(string[] args){
        Check(System.Runtime.InteropServices.Marshal.SizeOf<Idas3StoryVoiceLogic.State>()==64,"Speech telemetry ABI is 64 bytes");
        for(uint enemy=0;enemy<31;++enemy)Check((Idas3StoryVoiceLogic.Bank(enemy)>=0)==(enemy!=17),"Roster voice mapping "+enemy);
        var logic=new Idas3StoryVoiceLogic();
        logic.Reset(new[]{Rule(0,1,2,1,1),Rule(1,12,2,1)},1);
        var input=new Idas3StoryVoiceLogic.Input();
        for(int i=0;i<90;++i){input.clockFrame=i;Check(logic.Tick(input)==null,"No start before PSP frame "+i);}
        input.clockFrame=90;Check(logic.Tick(input)?.Id==0,"PSP frame 90 selects start");
        input.clockFrame=91;input.playing=true;Check(logic.Tick(input)==null,"Playing start cannot restart");
        input.clockFrame=92;input.playing=false;Check(logic.Tick(input)?.Id==1,"Original row chain plays reply without guessed event queue");
        input.clockFrame=93;Check(logic.Tick(input)==null,"One-shot reply cannot repeat");
        Check(!new Idas3StoryVoiceLogic.State{flags=1,enemy=17,course=8}.Eligible,"Snow has no borrowed voices");
        if(args.Length>0)Reference(args[0]);
        Console.WriteLine("Checks: "+checks);
    }
}
