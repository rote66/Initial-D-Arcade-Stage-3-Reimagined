using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

// Bounded background I/O: neither frame sampling nor Unity log callbacks wait
// for the filesystem. Each stream keeps at most one rotated predecessor.
internal sealed class Idas3DiagnosticWriter : IDisposable
{
    internal enum Channel {Runtime,Performance}
    private struct Entry {internal Channel channel;internal string text;}
    internal static readonly string[] Names={"runtime.log","runtime.previous.log","performance.jsonl","performance.previous.jsonl"};
    private readonly BlockingCollection<Entry> queue=new BlockingCollection<Entry>(128);
    private readonly Thread worker;
    private readonly string folder;
    private readonly long maximumBytes;
    private int closing,dropped;
    private string error;
    internal string Error=>Volatile.Read(ref error);
    internal int Dropped=>Volatile.Read(ref dropped);
    internal Idas3DiagnosticWriter(string directory,long limit=1024*1024){
        folder=Path.GetFullPath(directory);maximumBytes=Math.Max(256,limit);
        CheckPath(folder);
        Directory.CreateDirectory(folder);
        foreach(string name in Names)CheckPath(Path.Combine(folder,name));
        worker=new Thread(WriteLoop){IsBackground=true,Name="IDAS3 diagnostic files"};
        worker.Start();
    }
    internal void Enqueue(Channel channel,string text){
        if(text==null||Volatile.Read(ref closing)!=0)return;
        if(text.Length>8192)text=text.Substring(0,8192)+" [truncated]";
        try{if(!queue.TryAdd(new Entry{channel=channel,text=text}))Interlocked.Increment(ref dropped);}
        catch(InvalidOperationException){ }
    }
    private void WriteLoop(){
        StreamWriter[] streams=new StreamWriter[2];long[] sizes=new long[2];
        try{
            foreach(var entry in queue.GetConsumingEnumerable()){
                int at=(int)entry.channel;
                string file=Path.Combine(folder,at==0?Names[0]:Names[2]);
                string previous=Path.Combine(folder,at==0?Names[1]:Names[3]);
                if(streams[at]==null){
                    CheckPath(file);
                    sizes[at]=File.Exists(file)?new FileInfo(file).Length:0;
                    streams[at]=Open(file);
                }
                int bytes=Encoding.UTF8.GetByteCount(entry.text+Environment.NewLine);
                if(sizes[at]>0&&sizes[at]+bytes>maximumBytes){
                    streams[at].Dispose();streams[at]=null;
                    CheckPath(file);CheckPath(previous);
                    if(File.Exists(previous))File.Delete(previous);
                    File.Move(file,previous);streams[at]=Open(file);sizes[at]=0;
                }
                streams[at].WriteLine(entry.text);sizes[at]+=bytes;
            }
        }catch(Exception e){Volatile.Write(ref error,e.GetType().Name+": "+e.Message);}
        finally{foreach(var stream in streams)try{stream?.Dispose();}catch(Exception){ }}
    }
    private static StreamWriter Open(string file)=>new StreamWriter(new FileStream(file,FileMode.Append,
        FileAccess.Write,FileShare.Read),new UTF8Encoding(false)){AutoFlush=true};
    public void Dispose(){
        if(Interlocked.Exchange(ref closing,1)==0)queue.CompleteAdding();
        if(!worker.Join(5000))throw new IOException("Diagnostic writer is still stopping.");
    }
    internal static void DeleteLogs(string directory){
        string full=Path.GetFullPath(directory);CheckPath(full);
        if(!Directory.Exists(full))return;
        foreach(string name in Names){
            string file=Path.Combine(full,name);CheckPath(file);
            if(File.Exists(file))File.Delete(file);
        }
        if(Directory.GetFileSystemEntries(full).Length==0)Directory.Delete(full);
    }
    private static void CheckPath(string path){
        try{
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("Refusing linked diagnostic path: "+path);
        }catch(FileNotFoundException){ }
        catch(DirectoryNotFoundException){ }
    }
}
