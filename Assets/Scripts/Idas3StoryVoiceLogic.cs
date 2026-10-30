using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Street Stage 1AEC50/1AEFA0. Owns presentation state and a private RNG,
// never the driving simulation's random stream or clock.
public sealed class Idas3StoryVoiceLogic
{
    [Serializable,StructLayout(LayoutKind.Sequential,Pack=8)]
    public struct State {
        public uint size,version,flags,enemy,course,phase,raceId,outcome;
        public ulong ticks;
        public float advantage,progress;
        public uint sector,clockFrame;
        public float speedKmh;
        public uint playerAhead;
        public bool Eligible=>(flags&1)!=0&&course!=8&&Bank(enemy)>=0;
        public bool Paused=>(flags&2)!=0;
    }
    [Serializable] public sealed class Rule {
        public int @event;
        public string clip;
        public int[] sourceFlags;
        public int Id=>sourceFlags[0];
        public int Priority=>sourceFlags[2];
    }
    public struct Input {
        public int sector,clockFrame;
        public float progress,speedKmh,gap;
        public bool playerAhead,playing;
    }
    public static int Bank(uint enemy)=>enemy<=13?(int)enemy:
        enemy>=14&&enemy<=16?(int)enemy+1:enemy>=18&&enemy<=29?(int)enemy:enemy==30?14:-1;
    private Rule[] rules=Array.Empty<Rule>();
    private readonly int[] counts=new int[13],starts=new int[13];
    private readonly sbyte[] ready=new sbyte[13],handled=new sbyte[13],last=new sbyte[13];
    private readonly short[] cooldown=new short[13];
    private sbyte[] used,remaining;
    private int active=-1,lastClip=-1,activeEvent,lastPriority=-1,voiceState,voiceMode,previousVoiceState;
    private short hold,restoreDelay=-1;
    private bool started;
    private ulong seed;
    private readonly int[] order=new int[256],candidates=new int[256];
    internal int ActiveIndex=>active;
    internal Rule Active=>active<0?null:rules[active];
    internal int RuleCount=>rules.Length;

