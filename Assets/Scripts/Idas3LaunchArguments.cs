using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

internal static class Idas3LaunchArguments
{
    private static string[] cached;
#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal", CallingConvention=CallingConvention.Cdecl)]
    private static extern int Idas3CopyProcessArgument(int index, [Out] byte[] output, int capacity);
#endif
    internal static string[] GetArguments()
    {
        if(cached!=null)return cached;
#if UNITY_IOS && !UNITY_EDITOR
        var arguments=new List<string>();
        for(int index=0;;++index){
            int length=Idas3CopyProcessArgument(index,null,0);
            if(length<0)break;
            var bytes=new byte[length+1];
            Idas3CopyProcessArgument(index,bytes,bytes.Length);
            arguments.Add(Encoding.UTF8.GetString(bytes,0,length));
        }
        return cached=arguments.ToArray();
#else
        return cached=Environment.GetCommandLineArgs();
#endif
    }
}
