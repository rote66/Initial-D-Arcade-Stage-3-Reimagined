using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace Idas3.Multiplayer
{
    public sealed partial class Idas3MultiplayerSmoke
    {
        private static bool IosLanCheck=>Array.IndexOf(Idas3LaunchArguments.GetArguments(),"-idas3-ios-lan-check")>=0;
        private IEnumerator RunIosLan()
        {
            Check(peerRoot.Length>0,"LAN check requires an isolated peer output folder.");
            session.ConfigureLocalTest(port,role=="host"?"IOS HOST":"LAN PEER");
            Check(session.Available&&session.TransportIndex==1,"LAN transport initialized");
            session.SetCar(role=="host"?0:8);
            session.SetRaceOptions(0,false,false,true);
            menu.SetOpen(true);
            if(role=="host"){
                session.HostRoom();roomCode=session.RoomCode;Phase("hosting");
                File.WriteAllText(Path.Combine(root,"room.json"),"{\"code\":\""+roomCode+"\"}");
            }else{Phase("joining");session.JoinRoom(address);}
            yield return Until(()=>session.HandshakeComplete&&session.CanReady,90,"Compatible LAN handshake and challenger did not complete.");
            Check(CarSelectionAgrees(),"Both cars were not synchronized.");
            Check(session.LocalChoice.Equals(session.RemoteChoice),"Course and conditions differ.");
            menu.SetOpen(true);session.SetReady(true);Phase("ready");
            if(role=="host"){
                yield return Until(()=>session.CanStart,30,"Both LAN peers did not become ready.");session.StartRace();
            }
            yield return Until(()=>session.IsRacing&&session.RaceReleased,45,"Synchronized native race did not start.");
            yield return Frames(2);lobbyAutoClosed=!menu.IsOpen;Check(lobbyAutoClosed,"Lobby did not close on race start.");
            driving=true;Phase("racing");
            yield return Until(()=>raceObserved&&lastLocal.raceTicks-firstLocalTick>=600&&lastRemote.raceTicks-firstRemoteTick>=600&&greatestReceived>=100,90,"LAN race did not exchange 100 snapshots and advance both cars for 600 ticks.");
            Check(localTravel>10&&remoteTravel>10,"Both LAN cars did not move.");
            Check(maxPeerMainRanges>0,"Remote car was not submitted to the main-view renderer.");
            menu.SetOpen(true);yield return Frames(3);liveMenuReopened=menu.IsOpen;
            Check(liveMenuReopened&&session.IsRacing,"Live LAN menu could not reopen.");menu.SetOpen(false);
            File.WriteAllText(Path.Combine(root,"race-verified.json"),"{\"passed\":true}");
            yield return CheckConnectionDrop();Phase("ios-lan-complete");Finish(true,null);
        }
    }
}
