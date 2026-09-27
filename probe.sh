#!/bin/bash
# Windows experiments; runs in the ironruby checkout.
export RUBY_EXE="$(cygpath -w "$PWD/ir.cmd")"

mkdir -p udpprobe && cd udpprobe
cat > udpprobe.csproj <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup>
</Project>
XML
cat > Program.cs <<'CS'
using System;
using System.Net;
using System.Net.Sockets;

class Program {
    static void Main() {
        foreach (int size in new[] { 2, 65536 }) {
            foreach (bool from in new[] { true, false }) {
                var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                client.Connect(server.LocalEndPoint);
                client.Send(new byte[] { 104, 101, 108, 108, 111 });
                System.Threading.Thread.Sleep(50);
                var buffer = new byte[size];
                try {
                    int n;
                    if (from) {
                        EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                        n = server.ReceiveFrom(buffer, SocketFlags.Peek, ref ep);
                    } else {
                        n = server.Receive(buffer, SocketFlags.Peek);
                    }
                    Console.WriteLine($"size {size} {(from ? "ReceiveFrom" : "Receive")} Peek -> {n}");
                } catch (SocketException e) {
                    Console.WriteLine($"size {size} {(from ? "ReceiveFrom" : "Receive")} Peek -> {e.SocketErrorCode}");
                }
                Console.WriteLine($"   then Poll(0, SelectRead) = {server.Poll(0, SelectMode.SelectRead)}, Available = {server.Available}");
                server.Dispose();
                client.Dispose();
            }
        }
    }
}
CS
dotnet run -c Release 2>&1 | tail -12
