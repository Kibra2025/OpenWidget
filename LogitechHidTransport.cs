// Adapted from LogiCopy+ native/LogiBridge.cs (MIT).
// Copyright (c) 2026 LogiCopy+ contributors. See licenses/LogiCopy-MIT.txt.
// Only vendor-defined HID++ request/reply transport is included.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DeskHardwareWidget.Logitech {
  internal static class Native {
    internal const uint GENERIC_READ=0x80000000, GENERIC_WRITE=0x40000000, OVERLAPPED=0x40000000;
    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { public int cbSize; public Guid guid; public int flags; public IntPtr reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceInfo { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] internal struct Attributes { public int Size; public ushort VendorID,ProductID,VersionNumber; }
    [StructLayout(LayoutKind.Sequential)] internal struct Caps {
      public ushort Usage,UsagePage,InputReportByteLength,OutputReportByteLength,FeatureReportByteLength;
      [MarshalAs(UnmanagedType.ByValArray,SizeConst=17)] public ushort[] Reserved;
      public ushort NumberLinkCollectionNodes,NumberInputButtonCaps,NumberInputValueCaps,NumberInputDataIndices;
      public ushort NumberOutputButtonCaps,NumberOutputValueCaps,NumberOutputDataIndices,NumberFeatureButtonCaps,NumberFeatureValueCaps,NumberFeatureDataIndices;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Overlap { public IntPtr Internal,InternalHigh; public uint Offset,OffsetHigh; public IntPtr Event; }
    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle h,ref Attributes a);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle h,out IntPtr p);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr p,out Caps c);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetProductString(SafeFileHandle h,byte[] buffer,int length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_SetNumInputBuffers(SafeFileHandle h,int count);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid g,string e,IntPtr p,uint f);
    [DllImport("setupapi.dll",SetLastError=true)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr h,IntPtr d,ref Guid g,int i,ref InterfaceData data);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr h,ref InterfaceData data,IntPtr detail,int size,out int needed,IntPtr dev);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr h,ref InterfaceData data,IntPtr detail,int size,out int needed,ref DeviceInfo dev);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern bool SetupDiGetDevicePropertyW(IntPtr h,ref DeviceInfo dev,ref PropertyKey key,out uint type,byte[] buffer,uint size,out uint needed,uint flags);
    [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern SafeFileHandle CreateFile(string p,uint a,uint s,IntPtr sec,uint c,uint f,IntPtr t);
    [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool ReadFile(SafeFileHandle h,IntPtr b,int len,out int n,IntPtr ov);
    [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool WriteFile(SafeFileHandle h,IntPtr b,int len,out int n,IntPtr ov);
    [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool GetOverlappedResult(SafeFileHandle h,IntPtr ov,out int n,bool wait);
    [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool CancelIoEx(SafeFileHandle h,IntPtr ov);
    [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(IntPtr h,uint ms);
  }

  internal sealed class HidEndpoint : IDisposable {
    internal string Path, Product, Group, Container; internal ushort ProductId,Usage,UsagePage;
    internal int InputLength,OutputLength; internal SafeFileHandle Handle;
    private volatile bool closing; private int disposed; private Thread reader; private BlockingCollection<byte[]> inbox; private ManualResetEvent readerReady=new ManualResetEvent(false);
    internal void Start(BlockingCollection<byte[]> destination) {
      inbox=destination; Native.HidD_SetNumInputBuffers(Handle,64);
      if(InputLength<7) return;
      reader=new Thread(ReadLoop); reader.IsBackground=true; reader.Name="HID input"; reader.Start();
      // Establish the pending ReadFile before the first fast device reply. HID
      // input can otherwise race creation of the long-report reader thread.
      readerReady.WaitOne(750);
    }
    private void ReadLoop() {
      byte[] data=new byte[Math.Max(InputLength,20)]; GCHandle pin=GCHandle.Alloc(data,GCHandleType.Pinned);
      using(EventWaitHandle ev=new EventWaitHandle(false,EventResetMode.ManualReset)) {
        IntPtr ov=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.Overlap)));
        try {
          while(!closing) {
            ev.Reset(); Native.Overlap state=new Native.Overlap(); state.Event=ev.SafeWaitHandle.DangerousGetHandle(); Marshal.StructureToPtr(state,ov,false);
            int n; bool success=Native.ReadFile(Handle,pin.AddrOfPinnedObject(),InputLength,out n,ov);
            int readError=success?0:Marshal.GetLastWin32Error();
            readerReady.Set();
            if(!success) {
              if(readError!=997) break;
              while(!closing && Native.WaitForSingleObject(state.Event,250)==258) {}
              if(closing) Native.CancelIoEx(Handle,ov);
              if(!Native.GetOverlappedResult(Handle,ov,out n,true)) { if(closing) break; continue; }
            }
            if(n>=7 && (data[0]==0x10 || data[0]==0x11)) {
              byte[] packet=new byte[n]; Array.Copy(data,packet,n);
              if(!closing) inbox.TryAdd(packet);
            }
          }
        } catch(ObjectDisposedException) {} catch(Exception ex) { Console.Error.WriteLine("HID read: "+ex.Message); }
        finally { readerReady.Set();Marshal.FreeHGlobal(ov); pin.Free(); }
      }
    }
    internal bool Send(byte[] packet) {
      byte[] data=new byte[OutputLength]; Array.Copy(packet,data,Math.Min(packet.Length,data.Length));
      GCHandle pin=GCHandle.Alloc(data,GCHandleType.Pinned);
      using(EventWaitHandle ev=new EventWaitHandle(false,EventResetMode.ManualReset)) {
        Native.Overlap state=new Native.Overlap(); state.Event=ev.SafeWaitHandle.DangerousGetHandle();
        IntPtr ov=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.Overlap))); Marshal.StructureToPtr(state,ov,false);
        try {
          int n; bool sent=Native.WriteFile(Handle,pin.AddrOfPinnedObject(),data.Length,out n,ov);
          if(!sent) {
            if(Marshal.GetLastWin32Error()!=997) return false;
            if(Native.WaitForSingleObject(state.Event,750)!=0) { Native.CancelIoEx(Handle,ov); Native.GetOverlappedResult(Handle,ov,out n,true); return false; }
            sent=Native.GetOverlappedResult(Handle,ov,out n,false);
          }
          return sent && n==data.Length;
        } finally { Marshal.FreeHGlobal(ov); pin.Free(); }
      }
    }
    public void Dispose() { if(Interlocked.Exchange(ref disposed,1)!=0)return; closing=true; if(Handle!=null && !Handle.IsInvalid && !Handle.IsClosed) { Native.CancelIoEx(Handle,IntPtr.Zero); if(reader!=null) reader.Join(1500); Handle.Dispose(); } if(reader==null || !reader.IsAlive)readerReady.Dispose(); }

    internal static List<HidEndpoint> Discover() {
      List<HidEndpoint> result=new List<HidEndpoint>(); Guid guid; Native.HidD_GetHidGuid(out guid);
      IntPtr set=Native.SetupDiGetClassDevs(ref guid,null,IntPtr.Zero,0x12);
      if(set==new IntPtr(-1)) return result;
      try {
        for(int i=0;;i++) {
          Native.InterfaceData d=new Native.InterfaceData(); d.cbSize=Marshal.SizeOf(typeof(Native.InterfaceData));
          if(!Native.SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref guid,i,ref d)) break;
          int needed; Native.SetupDiGetDeviceInterfaceDetail(set,ref d,IntPtr.Zero,0,out needed,IntPtr.Zero);
          if(needed<=0) continue;
          IntPtr detail=Marshal.AllocHGlobal(needed);
          string path=null;
          var deviceInfo=new Native.DeviceInfo { Size=Marshal.SizeOf(typeof(Native.DeviceInfo)) };
          try { Marshal.WriteInt32(detail,IntPtr.Size==8?8:6); if(Native.SetupDiGetDeviceInterfaceDetail(set,ref d,detail,needed,out needed,ref deviceInfo)) path=Marshal.PtrToStringUni(IntPtr.Add(detail,4)); }
          finally { Marshal.FreeHGlobal(detail); }
          if(path==null || path.IndexOf("vid_046d",StringComparison.OrdinalIgnoreCase)<0) continue;
          SafeFileHandle h=Native.CreateFile(path,Native.GENERIC_READ|Native.GENERIC_WRITE,3,IntPtr.Zero,3,Native.OVERLAPPED,IntPtr.Zero);
          if(h.IsInvalid) { h.Dispose(); continue; }
          Native.Attributes attrs=new Native.Attributes(); attrs.Size=Marshal.SizeOf(typeof(Native.Attributes));
          IntPtr preparsed=IntPtr.Zero; Native.Caps caps=new Native.Caps(); bool retained=false;
          try {
            if(!Native.HidD_GetAttributes(h,ref attrs) || attrs.VendorID!=0x046D || !Native.HidD_GetPreparsedData(h,out preparsed) || Native.HidP_GetCaps(preparsed,out caps)!=0x110000) { h.Dispose(); continue; }
            // Only vendor-defined HID++ collections, never ordinary keyboard/mouse input.
            if(caps.UsagePage<0xFF00 || caps.InputReportByteLength<7 || caps.OutputReportByteLength<7) { h.Dispose(); continue; }
            byte[] product=new byte[512]; string name=Native.HidD_GetProductString(h,product,product.Length)?Encoding.Unicode.GetString(product).TrimEnd('\0'):"Logitech";
            // Windows assigns both a COL suffix and a final instance index to
            // collections belonging to one HID interface. Merge those collections
            // so a short request can receive a long response on its sibling.
            string group=Regex.Replace(Regex.Replace(path.ToLowerInvariant(),"&col[0-9a-f]+",""),"&[0-9a-f]{4}(?=#\\{)","");
            var key=new Native.PropertyKey { Format=new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),Id=2 };
            uint propertyType,propertySize; var containerBytes=new byte[16]; string container=null;
            if(Native.SetupDiGetDevicePropertyW(set,ref deviceInfo,ref key,out propertyType,containerBytes,16,out propertySize,0) && propertyType==13 && propertySize==16) {
              var containerGuid=new Guid(containerBytes); if(containerGuid!=Guid.Empty)container=containerGuid.ToString();
            }
            result.Add(new HidEndpoint { Path=path,Group=group,Container=container,Product=name,ProductId=attrs.ProductID,Usage=caps.Usage,UsagePage=caps.UsagePage,InputLength=caps.InputReportByteLength,OutputLength=caps.OutputReportByteLength,Handle=h });
            retained=true;
          } finally { if(preparsed!=IntPtr.Zero) Native.HidD_FreePreparsedData(preparsed); if(!retained)h.Dispose(); }
        }
      } catch { foreach(var endpoint in result)endpoint.Dispose(); throw; }
      finally { Native.SetupDiDestroyDeviceInfoList(set); }
      return result;
    }
  }

  internal sealed class HidTransport : IDisposable {
    internal List<HidEndpoint> Endpoints; internal string Key; internal ushort ProductId;
    private BlockingCollection<byte[]> inbox=new BlockingCollection<byte[]>(128); private int sequence=11,nonceCounter=Environment.TickCount&255;
    private readonly CancellationToken cancellation;
    private readonly bool trace=Environment.GetEnvironmentVariable("DESKWIDGET_HID_TRACE")=="1";
    internal string LastError="";
    internal HidTransport(List<HidEndpoint> endpoints,CancellationToken token) { cancellation=token;Endpoints=endpoints; Key=endpoints[0].Group; ProductId=endpoints[0].ProductId; try { foreach(var e in endpoints) e.Start(inbox); } catch { Dispose();throw; } }
    internal bool IsReceiver { get { return new ushort[]{0xC52B,0xC532,0xC539,0xC53A,0xC53F,0xC547,0xC548}.Contains(ProductId); } }
    internal static bool Matches(byte[] packet,byte slot,byte feature,byte function) { return packet!=null && packet.Length>=7 && (packet[0]==0x10 || packet[0]==0x11) && packet[1]==slot && ((packet[2]==feature && packet[3]==function) || ((packet[2]==0xFF || packet[2]==0x8F) && packet[3]==feature && packet[4]==function)); }
    private void Trace(string message) { if(trace) Console.Error.WriteLine(DateTime.UtcNow.ToString("o")+" HID++ "+message); }
    internal byte[] Request(byte slot,byte feature,byte function,byte[] parameters,int timeout=450,byte? echoNonce=null) {
      lock(this) {
        cancellation.ThrowIfCancellationRequested();
        LastError=""; byte[] stale; while(inbox.TryTake(out stale)) {}
        // Use two distinct high software IDs so adjacent feature requests are
        // distinguishable, without cycling through every other client's ID.
        // These are client tokens, not reserved IDs or a device lock.
        byte fn=(byte)((function&0xF0)|sequence); sequence=sequence==11?12:11;
        int size=parameters.Length>3 || !Endpoints.Any(e=>e.OutputLength==7)?20:7; byte[] message=new byte[size]; message[0]=(byte)(size==7?0x10:0x11); message[1]=slot; message[2]=feature; message[3]=fn; Array.Copy(parameters,0,message,4,Math.Min(parameters.Length,size-4));
        var candidates=Endpoints.Where(e=>e.OutputLength>=size).OrderBy(e=>e.OutputLength).ToList();
        if(candidates.Count==0) { LastError="Nessun report HID di lunghezza compatibile.";Trace("no-endpoint slot="+slot); return null; }
        bool sent=false; foreach(var endpoint in candidates) { if(endpoint.Send(message)) { sent=true; break; } }
        if(!sent) { LastError="Scrittura della richiesta HID non riuscita; il dispositivo potrebbe essere occupato.";Trace("send-failed slot="+slot); return null; }
        Stopwatch watch=Stopwatch.StartNew(); byte[] packet;int unrelated=0;
        while(watch.ElapsedMilliseconds<timeout) {
          if(!inbox.TryTake(out packet,Math.Max(1,timeout-(int)watch.ElapsedMilliseconds),cancellation)) break;
          if(!Matches(packet,slot,feature,fn)) { unrelated++;continue; }
          if(packet[2]==0xFF || packet[2]==0x8F) { LastError="HID++ error 0x"+packet[5].ToString("X2");Trace("error slot="+slot+" feature="+feature.ToString("X2")+" function="+fn.ToString("X2")+" code="+packet[5].ToString("X2")+" elapsed="+watch.ElapsedMilliseconds); return null; }
          byte[] data=new byte[packet.Length-4]; Array.Copy(packet,4,data,0,data.Length);
          if(echoNonce.HasValue && (data.Length<3 || data[2]!=echoNonce.Value)) { unrelated++;Trace("nonce-mismatch slot="+slot+" function="+fn.ToString("X2"));continue; }
          Trace("ok slot="+slot+" feature="+feature.ToString("X2")+" function="+fn.ToString("X2")+" elapsed="+watch.ElapsedMilliseconds+" unrelated="+unrelated);return data;
        }
        LastError="Nessuna risposta HID++: dispositivo in sospensione, non collegato o interfaccia occupata.";Trace("timeout slot="+slot+" feature="+feature.ToString("X2")+" function="+fn.ToString("X2")+" elapsed="+watch.ElapsedMilliseconds+" unrelated="+unrelated); return null;
      }
    }
    internal byte Feature(byte slot,ushort id) { byte[] r=Request(slot,0,0,new byte[]{(byte)(id>>8),(byte)id,0}); return r!=null && r.Length>=1?r[0]:(byte)0; }
    internal bool Ping(byte slot) {
      for(int attempt=0;attempt<2;attempt++) {
        byte nonce=(byte)(Interlocked.Increment(ref nonceCounter)&255);
        byte[] r=Request(slot,0,0x10,new byte[]{0,0,nonce},450,nonce);
        if(r!=null && r.Length>=3 && r[0]>=2 && r[2]==nonce)return true;
        if(r!=null) LastError="Il dispositivo non risponde con un protocollo HID++ 2.0 supportato.";
        if(attempt==0 && cancellation.WaitHandle.WaitOne(60))cancellation.ThrowIfCancellationRequested();
      }
      return false;
    }
    public void Dispose() { foreach(var e in Endpoints) e.Dispose(); inbox.Dispose(); }
  }

}
