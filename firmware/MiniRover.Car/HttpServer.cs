using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MiniRover.Car
{
    /// <summary>One parsed HTTP request.</summary>
    public sealed class HttpRequest
    {
        public string Method = "";
        public string Path = "";
        public Hashtable Query = new Hashtable();
        public byte[] Body = new byte[0];

        public string Get(string key, string fallback = null)
        {
            string v = Query[key] as string;
            return v ?? fallback;
        }

        public double GetDouble(string key, double fallback)
        {
            string v = Get(key);
            if (v == null) return fallback;
            try { return double.Parse(v); }
            catch { return fallback; }
        }
    }

    /// <summary>
    /// Minimal blocking HTTP/1.0 server on raw sockets: one connection at a time on its own thread, request
    /// bodies up to <see cref="MaxBody"/> bytes, form-encoded bodies merged into the query table. It serves the
    /// WiFi setup page and the development/test API; the driving link itself is WebRTC.
    /// </summary>
    public sealed class HttpServer
    {
        public const int MaxBody = 4096;

        public delegate void Handler(HttpRequest request, Socket client);

        readonly int _port;
        readonly Handler _handler;
        Socket _listener;
        Thread _thread;

        public HttpServer(int port, Handler handler)
        {
            _port = port;
            _handler = handler;
        }

        public void Start()
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.Bind(new IPEndPoint(IPAddress.Any, _port));
            _listener.Listen(2);
            _thread = new Thread(AcceptLoop);
            _thread.Start();
        }

        void AcceptLoop()
        {
            while (true)
            {
                Socket client = null;
                try
                {
                    client = _listener.Accept();
                    client.ReceiveTimeout = 3000;
                    HttpRequest req = Parse(client);
                    if (req != null)
                    {
                        _handler(req, client);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("HTTP: " + ex.Message);
                    try { if (client != null) SendText(client, 500, "text/plain", "error: " + ex.Message); } catch { }
                }
                finally
                {
                    if (client != null)
                    {
                        try { client.Close(); } catch { }
                    }
                }
            }
        }

        static HttpRequest Parse(Socket client)
        {
            byte[] buf = new byte[1024];
            int total = 0;
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                if (total == buf.Length)
                {
                    if (buf.Length >= 8192) return null; // headers too large
                    byte[] bigger = new byte[buf.Length * 2];
                    Array.Copy(buf, bigger, total);
                    buf = bigger;
                }
                int n = client.Receive(buf, total, buf.Length - total, SocketFlags.None);
                if (n <= 0) return null;
                total += n;
                headerEnd = IndexOfHeaderEnd(buf, total);
            }

            string head = Encoding.UTF8.GetString(buf, 0, headerEnd);
            string[] lines = head.Split('\n');
            string[] first = lines[0].Trim().Split(' ');
            if (first.Length < 2) return null;

            var req = new HttpRequest { Method = first[0].ToUpper() };
            string target = first[1];
            int q = target.IndexOf('?');
            req.Path = q >= 0 ? target.Substring(0, q) : target;
            if (q >= 0) ParseForm(target.Substring(q + 1), req.Query);

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.ToLower().StartsWith("content-length:"))
                {
                    contentLength = int.Parse(line.Substring(15).Trim());
                }
            }
            if (contentLength > MaxBody) throw new Exception("body too large");

            int bodyStart = headerEnd + 4;
            byte[] body = new byte[contentLength];
            int have = total - bodyStart;
            if (have > contentLength) have = contentLength;
            if (have > 0) Array.Copy(buf, bodyStart, body, 0, have);
            while (have < contentLength)
            {
                int n = client.Receive(body, have, contentLength - have, SocketFlags.None);
                if (n <= 0) break;
                have += n;
            }
            req.Body = body;

            if (contentLength > 0)
            {
                // Form posts (the setup page) land in the same table as query parameters.
                ParseForm(Encoding.UTF8.GetString(body, 0, body.Length), req.Query);
            }
            return req;
        }

        static int IndexOfHeaderEnd(byte[] b, int len)
        {
            for (int i = 0; i + 3 < len; i++)
            {
                if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
            }
            return -1;
        }

        static void ParseForm(string s, Hashtable into)
        {
            foreach (string pair in s.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string k = UrlDecode(eq >= 0 ? pair.Substring(0, eq) : pair);
                string v = eq >= 0 ? UrlDecode(pair.Substring(eq + 1)) : "";
                into[k] = v;
            }
        }

        public static string UrlDecode(string s)
        {
            var bytes = new byte[s.Length];
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '+')
                {
                    bytes[n++] = (byte)' ';
                }
                else if (c == '%' && i + 2 < s.Length)
                {
                    bytes[n++] = (byte)Convert.ToInt32(s.Substring(i + 1, 2), 16);
                    i += 2;
                }
                else
                {
                    bytes[n++] = (byte)c;
                }
            }
            return Encoding.UTF8.GetString(bytes, 0, n);
        }

        public static string HtmlEncode(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '&': sb.Append("&amp;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        public static void SendText(Socket client, int status, string contentType, string body)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body);
            string header = "HTTP/1.0 " + status + " " + Reason(status) + "\r\n" +
                            "Content-Type: " + contentType + "\r\n" +
                            "Content-Length: " + payload.Length + "\r\n" +
                            "Access-Control-Allow-Origin: *\r\n" +
                            "Cache-Control: no-store\r\n" +
                            "Connection: close\r\n\r\n";
            byte[] h = Encoding.UTF8.GetBytes(header);
            client.Send(h, 0, h.Length, SocketFlags.None);
            if (payload.Length > 0) client.Send(payload, 0, payload.Length, SocketFlags.None);
        }

        static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 404: return "Not Found";
                case 503: return "Service Unavailable";
                default: return "Error";
            }
        }
    }
}
