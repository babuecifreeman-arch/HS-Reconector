using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Forms;

namespace HearthstoneReconnector {
    internal sealed class ConnectionSnapshot {
        internal int Pid;
        internal long Started;
        internal string Path, LoggedEndpoint;
        internal List<GameConnection> Rows;
    }
    internal sealed class ConnectionObservation {
        long started; int pid;
        readonly Dictionary<string,DateTime> appeared=new Dictionary<string,DateTime>();
        readonly HashSet<string> initial=new HashSet<string>();
        internal void Update(int process,long birth,List<GameConnection> rows,DateTime now) {
            bool reset=process!=pid || birth!=started;
            if(reset) { appeared.Clear();initial.Clear();pid=process;started=birth; }
            var present=new HashSet<string>();
            foreach(var row in rows) {
                string key=Key(row);present.Add(key);
                if(reset)initial.Add(key);
                if(!appeared.ContainsKey(key))appeared[key]=now;
            }
            foreach(string key in new List<string>(appeared.Keys))if(!present.Contains(key)){appeared.Remove(key);initial.Remove(key);}
        }
        internal bool IsNew(GameConnection row,DateTime now) {
            DateTime seen;string key=Key(row);
            return appeared.TryGetValue(key,out seen) && !initial.Contains(key) && now-seen<TimeSpan.FromMinutes(10);
        }
        internal static string Key(GameConnection row) { return row.LocalAddress+"|"+row.LocalPort+"|"+row.RemoteAddress+"|"+row.RemotePort; }
        internal static GameConnection Parse(string key) {
            string[] parts=key.Split('|');IPAddress local,remote;int lp,rp;
            if(parts.Length!=4 || !IPAddress.TryParse(parts[0],out local) || !int.TryParse(parts[1],out lp) || !IPAddress.TryParse(parts[2],out remote) || !int.TryParse(parts[3],out rp) || lp<1 || lp>65535 || rp<1 || rp>65535)throw new ArgumentException("Некорректное соединение.");
            return new GameConnection{LocalAddress=local.ToString(),LocalPort=lp,RemoteAddress=remote.ToString(),RemotePort=rp};
        }
    }
    internal sealed class ConnectionMemory {
        internal static readonly string DefaultFile=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HearthstoneReconnector","connections-v1.txt");
        readonly string file;
        internal sealed class Profile { internal string Path,Address;internal int Port;internal DateTime Confirmed; }
        internal ConnectionMemory():this(DefaultFile){}
        internal ConnectionMemory(string path){file=path;}
        internal List<Profile> Load() {
            var result=new List<Profile>();
            try {
                if(!File.Exists(file))return result;
                foreach(string line in File.ReadAllLines(file,Encoding.UTF8)) {
                    try {
                        string[] p=line.Split('\t');IPAddress address;int port;long ticks;
                        if(p.Length!=4 || !IPAddress.TryParse(p[1],out address) || !int.TryParse(p[2],out port) || port<1 || port>65535 || !long.TryParse(p[3],out ticks))continue;
                        var time=new DateTime(ticks,DateTimeKind.Utc);
                        if(time>DateTime.UtcNow.AddMinutes(5) || time<DateTime.UtcNow.AddDays(-30))continue;
                        result.Add(new Profile{Path=Encoding.UTF8.GetString(Convert.FromBase64String(p[0])),Address=address.ToString(),Port=port,Confirmed=time});
                    }catch{}
                }
            }catch(Exception ex){ConnectionManager.Log("MEMORY_READ "+ex.Message);}
            return result;
        }
        internal void Confirm(string path,GameConnection target,string evidence) {
            if(evidence!="MATCH_RESTORED" && evidence!="USER_CONFIRMED")throw new ArgumentException("Нет подтверждения возврата в матч.");
            var profiles=Load();profiles.RemoveAll(delegate(Profile p){return SamePath(p.Path,path)&&p.Address==target.RemoteAddress&&p.Port==target.RemotePort;});
            profiles.Insert(0,new Profile{Path=path,Address=target.RemoteAddress,Port=target.RemotePort,Confirmed=DateTime.UtcNow});
            if(profiles.Count>128)profiles.RemoveRange(128,profiles.Count-128);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
            string temporary=file+".tmp";
            using(var writer=new StreamWriter(temporary,false,new UTF8Encoding(false)))foreach(var p in profiles)writer.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Path))+"\t"+p.Address+"\t"+p.Port+"\t"+p.Confirmed.Ticks);
            if(File.Exists(file))File.Replace(temporary,file,null);else File.Move(temporary,file);
            ConnectionManager.Log("MEMORY_CONFIRMED endpoint="+target.RemoteAddress+":"+target.RemotePort+" evidence="+evidence);
        }
        internal void Clear(){if(File.Exists(file))File.Delete(file);}
        static bool SamePath(string a,string b){return string.Equals(a,b,StringComparison.OrdinalIgnoreCase);}
        internal bool Known(string path,GameConnection row){foreach(var p in Load())if(SamePath(p.Path,path)&&p.Address==row.RemoteAddress&&p.Port==row.RemotePort)return true;return false;}
        internal bool NeedsConfirmation(string path,GameConnection row){return !Known(path,row);}
        internal bool KnownPort(string path,int port){foreach(var p in Load())if(SamePath(p.Path,path)&&p.Port==port)return true;return false;}
        internal GameConnection Select(ConnectionSnapshot snapshot,ConnectionObservation observation,out string reason) {
            // A current log match takes precedence even when another connection uses port 3724.
            GameConnection found=null;int count=0;
            foreach(var row in snapshot.Rows)if(row.RemoteAddress+":"+row.RemotePort==snapshot.LoggedEndpoint){found=row;count++;}
            if(count>0){reason="GAME_LOG";return count==1?found:null;}
            var known=new List<GameConnection>();
            foreach(var row in snapshot.Rows)if(Known(snapshot.Path,row))known.Add(row);
            if(known.Count>0) {
                // A known service endpoint or a reused endpoint must not win by port alone.
                reason="MEMORY_AND_NEW_SOCKET";
                return known.Count==1 && observation.IsNew(known[0],DateTime.UtcNow)?known[0]:null;
            }
            found=GameConnections.Select(snapshot.Rows,null);reason=found==null?"MANUAL_REQUIRED":"UNIQUE_3724";return found;
        }
    }
    internal sealed class ConnectionPicker : Form {
        readonly ListBox choices=new ListBox();readonly Button use=new Button();
        internal GameConnection Selected;
        internal ConnectionPicker(ConnectionSnapshot snapshot,ConnectionObservation observation,ConnectionMemory memory) {
            Text="Выбор соединения матча";StartPosition=FormStartPosition.CenterScreen;ClientSize=new Size(720,320);MinimizeBox=false;MaximizeBox=false;FormBorderStyle=FormBorderStyle.FixedDialog;
            var label=new Label{Text="Соединение матча не определено. Выберите его по игровому журналу.\nОтключение служебного соединения может вывести из игры. Автоматического перебора нет.",Location=new Point(15,12),Size=new Size(690,48)};
            choices.Location=new Point(15,70);choices.Size=new Size(690,185);choices.Font=new Font(FontFamily.GenericMonospace,9);choices.HorizontalScrollbar=true;
            foreach(var row in snapshot.Rows)choices.Items.Add(row+"   "+(observation.IsNew(row,DateTime.UtcNow)?"новое":"было при запуске")+(memory.Known(snapshot.Path,row)?"; подтверждённый адрес":memory.KnownPort(snapshot.Path,row.RemotePort)?"; знакомый порт":""));
            choices.SelectedIndex=-1;
            use.Text="Отключить выбранное";use.Location=new Point(355,275);use.Size=new Size(190,30);use.Enabled=false;
            choices.SelectedIndexChanged+=delegate{use.Enabled=choices.SelectedIndex>=0;};
            use.Click+=delegate{if(choices.SelectedIndex>=0){Selected=snapshot.Rows[choices.SelectedIndex];DialogResult=DialogResult.OK;Close();}};
            var cancel=new Button{Text="Отмена",Location=new Point(555,275),Size=new Size(150,30),DialogResult=DialogResult.Cancel};CancelButton=cancel;
            Controls.AddRange(new Control[]{label,choices,use,cancel});
        }
    }
}
