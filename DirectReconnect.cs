using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace HearthstoneReconnector
{
    internal static class DirectReconnect
    {
        [StructLayout(LayoutKind.Sequential)] internal struct TcpRow { internal uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort; }
        [DllImport("iphlpapi.dll")] static extern uint SetTcpEntry(ref TcpRow row);
        internal static TcpRow MakeRow(GameConnection target)
        {
            var local = IPAddress.Parse(target.LocalAddress); var remote = IPAddress.Parse(target.RemoteAddress);
            if(local.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || remote.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new NotSupportedException("Точечный разрыв IPv6 не поддерживается системным API этой версии. Отключение отменено.");
            return new TcpRow { State = 12, LocalAddress = BitConverter.ToUInt32(local.GetAddressBytes(),0), RemoteAddress = BitConverter.ToUInt32(remote.GetAddressBytes(),0), LocalPort = SwapPort(target.LocalPort), RemotePort = SwapPort(target.RemotePort) };
        }
        static uint SwapPort(int port) { if(port<1 || port>65535) throw new ArgumentOutOfRangeException("port"); return (uint)(((port&255)<<8)|((port>>8)&255)); }
        internal static string Execute(int pid, EventWaitHandle cancel) { return Execute(pid,cancel,null,0); }
        internal static string Execute(int pid, EventWaitHandle cancel, string selectedKey, long expectedStart)
        {
            string attempt=Guid.NewGuid().ToString("N").Substring(0,8);
            string path; DateTime start;
            using(var process = Process.GetProcessById(pid)) {
                if(!process.ProcessName.Equals("Hearthstone",StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Процесс не является Hearthstone.");
                path=process.MainModule.FileName; start=process.StartTime;
                if(expectedStart!=0 && start.ToUniversalTime().Ticks!=expectedStart)throw new IOException("Процесс игры изменился после выбора.");
            }
            var before=GameConnections.Read(pid);
            string loggedEndpoint=GameConnections.ReadLoggedEndpoint(path);
            ConnectionManager.Log("RECONNECT_BASELINE attempt="+attempt+" pid="+pid+" socketCount="+before.Count+" loggedEndpoint="+(loggedEndpoint??"<none>"));
            foreach(var connection in before) {
                ConnectionManager.Log("CANDIDATE "+connection+" localIP="+connection.LocalAddress);
                ConnectionManager.Log("RECONNECT_SOCKET attempt="+attempt+" phase=BEFORE socket="+Describe(connection));
            }
            var target=selectedKey==null?GameConnections.Select(before,loggedEndpoint):ConnectionObservation.Parse(selectedKey);
            if(target!=null && !ContainsSocket(before,target))throw new IOException("Выбранное соединение уже исчезло. Обновите список.");
            if(target==null) throw new InvalidOperationException("Не удалось однозначно определить соединение матча. См. CANDIDATE в журнале.");
            ConnectionManager.Log("RECONNECT_TARGET attempt="+attempt+" socket="+Describe(target)+" source="+(selectedKey==null?"AUTO":"SELECTED"));
            var native=MakeRow(target);
            var log=GameLogCursor.Capture(path,attempt);
            if(cancel.WaitOne(0)) return "CANCELED|Операция отменена.";
            bool stillExists=false; foreach(var current in GameConnections.Read(pid)) if(current.SameSocket(target)) stillExists=true;
            if(!stillExists) throw new IOException("Соединение изменилось до отключения. Повторите попытку после восстановления игры.");
            using(var process=Process.GetProcessById(pid)) if(process.StartTime!=start) throw new IOException("Процесс игры изменился.");
            ConnectionManager.Log("DIRECT_BEGIN attempt="+attempt+" pid="+pid+" target="+Describe(target));
            uint code=SetTcpEntry(ref native);
            ConnectionManager.Log("SET_TCP_ENTRY attempt="+attempt+" result="+code);
            if(code!=0) throw new Win32Exception((int)code,"Не удалось разорвать выбранное соединение: "+new Win32Exception((int)code).Message);
            var clock=Stopwatch.StartNew(); bool oldGone=false, newFound=false;long replacementMs=-1;GameConnection replacement=null;
            var previous=before;
            while(clock.ElapsedMilliseconds<12000) {
                if(cancel.WaitOne(100)) return "CANCELED|Разрыв выполнен; ожидание отменено. Блокирующих правил нет.";
                using(var process=Process.GetProcessById(pid)) if(process.HasExited || process.StartTime!=start) throw new IOException("Клиент Hearthstone завершился после отключения.");
                var rows=GameConnections.Read(pid); bool present=false;
                LogSocketChanges(attempt,clock.ElapsedMilliseconds,previous,rows,before,target);
                previous=rows;
                foreach(var row in rows) {
                    if(row.SameSocket(target)) present=true;
                    else if(row.RemotePort==target.RemotePort && row.RemoteAddress==target.RemoteAddress && !ContainsSocket(before,row)) {
                        newFound=true;if(replacement==null){replacement=row;replacementMs=clock.ElapsedMilliseconds;ConnectionManager.Log("TARGET_REPLACEMENT attempt="+attempt+" elapsedMs="+replacementMs+" socket="+Describe(row));}
                    }
                }
                if(!present && !oldGone) { oldGone=true; ConnectionManager.Log("OLD_SOCKET_GONE attempt="+attempt+" elapsedMs="+clock.ElapsedMilliseconds); }
                bool matchStarted=log!=null && log.MatchStarted(attempt);
                if(oldGone && newFound && matchStarted) {
                    ConnectionManager.Log("MATCH_RESTORED attempt="+attempt+" elapsedMs="+clock.ElapsedMilliseconds+" logEndpoint="+(log.LastEndpoint??"<none>"));
                    try { new ConnectionMemory().Confirm(path,replacement??target,"MATCH_RESTORED"); } catch(Exception ex) { ConnectionManager.Log("MEMORY_SAVE "+ex.Message); }
                    return "MATCH_RESTORED|Новое TCP-соединение и SERVER_GAME_STARTED подтверждены.";
                }
            }
            ConnectionManager.Log("RECONNECT_TIMEOUT attempt="+attempt+" oldGone="+oldGone+" exactEndpointReplacement="+newFound+" logMatchStarted="+(log!=null&&log.HasMatchStarted)+" logEndpoint="+(log==null||log.LastEndpoint==null?"<none>":log.LastEndpoint)+" finalSocketCount="+previous.Count);
            foreach(var connection in previous) ConnectionManager.Log("RECONNECT_SOCKET attempt="+attempt+" phase=TIMEOUT socket="+Describe(connection)+" relation="+Relation(connection,target)+" existedBefore="+ContainsSocket(before,connection));
            if(oldGone && newFound) {
                if(replacementMs>=2000)try { new ConnectionMemory().Confirm(path,replacement??target,"DELAYED_SOCKET_REPLACEMENT"); } catch(Exception ex) { ConnectionManager.Log("MEMORY_SAVE "+ex.Message); }
                return "SOCKET_REPLACED|Новое TCP-соединение найдено. Возврат в матч по игровому журналу не подтверждён.";
            }
            throw new IOException(oldGone?"Старое соединение закрыто, но новое не найдено за 12 секунд. Проверьте состояние игры.":"Закрытие старого соединения не подтверждено. См. журнал.");
        }
        static string Describe(GameConnection row) { return row.LocalAddress+":"+row.LocalPort+"->"+row.RemoteAddress+":"+row.RemotePort; }
        static string Relation(GameConnection row,GameConnection target) {
            if(row.RemoteAddress==target.RemoteAddress && row.RemotePort==target.RemotePort)return "SAME_REMOTE_ENDPOINT";
            if(row.RemoteAddress==target.RemoteAddress)return "SAME_HOST_NEW_REMOTE_PORT";
            if(row.RemotePort==target.RemotePort)return "NEW_HOST_SAME_REMOTE_PORT";
            return "NEW_HOST_AND_REMOTE_PORT";
        }
        static void LogSocketChanges(string attempt,long elapsed,List<GameConnection> previous,List<GameConnection> current,List<GameConnection> baseline,GameConnection target) {
            foreach(var row in previous)if(!ContainsSocket(current,row))ConnectionManager.Log("SOCKET_REMOVED attempt="+attempt+" elapsedMs="+elapsed+" socket="+Describe(row)+" relation="+Relation(row,target)+" existedBefore="+ContainsSocket(baseline,row));
            foreach(var row in current)if(!ContainsSocket(previous,row))ConnectionManager.Log("SOCKET_ADDED attempt="+attempt+" elapsedMs="+elapsed+" socket="+Describe(row)+" relation="+Relation(row,target)+" existedBefore="+ContainsSocket(baseline,row));
        }
        static bool ContainsSocket(List<GameConnection> rows,GameConnection target) { foreach(var row in rows) if(row.SameSocket(target))return true;return false; }
        internal static void SelfTest() {
            var target=new GameConnection{LocalAddress="192.0.2.1",RemoteAddress="198.51.100.2",LocalPort=50663,RemotePort=3724};
            var row=MakeRow(target);
            if(row.State!=12 || row.RemotePort!=0x8c0e || row.LocalAddress!=0x010200c0 || row.RemoteAddress!=0x026433c6)throw new Exception("Native tuple encoding failed.");
            var replacement=new GameConnection{LocalAddress=target.LocalAddress,RemoteAddress=target.RemoteAddress,LocalPort=50664,RemotePort=target.RemotePort};
            if(target.SameSocket(replacement)||ContainsSocket(new List<GameConnection>{target},replacement))throw new Exception("Replacement isolation failed.");
            bool refused=false;try {MakeRow(new GameConnection{LocalAddress="::1",RemoteAddress="2001:db8::1",LocalPort=1234,RemotePort=3724});}catch(NotSupportedException){refused=true;}
            if(!refused)throw new Exception("IPv6 refusal failed.");
            GameConnections.SelfTest();
            ConnectionMemory.SelfTest();
        }
        internal static void NativeLoopbackTest() {
            var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();
            try{using(var first=new System.Net.Sockets.TcpClient())using(var second=new System.Net.Sockets.TcpClient()){
                first.Connect((IPEndPoint)listener.LocalEndpoint);
                using(var firstServer=listener.AcceptTcpClient()){
                    second.Connect((IPEndPoint)listener.LocalEndpoint);
                    using(var secondServer=listener.AcceptTcpClient()){
                        var local=(IPEndPoint)first.Client.LocalEndPoint;var remote=(IPEndPoint)first.Client.RemoteEndPoint;
                        var native=MakeRow(new GameConnection{LocalAddress=local.Address.ToString(),RemoteAddress=remote.Address.ToString(),LocalPort=local.Port,RemotePort=remote.Port});
                        uint code=SetTcpEntry(ref native);
                        if(code==5 || code==317){Console.WriteLine("SKIP: native loopback close needs elevated rights.");return;}
                        if(code!=0)throw new Win32Exception((int)code);
                        second.GetStream().WriteByte(42);secondServer.ReceiveTimeout=1500;
                        if(secondServer.GetStream().ReadByte()!=42)throw new Exception("Unrelated socket affected.");
                        firstServer.ReceiveTimeout=1500;bool closed=false;
                        try{closed=firstServer.GetStream().ReadByte()==-1;}catch(IOException){closed=true;}
                        if(!closed)throw new Exception("Selected socket still open.");
                        Console.WriteLine("PASS: selected loopback TCP closed, unrelated TCP preserved.");
                    }
                }
            }}finally{listener.Stop();}
        }
    }
    internal sealed class GameLogCursor
    {
        readonly string file; long position; string carry=""; bool matchStarted; string lastEndpoint; long pendingBytes; DateTime lastProgressLog=DateTime.MinValue,lastErrorLog=DateTime.MinValue; string lastError;
        internal bool HasMatchStarted { get { return matchStarted; } }
        internal string LastEndpoint { get { return lastEndpoint; } }
        GameLogCursor(string path,long offset){file=path;position=offset;}
        internal static GameLogCursor Capture(string gamePath) { return Capture(gamePath,null); }
        internal static GameLogCursor Capture(string gamePath,string attempt) {
            try {
                var root=Path.Combine(Path.GetDirectoryName(gamePath),"Logs");
                string best=null; DateTime newest=DateTime.MinValue;
                if(!Directory.Exists(root))return null;
                var dirs=new List<string>(Directory.GetDirectories(root));dirs.Add(root);
                foreach(var dir in dirs)foreach(var name in new[]{"GameNetLogger.log","Hearthstone.log"}) {
                    string file=Path.Combine(dir,name);if(File.Exists(file)&&File.GetLastWriteTimeUtc(file)>newest){best=file;newest=File.GetLastWriteTimeUtc(file);}
                }
                if(best==null || newest<DateTime.UtcNow.AddHours(-1)){ConnectionManager.Log("GAME_LOG_CAPTURE attempt="+(attempt??"<none>")+" result=NONE");return null;}
                long length=new FileInfo(best).Length;
                ConnectionManager.Log("GAME_LOG_CAPTURE attempt="+(attempt??"<none>")+" file="+best+" offset="+length+" modifiedUtc="+newest.ToString("o"));
                return new GameLogCursor(best,length);
            }catch(Exception ex){ConnectionManager.Log("GAME_LOG_CAPTURE attempt="+(attempt??"<none>")+" error="+ex.Message);return null;}
        }
        internal bool MatchStarted(){return MatchStarted(null);}
        internal bool MatchStarted(string attempt){
            try{using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
                if(stream.Length<position){ConnectionManager.Log("GAME_LOG_TRUNCATED attempt="+(attempt??"<none>")+" file="+file+" oldOffset="+position+" newLength="+stream.Length);position=0;carry="";}
                if(stream.Length==position)return matchStarted;
                long end=stream.Length;long added=end-position;stream.Seek(position,SeekOrigin.Begin);
                string text;using(var reader=new StreamReader(stream,Encoding.UTF8)){text=reader.ReadToEnd();}
                position=end;pendingBytes+=added;
                string searchable=carry+text;
                bool newStarted=!matchStarted&&searchable.Contains("SERVER_GAME_STARTED");
                string endpoint=GameConnections.ParseEndpoint(searchable);
                if(newStarted)matchStarted=true;
                bool endpointChanged=endpoint!=null&&endpoint!=lastEndpoint;
                if(endpointChanged)lastEndpoint=endpoint;
                carry=searchable.Length>512?searchable.Substring(searchable.Length-512):searchable;
                if(newStarted || endpointChanged || DateTime.UtcNow-lastProgressLog>=TimeSpan.FromSeconds(1)){
                    ConnectionManager.Log("GAME_LOG_DELTA attempt="+(attempt??"<none>")+" bytes="+pendingBytes+" serverGameStarted="+newStarted+" endpoint="+(endpointChanged?endpoint:"<none>")+" file="+file);
                    pendingBytes=0;lastProgressLog=DateTime.UtcNow;
                }
                return matchStarted;
            }}catch(Exception ex){
                if(ex.Message!=lastError||DateTime.UtcNow-lastErrorLog>=TimeSpan.FromSeconds(2)){
                    ConnectionManager.Log("GAME_LOG_READ attempt="+(attempt??"<none>")+" error="+ex.Message);
                    lastError=ex.Message;lastErrorLog=DateTime.UtcNow;
                }
                return matchStarted;
            }
        }
    }
}
