/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation.
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution. If
 * you cannot locate the  Apache License, Version 2.0, please send an email to
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace IronRuby.Tests {

    public partial class Tests {
        /// <summary>
        /// Net::HTTP over TLS against a server in this process (TlsTestServer below), so that no
        /// CRuby is needed. The server's self-signed certificate is the CA: ca_file, cert_store and
        /// verify_callback decide what is trusted, and the read, write and open (handshake)
        /// timeouts fire against a server that stops answering. IronRuby's SSLSocket is a blocking
        /// SslStream; before Docs/net-http-over-tls.md stage 1 none of those timeouts fired and
        /// only the system trust store counted. net/http needs prism.
        /// </summary>
        [Options(Prism = true)]
        public void NetHttp_Tls1() {
            using (var server = new TlsTestServer()) {
                TestOutput(@"
verbose, $VERBOSE = $VERBOSE, nil   # the libraries' own parse warnings are not what this is about
require 'ruby4'
require 'net/http'
require 'openssl'
$VERBOSE = verbose
port, stall_port, ca = " + server.Port + ", " + server.StallPort + ", '" + server.CertificatePath.Replace("\\", "/") + @"'

http = lambda do |**opts|
  h = Net::HTTP.new('127.0.0.1', opts.delete(:port) || port)
  h.use_ssl = true
  h.max_retries = 0
  opts.each { |name, value| h.__send__(""#{name}="", value) }
  h
end

# ca_file: keep-alive requests both ways
http.(ca_file: ca).start do |h|
  p h.get('/data?size=100000').body.bytesize
  body = 'y' * 300_000
  p h.post('/echo', body).body == body
end

# cert_store
store = OpenSSL::X509::Store.new
store.add_file(ca)
p http.(cert_store: store).start { |h| h.get('/data?size=3').body }

# the system store does not know the certificate
begin
  http.().start { |h| h.get('/data?size=3') }
rescue OpenSSL::SSL::SSLError
  p :untrusted
end

# verify_callback has the last word, either way
seen = []
p http.(verify_callback: lambda { |ok, ctx| seen << [ok, ctx.error_depth, ctx.error, ctx.current_cert.subject.to_s]; true }).start { |h| h.get('/data?size=3').body }
p seen
seen = []
http.(ca_file: ca, verify_callback: lambda { |ok, ctx| seen << ok; ok }).start { |h| h.get('/data?size=3') }
p seen
begin
  http.(ca_file: ca, verify_callback: lambda { |ok, ctx| false }).start { |h| h.get('/data?size=3') }
rescue OpenSSL::SSL::SSLError
  p :refused
end

# read_timeout: the server never answers
t = Time.now
begin
  http.(ca_file: ca, read_timeout: 1).start { |h| h.get('/stall') }
rescue Net::ReadTimeout
  p [:read_timeout, Time.now - t < 5]
end

# write_timeout: the server never reads the body
t = Time.now
begin
  http.(ca_file: ca, write_timeout: 1).start { |h| h.post('/sink', 'z' * 64_000_000) }
rescue Net::WriteTimeout
  p [:write_timeout, Time.now - t < 10]
end

# open_timeout: the server accepts the connection but never answers the handshake
t = Time.now
begin
  http.(port: stall_port, ca_file: ca, open_timeout: 1).start { |h| h.get('/') }
rescue Net::OpenTimeout
  p [:open_timeout, Time.now - t < 5]
end

# the socket itself: the name check, and no use after a timeout
ctx = OpenSSL::SSL::SSLContext.new
ctx.set_params(ca_file: ca)
s = OpenSSL::SSL::SSLSocket.new(TCPSocket.new('127.0.0.1', port), ctx)
s.sync_close = true
s.connect
p s.post_connection_check('127.0.0.1')
begin
  s.post_connection_check('example.com')
rescue OpenSSL::SSL::SSLError
  p :name_mismatch
end
s.write ""GET /stall HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n""
s.__read_timeout = 0.5
begin
  s.sysread(10)
rescue IO::TimeoutError
  p :io_timeout
end
begin
  s.sysread(10)
rescue OpenSSL::SSL::SSLError => e
  p e.message.include?('unusable')
end
s.close
", @"
100000
true
""xxx""
:untrusted
""xxx""
[[false, 0, 19, ""/CN=127.0.0.1""]]
[true]
:refused
[:read_timeout, true]
[:write_timeout, true]
[:open_timeout, true]
true
:name_mismatch
:io_timeout
true
");
            }
        }

        /// <summary>
        /// HTTPS on a loopback port with a throwaway self-signed certificate for 127.0.0.1, whose
        /// PEM is written to a temporary file. GET /data?size=N answers N bytes, POST /echo sends the
        /// body back, /stall and /sink answer nothing and read nothing more. StallPort accepts TCP
        /// connections and never starts the handshake.
        /// </summary>
        private sealed class TlsTestServer : IDisposable {
            private readonly TcpListener _listener;
            private readonly TcpListener _stallListener;
            private readonly X509Certificate2 _certificate;
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly ManualResetEvent _stop = new ManualResetEvent(false);
            private volatile bool _disposed;

            public readonly string CertificatePath;

            public TlsTestServer() {
                using (var key = RSA.Create(2048)) {
                    var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    var names = new SubjectAlternativeNameBuilder();
                    names.AddIpAddress(IPAddress.Loopback);
                    request.CertificateExtensions.Add(names.Build());
                    using (var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1))) {
                        // SChannel cannot serve with CreateSelfSigned's ephemeral key: round-trip it through PFX.
#if NET9_0_OR_GREATER
                        _certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
#else
                        _certificate = new X509Certificate2(created.Export(X509ContentType.Pfx));
#endif
                    }
                }
                CertificatePath = Path.Combine(Path.GetTempPath(), "ironruby-tls-test-" + Guid.NewGuid().ToString("N") + ".pem");
                File.WriteAllText(CertificatePath, _certificate.ExportCertificatePem() + "\n");

                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _stallListener = new TcpListener(IPAddress.Loopback, 0);
                _stallListener.Start();
                Start(() => Accept(_listener, Serve));
                Start(() => Accept(_stallListener, client => _stop.WaitOne()));
            }

            public int Port {
                get { return ((IPEndPoint)_listener.LocalEndpoint).Port; }
            }

            public int StallPort {
                get { return ((IPEndPoint)_stallListener.LocalEndpoint).Port; }
            }

            private static void Start(ThreadStart body) {
                new Thread(body) { IsBackground = true }.Start();
            }

            private void Accept(TcpListener listener, Action<TcpClient> serve) {
                while (!_disposed) {
                    TcpClient client;
                    try {
                        client = listener.AcceptTcpClient();
                    } catch (Exception) {
                        return;
                    }
                    lock (_clients) {
                        _clients.Add(client);
                    }
                    Start(() => {
                        try {
                            serve(client);
                        } catch (Exception) {
                            // the client went away, or the test is over
                        } finally {
                            client.Dispose();
                        }
                    });
                }
            }

            private void Serve(TcpClient client) {
                using (var ssl = new SslStream(client.GetStream(), false)) {
                    ssl.AuthenticateAsServer(_certificate);
                    while (true) {
                        string head = ReadHead(ssl);
                        if (head == null) {
                            return;
                        }
                        string path = head.Split(' ')[1];
                        if (path == "/stall" || path == "/sink") {
                            // answer nothing and read nothing until the client gives up
                            _stop.WaitOne();
                            return;
                        }

                        byte[] body;
                        if (path == "/echo") {
                            body = ReadExactly(ssl, ContentLength(head));
                        } else {
                            int size = Int32.Parse(path.Substring(path.IndexOf("size=") + 5));
                            body = new byte[size];
                            for (int i = 0; i < size; i++) {
                                body[i] = (byte)'x';
                            }
                        }
                        byte[] header = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + body.Length + "\r\n\r\n");
                        ssl.Write(header, 0, header.Length);
                        ssl.Write(body, 0, body.Length);
                        ssl.Flush();
                    }
                }
            }

            // The request line and headers, or null when the client closed the connection.
            private static string ReadHead(Stream/*!*/ stream) {
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) {
                    if (stream.Read(one, 0, 1) == 0) {
                        return null;
                    }
                    head.Append((char)one[0]);
                }
                return head.ToString();
            }

            private static int ContentLength(string/*!*/ head) {
                foreach (string line in head.Split(new[] { "\r\n" }, StringSplitOptions.None)) {
                    if (line.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase)) {
                        return Int32.Parse(line.Substring(15).Trim());
                    }
                }
                return 0;
            }

            private static byte[]/*!*/ ReadExactly(Stream/*!*/ stream, int length) {
                var result = new byte[length];
                int offset = 0;
                while (offset < length) {
                    int read = stream.Read(result, offset, length - offset);
                    if (read == 0) {
                        throw new EndOfStreamException();
                    }
                    offset += read;
                }
                return result;
            }

            public void Dispose() {
                _disposed = true;
                _stop.Set();
                _listener.Stop();
                _stallListener.Stop();
                lock (_clients) {
                    foreach (var client in _clients) {
                        client.Dispose();
                    }
                }
                _certificate.Dispose();
                try {
                    File.Delete(CertificatePath);
                } catch (IOException) {
                }
            }
        }
    }
}
