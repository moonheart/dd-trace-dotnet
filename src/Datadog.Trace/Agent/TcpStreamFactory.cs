using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Datadog.Trace.Agent
{
    internal class TcpStreamFactory : IStreamFactory
    {
        private string _host;
        private int _port;

        public TcpStreamFactory(string host, int port)
        {
            _host = host;
            _port = port;
        }

        public void GetStreams(out Stream requestStream, out Stream responseStream)
        {
            var stream = ConnectSocket(_host, _port);
            requestStream = stream;
            responseStream = stream;
        }

        private static NetworkStream ConnectSocket(string host, int port)
        {
            var ipAddress = Dns.GetHostAddresses(host).FirstOrDefault(t => t.AddressFamily == AddressFamily.InterNetwork);
            IPEndPoint endpoint = new IPEndPoint(ipAddress, port);

            Socket socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(endpoint);

            return new NetworkStream(socket, FileAccess.ReadWrite, ownsSocket: true);
        }
    }
}