    public void Reset(Rule[] source,ulong randomSeed){
        var sorted=new List<Rule>();
        for(int e=1;e<=12;++e){
            starts[e]=sorted.Count;counts[e]=0;
            foreach(var rule in source){
                if(rule.sourceFlags==null||rule.sourceFlags.Length!=28||rule.sourceFlags[1]!=rule.@event)
                    throw new ArgumentException("Invalid original speech rule.");
                if(rule.@event==e){sorted.Add(rule);++counts[e];}
            }
        }
        if(sorted.Count!=source.Length||sorted.Count>127)throw new ArgumentException("Invalid original speech bank.");
        rules=sorted.ToArray();used=new sbyte[rules.Length];remaining=new sbyte[rules.Length];
        for(int i=0;i<rules.Length;++i)remaining[i]=unchecked((sbyte)rules[i].sourceFlags[3]);
        Array.Clear(ready,0,13);Array.Clear(handled,0,13);Array.Clear(cooldown,0,13);
        for(int e=0;e<13;++e)last[e]=-1;
        handled[6]=handled[7]=1;cooldown[6]=cooldown[7]=30;cooldown[10]=90;
        active=lastClip=-1;activeEvent=0;lastPriority=-1;started=false;
        hold=0;voiceMode=voiceState=previousVoiceState=0;restoreDelay=-1;seed=randomSeed;
    }
    private int Random(){
        seed=unchecked(seed*0x5851f42d4c957f2dUL+1);
        return (int)((seed>>32)&0x7fffffff);
    }
    private int Jitter()=>Random()%61-30;
    private void Shuffle(int count){
        for(int i=0;i<count;++i)order[i]=i;
        for(int i=0;i<count-1;++i){int j=i+Random()%(count-i);int value=order[i];order[i]=order[j];order[j]=value;}
    }
    private bool Allows(int index,Input input){
        // 1AF9C0 returns true even after an optional-condition mismatch.
        // Every shipped row has zero optional conditions.
        return remaining[index]!=0&&rules[index].sourceFlags[4+input.sector]!=0;
    }
    private void ResetEvents(int a,int b,short delay){
        ready[a]=ready[b]=handled[a]=handled[b]=0;cooldown[a]=cooldown[b]=delay;
    }
    private void Ambient(int e){
        if(activeEvent==e)return;
        if(cooldown[e]==0){ready[e]=1;handled[e]=0;}
        else cooldown[e]=unchecked((short)(cooldown[e]-1));
    }
    private void Passing(int e,bool side,float gap){
        if(cooldown[e]>30){--cooldown[e];return;}
        if(!started)return;
        if(side){
            if(gap>=4.3f){
                if(cooldown[e]==0)ready[e]=1;
                else --cooldown[e];
            }
        }else {ready[e]=handled[e]=0;cooldown[e]=30;}
    }
    public Rule Tick(Input input){
        if(input.sector<0||input.sector>3)throw new ArgumentOutOfRangeException(nameof(input.sector));
        if(input.sector>=2&&voiceMode==0){voiceMode=6;if(restoreDelay==-1)voiceState=6;}
        previousVoiceState=voiceState;
        if(input.clockFrame==90)ready[1]=1;
        else if(input.clockFrame<90)return null;
        float gap=Math.Abs(input.gap);
        if(gap>4.3f&&gap<80)Ambient(input.playerAhead?2:3);
        else ResetEvents(2,3,120);
        if(!input.playerAhead&&gap>=90)Ambient(5);
        else if(input.playerAhead&&gap>=81)Ambient(4);
        else ResetEvents(4,5,150);
        Passing(6,input.playerAhead,gap);Passing(7,!input.playerAhead,gap);
        if(input.progress>.9f&&gap<50){
            if(input.playerAhead){if(handled[9]==0)ready[8]=1;}
            else if(handled[8]==0)ready[9]=1;
        }
        if(input.speedKmh>100)Ambient(10);
        else {ready[10]=handled[10]=0;cooldown[10]=90;}
        int candidateCount=0;
        for(int e=1;e<=10;++e){
            int count=counts[e],start=starts[e];
            if(count==0||ready[e]==0||handled[e]!=0)continue;
            Shuffle(count);int selected=-1;
            for(int pass=0;pass<2&&selected<0;++pass){
                if(pass==1)Array.Clear(used,start,count);
                for(int n=0;n<count;++n){
                    int index=start+order[n];
                    if(pass==0&&used[index]!=0)continue;
                    if(lastClip>=0&&rules[index].clip==rules[lastClip].clip)continue;
                    if(Allows(index,input)){selected=index;break;}
                }
            }
            if(selected<0&&last[e]>=0&&Allows(last[e],input))selected=last[e];
            if(selected>=0)candidates[candidateCount++]=selected;
        }
        for(int i=starts[11];i<rules.Length&&rules[i].@event==11;++i)
            if(Allows(i,input))candidates[candidateCount++]=i;
        int next=-1;
        if(candidateCount>0){
            Shuffle(candidateCount);
            for(int i=0;i<candidateCount;++i){
                int index=candidates[order[i]];
                if(next<0||rules[index].Priority>rules[next].Priority)next=index;
            }
        }
        if(active>=0&&!input.playing){
            var completed=rules[active];activeEvent=0;lastPriority=completed.Priority;hold=30;
            cooldown[completed.@event]=(short)Jitter();
            if(!started&&(completed.@event==1||completed.@event==11)){
                started=true;next=-1;cooldown[2]=cooldown[3]=120;cooldown[4]=cooldown[5]=150;
            }
            int chain=unchecked((sbyte)completed.sourceFlags[10]);
            if(chain!=-1){
                next=-1;
                for(int i=rules.Length-1;i>=0;--i)if(rules[i].Id==chain){next=i;break;}
                hold=0;
            }
            active=-1;restoreDelay=60;
        }
        if(restoreDelay>0)--restoreDelay;
        if(restoreDelay==0){restoreDelay=-1;voiceState=voiceMode;}
        if(hold>0)--hold;
        if(next<0)return null;
        if(active>=0&&rules[active].Priority>=rules[next].Priority)return null;
        if(hold!=0&&lastPriority>=rules[next].Priority)return null;
        if(active>=0)cooldown[rules[active].@event]=(short)Jitter();
        var chosen=rules[next];active=next;activeEvent=chosen.@event;lastPriority=chosen.Priority;lastClip=next;
        if(remaining[next]>0)--remaining[next];
        handled[activeEvent]=unchecked((sbyte)(handled[activeEvent]+1));
        used[next]=unchecked((sbyte)(used[next]+1));last[activeEvent]=(sbyte)next;
        cooldown[activeEvent]=-1;restoreDelay=-2;
        voiceState=chosen.sourceFlags[9]!=0?chosen.sourceFlags[9]:voiceMode;
        return chosen;
    }
#if IDAS3_STORY_VOICE_TEST
    internal byte[] Snapshot(){
        var data=new byte[0x64+rules.Length*2+8];
        Action<int,int> word=(offset,value)=>Array.Copy(BitConverter.GetBytes(value),0,data,offset,4);
        Action<int,short> half=(offset,value)=>Array.Copy(BitConverter.GetBytes(value),0,data,offset,2);
        word(0,active);data[4]=(byte)activeEvent;data[5]=unchecked((byte)lastPriority);word(8,lastClip);
        data[0x18]=started?(byte)1:(byte)0;
        for(int i=0;i<13;++i){
            data[0x19+i]=unchecked((byte)ready[i]);data[0x26+i]=unchecked((byte)handled[i]);
            data[0x33+i]=unchecked((byte)last[i]);half(0x40+i*2,cooldown[i]);
        }
        half(0x5a,hold);data[0x5c]=(byte)voiceMode;data[0x5d]=(byte)voiceState;data[0x5e]=(byte)previousVoiceState;half(0x60,restoreDelay);
        for(int i=0;i<rules.Length;++i){data[0x64+i]=unchecked((byte)used[i]);data[0x64+rules.Length+i]=unchecked((byte)remaining[i]);}
        Array.Copy(BitConverter.GetBytes(seed),0,data,0x64+rules.Length*2,8);return data;
    }
#endif
}
