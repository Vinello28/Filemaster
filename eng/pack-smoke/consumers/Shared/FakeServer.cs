// Server HTTP/1.1 finto per lo smoke dei pacchetti: ascolta su 127.0.0.1 (porta scelta dal sistema), una richiesta per
// connessione (`Connection: close`), risposte JSON programmate per "METODO /percorso" (query esclusa). Registra ogni
// richiesta (metodo, percorso, query, intestazioni, corpo) perche' lo scenario possa controllare cosa e' arrivato sul filo.
// Deve compilare in C# 7.3 (consumatore net48 senza LangVersion): niente nullable, `is not`, `using var`, switch expression.
// Non e' il LoopbackServer dei test (quello sta in tests/ e non finisce nei pacchetti): qui basta il minimo indispensabile.
#if NETCOREAPP
#nullable disable
#endif

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PackSmoke
{
    internal sealed class RecordedRequest
    {
        public RecordedRequest(string method, string target, Dictionary<string, string> headers, byte[] body)
        {
            Method = method;
            var question = target.IndexOf('?');
            Path = question < 0 ? target : target.Substring(0, question);
            Query = question < 0 ? string.Empty : target.Substring(question + 1);
            Headers = headers;
            Body = body;
        }

        public string Method { get; }

        public string Path { get; }

        public string Query { get; }

        public Dictionary<string, string> Headers { get; }

        public byte[] Body { get; }

        // Valore dell'intestazione (nome senza distinzione di maiuscole), null se assente.
        public string Header(string name)
        {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
        }
    }

    internal sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly Dictionary<string, KeyValuePair<int, byte[]>> _routes = new Dictionary<string, KeyValuePair<int, byte[]>>(StringComparer.Ordinal);
        private readonly List<RecordedRequest> _requests = new List<RecordedRequest>();
        private readonly List<string> _errors = new List<string>();
        private readonly Thread _thread;

        public FakeServer()
        {
            _listener.Start();
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "FakeServer" };
            _thread.Start();
        }

        public Uri BaseAddress
        {
            get { return new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) + "/"); }
        }

        // Risposta per "METODO /percorso" (la query non conta): status e corpo JSON (byte esatti, per esempio una fixture catturata).
        public void Respond(string method, string path, int status, byte[] jsonBody)
        {
            lock (_routes)
            {
                _routes[method + " " + path] = new KeyValuePair<int, byte[]>(status, jsonBody);
            }
        }

        public RecordedRequest[] Requests
        {
            get { lock (_requests) { return _requests.ToArray(); } }
        }

        // Errori del server stesso (richiesta illeggibile, connessione caduta): lo scenario li tratta come un fallimento.
        public string[] Errors
        {
            get { lock (_errors) { return _errors.ToArray(); } }
        }

        public void Dispose()
        {
            _listener.Stop();
            _thread.Join(5000);
        }

        private void AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    using (client)
                    {
                        Handle(client.GetStream());
                    }
                }
                catch (Exception e)
                {
                    lock (_errors)
                    {
                        _errors.Add(e.GetType().Name + ": " + e.Message);
                    }
                }
            }
        }

        private void Handle(NetworkStream stream)
        {
            stream.ReadTimeout = 30000;
            var requestLine = ReadLine(stream);
            var parts = requestLine.Split(' ');
            if (parts.Length != 3)
            {
                throw new InvalidDataException("Riga di richiesta non valida: " + requestLine);
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string line;
            while ((line = ReadLine(stream)).Length > 0)
            {
                var colon = line.IndexOf(':');
                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            string expect;
            if (headers.TryGetValue("Expect", out expect) && string.Equals(expect, "100-continue", StringComparison.OrdinalIgnoreCase))
            {
                // HttpWebRequest (net48) chiede il permesso prima di mandare il corpo: senza risposta aspetterebbe 350 ms.
                Write(stream, "HTTP/1.1 100 Continue\r\n\r\n", new byte[0]);
            }

            var request = new RecordedRequest(parts[0], parts[1], headers, ReadBody(stream, headers));
            lock (_requests)
            {
                _requests.Add(request);
            }

            KeyValuePair<int, byte[]> route;
            bool found;
            lock (_routes)
            {
                found = _routes.TryGetValue(request.Method + " " + request.Path, out route);
            }

            var status = found ? route.Key : 404;
            var body = found ? route.Value : Encoding.UTF8.GetBytes("{\"type\":\"/problems/not-found\",\"title\":\"Not Found\",\"status\":404}");
            var contentType = found ? "application/json; charset=utf-8" : "application/problem+json; charset=utf-8";
            Write(
                stream,
                "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " Smoke\r\n"
                    + "Content-Type: " + contentType + "\r\n"
                    + "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                    + "Connection: close\r\n\r\n",
                body);
        }

        private static byte[] ReadBody(NetworkStream stream, Dictionary<string, string> headers)
        {
            var body = new MemoryStream();
            string value;
            if (headers.TryGetValue("Transfer-Encoding", out value) && value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                while (true)
                {
                    var sizeLine = ReadLine(stream);
                    var semicolon = sizeLine.IndexOf(';');
                    var size = int.Parse(semicolon < 0 ? sizeLine : sizeLine.Substring(0, semicolon), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                    if (size == 0)
                    {
                        while (ReadLine(stream).Length > 0)
                        {
                        }

                        return body.ToArray();
                    }

                    Copy(stream, body, size);
                    if (ReadLine(stream).Length != 0)
                    {
                        throw new InvalidDataException("Manca il CRLF dopo un pezzo chunked.");
                    }
                }
            }

            if (headers.TryGetValue("Content-Length", out value))
            {
                Copy(stream, body, int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture));
            }

            return body.ToArray();
        }

        private static void Copy(NetworkStream stream, MemoryStream target, int count)
        {
            var buffer = new byte[16 * 1024];
            while (count > 0)
            {
                var read = stream.Read(buffer, 0, Math.Min(buffer.Length, count));
                if (read == 0)
                {
                    throw new EndOfStreamException("Connessione chiusa dentro il corpo della richiesta.");
                }

                target.Write(buffer, 0, read);
                count -= read;
            }
        }

        // Una riga terminata da CRLF, senza terminatore (un byte alla volta: le richieste dello smoke sono piccole).
        private static string ReadLine(NetworkStream stream)
        {
            var line = new List<byte>();
            while (true)
            {
                var b = stream.ReadByte();
                if (b < 0)
                {
                    throw new EndOfStreamException("Connessione chiusa a meta' riga.");
                }

                if (b == '\n')
                {
                    if (line.Count > 0 && line[line.Count - 1] == '\r')
                    {
                        line.RemoveAt(line.Count - 1);
                    }

                    return Encoding.ASCII.GetString(line.ToArray());
                }

                line.Add((byte)b);
            }
        }

        private static void Write(NetworkStream stream, string head, byte[] body)
        {
            var bytes = Encoding.ASCII.GetBytes(head);
            stream.Write(bytes, 0, bytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }
    }
}
