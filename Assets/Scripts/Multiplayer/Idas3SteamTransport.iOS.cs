#if UNITY_IOS
using System;
using System.Collections.Generic;
namespace Idas3.Multiplayer
{
    // Keep the offline session's API without linking or initializing Steam.
    public sealed class Idas3SteamTransport : IIdas3MatchmakingTransport, IIdas3RegionalMatchmakingTransport
    {
        public const uint DevelopmentAppId=480;
        public const string GameNamespace="idas3-ios-offline",TransportProtocol="1";
        public const int MaxPayloadBytes=65536;
        public string Kind=>"Offline";
        public bool Available=>false;
        public bool Connected=>false;
        public bool IsHost=>false;
        public string LocalId=>"";
        public string LocalName=>"Driver";
        public string RemoteId=>"";
        public string RemoteName=>"";
        public string RoomCode=>"";
        public string Status=>"Online battles are unavailable in this iOS build.";
        public IReadOnlyList<Idas3Room> Rooms=>Array.Empty<Idas3Room>();
        public bool IsBusy=>false;
        public bool InLobby=>false;
        public ulong RoomOrder=>0;
        public int RoomMembers=>0;
        public string BuildCompatibility {get;set;}="";
        public bool ActivityRequested {get;set;}
        public bool PublishActivity {get;set;}
        public string ActivityState {get;set;}="offline";
        public Idas3OnlineActivity Activity=>default;
        public event Action<byte[]> Message {add{} remove{}}
        public event Action PeerChanged {add{} remove{}}
        public event Action<string> Error {add{} remove{}}
        public bool Initialize()=>false;
        public void Host(string name){}
        public void HostQuickMatch(string name){}
        public void Join(string code){}
        public void Browse(){}
        public void BrowseQuickMatch(int distance){}
        public void Send(byte[] data,bool reliable){}
        public void Poll(){}
        public void Leave(){}
        public void Dispose(){}
        internal double[] DiagnosticMembershipCost()=>Array.Empty<double>();
    }
}
#endif
