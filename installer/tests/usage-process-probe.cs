using System;
using System.Net;
using System.Reflection;
class UsageProcessProbe {
 static int Main(string[] args) {
  try {
   Console.WriteLine("Initial TLS="+ServicePointManager.SecurityProtocol);
   if(args.Length>2 && args[2]=="system") ServicePointManager.SecurityProtocol=(SecurityProtocolType)0;
   if(args.Length>2 && args[2]=="tls12") ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
   Assembly a=Assembly.LoadFrom(args[0]);
   BindingFlags f=BindingFlags.Static|BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
   object p=a.GetType("CodexUsageBar.CcSwitchReader").GetMethod("ReadCurrent",f,null,new Type[]{typeof(string)},null).Invoke(null,new object[]{args[1]});
   Type t=a.GetType("CodexUsageBar.CcSwitchClient");
   object client=Activator.CreateInstance(t,true);
   object snapshot=t.GetMethod("Query",f).Invoke(client,new object[]{p});
   var rows=(System.Collections.ICollection)snapshot.GetType().GetField("UsageRows",f).GetValue(snapshot);
   Console.WriteLine("Query passed; rows="+rows.Count+"; final TLS="+ServicePointManager.SecurityProtocol);
   return 0;
  } catch(Exception e) {
   while(e.InnerException!=null)e=e.InnerException;
   Console.WriteLine(e is InvalidOperationException && e.Message.StartsWith("CC Switch") ? e.Message : e.GetType().Name);return 1;
  }
 }
}

