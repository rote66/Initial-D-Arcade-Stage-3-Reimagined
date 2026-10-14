using System;
using System.IO;
using System.Linq;

internal static class DiagnosticWriterDriver
{
    private static int checks;
    private static void Check(bool value,string message){
        if(!value)throw new InvalidOperationException(message);
        ++checks;Console.WriteLine("PASS\t"+message);
    }
    public static int Main(string[] args){
        string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
        try{
            string logs=Path.Combine(root,"logs");
            Idas3DiagnosticWriter.DeleteLogs(logs);
            Check(!Directory.Exists(logs),"Disabled logging does not create a directory");
            using(var writer=new Idas3DiagnosticWriter(logs,512)){
                writer.Enqueue(Idas3DiagnosticWriter.Channel.Runtime,"runtime marker");
                writer.Enqueue(Idas3DiagnosticWriter.Channel.Performance,"{\"fps\":60}");
            }
            Check(File.ReadAllText(Path.Combine(logs,"runtime.log")).Contains("runtime marker"),"Shutdown flushes runtime logs");
            Check(File.ReadAllText(Path.Combine(logs,"performance.jsonl")).Contains("\"fps\":60"),"Performance uses a separate stream");
            using(var writer=new Idas3DiagnosticWriter(logs,512)){
                for(int i=0;i<10000;++i)writer.Enqueue(Idas3DiagnosticWriter.Channel.Runtime,i+" "+new string('x',100));
            }
            using(var writer=new Idas3DiagnosticWriter(logs,512)){
                writer.Enqueue(Idas3DiagnosticWriter.Channel.Performance,new string('z',16000));
            }
            Check(File.Exists(Path.Combine(logs,"runtime.previous.log")),"Runtime stream rotates");
            Check(new FileInfo(Path.Combine(logs,"runtime.log")).Length<=512+8192*4,"Runtime stream has a fixed size bound");
            Check(new FileInfo(Path.Combine(logs,"runtime.previous.log")).Length<=512+8192*4,"Rotated stream has a fixed size bound");
            Check(File.ReadAllText(Path.Combine(logs,"performance.jsonl")).Length<8300&&
                File.ReadAllText(Path.Combine(logs,"performance.jsonl")).Contains("[truncated]"),"Oversized messages are truncated");
            Check(Directory.GetFiles(logs).Length<=4,"Rotation never creates unlimited archives");
            File.WriteAllText(Path.Combine(logs,"keep.txt"),"unrelated");
            Idas3DiagnosticWriter.DeleteLogs(logs);
            Check(Directory.GetFiles(logs).Select(Path.GetFileName).SequenceEqual(new[]{"keep.txt"}),"Disable removes only owned log files");
            Check(File.ReadAllText(Path.Combine(logs,"keep.txt"))=="unrelated","Unrelated file is unchanged");
            File.Delete(Path.Combine(logs,"keep.txt"));Idas3DiagnosticWriter.DeleteLogs(logs);
            Check(!Directory.Exists(logs),"Empty diagnostic directory is deleted");
            var stopped=new Idas3DiagnosticWriter(logs);
            stopped.Enqueue(Idas3DiagnosticWriter.Channel.Runtime,"last record");
            stopped.Dispose();Idas3DiagnosticWriter.DeleteLogs(logs);
            stopped.Enqueue(Idas3DiagnosticWriter.Channel.Runtime,"must not recreate");stopped.Dispose();
            Check(!Directory.Exists(logs),"Stopped callbacks cannot recreate deleted files");
            if(args.Length>1){
                bool refused=false;
                try{Idas3DiagnosticWriter.DeleteLogs(args[1]);}catch(IOException){refused=true;}
                Check(refused,"Linked diagnostic folder is refused");
                refused=false;
                try{using(var writer=new Idas3DiagnosticWriter(args[1])){ }}catch(IOException){refused=true;}
                Check(refused,"Linked diagnostic folder cannot start a writer");
            }
            Console.WriteLine("PASS "+checks+" diagnostic writer checks");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
