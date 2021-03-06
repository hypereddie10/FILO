﻿/*
    This file is part of FILO.

    FILO is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    FILO is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with FILO.  If not, see <http://www.gnu.org/licenses/>.
 */
using System;
using System.Collections.Concurrent;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Threading;
using System.Net.Sockets;
using System.Text;
using FILO.Core.Log;

namespace FILO.Core
{
    public class Connection
    {
        private const int DefaultPort = 1351;
        private const int DefaultBuffer = 131072; //TODO Maybe change..
        private const int DefaultTimeout = 300000;
        private static readonly string PortCheckerUrl = "http://ping.eu/action.php?atype=5"; //TODO Create custom php script at hypereddie.com
        private String _ip;
        private Socket _socket;
        private Socket _listenSocket;
        private bool _prepared;
        private bool _connected;
        private bool _sending;
        private bool _recieving;
        private int _pos;
        private long _posLength;
        private int _bufferLength;
        private bool _completed;
        private readonly ConcurrentQueue<byte[]> _data = new ConcurrentQueue<byte[]>(); 

        public delegate void ConnectionMade(Connection connection);

        public delegate void PortFowardFailed(Connection connection);

        public event ConnectionMade OnConnectionMade = null;
        public event PortFowardFailed OnPortFowardFailed = null;

        public ConnectionType ConnectionType { get; private set; }

        public bool IsPrepared
        {
            get
            {
                return _prepared;
            }
        }

        public bool IsConnected
        {
            get
            {
                return _connected && _socket.Connected;
            }
        }
        public String Ip
        {
            get {
                return ConnectionType == ConnectionType.Sender ? "127.0.0.1" : _ip;
            }

            private set
            {
                _ip = value;
            }
        }

        public int Buffer;

        public double BufferProgress
        {
            get
            {
                return (double) ((double) _pos/(double) _posLength)*100.0;
            }
        }

        public volatile int Progress;
        public Connection(ConnectionType connectionType) : this(connectionType, "127.0.0.1", DefaultBuffer){}

        public Connection(ConnectionType connectionType, String ip) : this(connectionType, ip, DefaultBuffer){}

        public Connection(ConnectionType connectionType, String ip, int buffer)
        {
            ConnectionType = connectionType;
            _ip = ip;
            Buffer = buffer;
        }

        public void PrepareConnection()
        {
            if (_prepared)
                throw new InvalidOperationException("This connection is already prepared!");

            Logger.Debug("Prepare requested");

            if (ConnectionType == ConnectionType.Reciever)
            {
                _asyncRecievePrepare();
            }
            else
            {
                _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _asyncSendPrepare();
            }

            _prepared = true;
        }

        public void AsyncPrepareConnection()
        {
            new Thread(PrepareConnection).Start();
        }

        public void SendFile(String filePath)
        {
            if (!File.Exists(filePath))
                throw new InvalidOperationException("The file \"" + filePath + "\" does not exist!");
            if (!_connected)
                throw new InvalidOperationException("This Connection object is not connected!");
            if (!_prepared)
                throw new InvalidOperationException("This Connection object is not prepared!");
            if (ConnectionType != ConnectionType.Sender)
                throw new InvalidOperationException("This Connection object was made to recieve, not send!");

            _sending = true;
            _socket.Send(new byte[] {10}); //Tell client to prepare itself for next packet
            string filename = Path.GetFileName(filePath);
            if (filename == null)
                throw new InvalidOperationException("Error getting filename for file \"" + filePath + "\"");
            byte[] bytes = new byte[filename.Length * sizeof(char)];
            System.Buffer.BlockCopy(filename.ToCharArray(), 0, bytes, 0, bytes.Length);
            _socket.Send(BitConverter.GetBytes(bytes.Length)); //Send filename length
            _socket.Send(bytes); //Send filename
            using (var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, System.IO.FileShare.None))
            {
                var t = new Thread(new ThreadStart(_sendBufferedBytes));
                _pos = 0;
                _posLength = fs.Length;
                _socket.Send(BitConverter.GetBytes(_posLength)); //Send file length
                t.Start();
                while (_pos < _posLength)
                {
                    if (!_sending)
                        break;
                    int size = Math.Min(Buffer, (int)(_posLength - _pos));
                    var dataBuffer = new byte[size];
                    var count = fs.Read(dataBuffer, 0, size);
                    _pos += count;
                    if (count == 0)
                        break;
                    _data.Enqueue(dataBuffer);
                    _bufferLength++;
                    Progress = (int)(((double)((double)_bufferCount / (double)_bufferLength) * 100.0) + ((double)((double)_pos / (double)_posLength) * 100.0));
                }
                _completed = true;
                t.Join();
            }

            //_socket.Send(new byte[] {255, 255, 255, 255}, 0, 4, SocketFlags.None); //Terminate command
            Progress = 200;
            _listenSocket.Close();
            _listenSocket.Dispose();
            _socket.Shutdown(SocketShutdown.Both);
            _socket.Disconnect(false);
            _socket.Close();
            _socket.Dispose();
        }

        public void RecieveFile(string savePath)
        {
            if (!_connected)
                throw new InvalidOperationException("This Connection object is not connected!");
            if (!_prepared)
                throw new InvalidOperationException("This Connection object is not prepared!");
            if (ConnectionType != ConnectionType.Reciever)
                throw new InvalidOperationException("This Connection object was made to send, not recieve!");

            _recieving = true;

            Thread.Sleep(500);
            Thread thread = null;
            bool hasFile = false;
            bool prepare = false;
            string fileName;
            try
            {
                while (true)
                {
                    int bsize = 0;
                    if (hasFile)
                        bsize = Math.Min(Buffer, (int)(_posLength - _pos));
                    else bsize = prepare ? 4 : 1;
                    var data = new byte[bsize];
                    int count = _socket.Receive(data, 0, data.Length, SocketFlags.None);
                    if (count == 0)
                        continue;
                    if (hasFile)
                    {
                        if (data[0] == 255 && data[1] == 255 && data[2] == 255 && data[3] == 255 && data[4] == 0 &&
                            data[5] == 0 && count == 4)
                            break;
                        _data.Enqueue(data);
                        _bufferLength++;
                        _pos += count;
                        if (_pos >= _posLength)
                            break;
                        Progress = (int)(((double)((double)_bufferCount / (double)_bufferLength) * 100.0) + ((double)((double)_pos / (double)_posLength) * 100.0));
                    }
                    else
                    {
                        if (prepare && count > 0)
                        {
                            int size = BitConverter.ToInt32(data, 0); //Convert bytes to filename length
                            while (true)
                            {
                                data = new byte[size];
                                count = _socket.Receive(data, 0, size, SocketFlags.None); //Recieve filename
                                if (count == 0)
                                    continue;
                                break;
                            }
                            char[] chars = new char[data.Length / sizeof(char)];
                            System.Buffer.BlockCopy(data, 0, chars, 0, data.Length);
                            fileName = new string(chars); //Convert bytes to filename
                            while (true)
                            {
                                data = new byte[sizeof (long)];
                                count = _socket.Receive(data, 0, sizeof (long), SocketFlags.None);
                                if (count == 0)
                                    continue;
                                break;
                            }
                            _posLength = BitConverter.ToInt64(data, 0); //Convert bytes to long
                            hasFile = true;
                            thread = new Thread(() => _saveBufferedBytes(savePath + "\\" + fileName));
                            thread.Start();
                        }
                        if (count == 1 && data[0] == 10)
                            prepare = true;
                    }
                    Thread.Sleep(10);
                }
                _recieving = false;
                thread.Join();
                Progress = 200;
            }
            catch (IOException e)
            {
                _recieving = false;
                if (thread != null) thread.Join();
            }
        }

        private int _bufferCount;
        private void _saveBufferedBytes(string savePath)
        {
            try
            {
                using (
                    var fs = File.Open(savePath, FileMode.CreateNew, FileAccess.ReadWrite,
                        System.IO.FileShare.None))
                {
                    while (_recieving)
                    {
                        if (_completed)
                        {
                            while (!_data.IsEmpty)
                            {
                                byte[] dataBytes;
                                _data.TryDequeue(out dataBytes);
                                fs.Write(dataBytes, 0, dataBytes.Length);
                                _bufferCount++;
                            }
                            break;
                        }
                        while (!_data.IsEmpty)
                        {
                            byte[] dataBytes;
                            _data.TryDequeue(out dataBytes);
                            fs.Write(dataBytes, 0, dataBytes.Length);
                            _bufferCount++;
                            Progress = (int)(((double)((double)_bufferCount / (double)_bufferLength) * 100.0) + ((double)((double)_pos / (double)_posLength) * 100.0));
                        }
                        Thread.Sleep(10);
                    }

                    //Safty net
                    while (!_data.IsEmpty)
                    {
                        byte[] dataBytes;
                        _data.TryDequeue(out dataBytes);
                        fs.Write(dataBytes, 0, dataBytes.Length);
                        _bufferCount++;
                    }
                }
                Progress = (int)(((double)((double)_bufferCount / (double)_bufferLength) * 100.0) + ((double)((double)_pos / (double)_posLength) * 100.0));
            }
            catch (Exception e)
            {
                _recieving = false;
                Logger.Error(e);
            }
        }

        private void _sendBufferedBytes()
        {
            try
            {
                while (_sending)
                {
                    if (_completed)
                    {
                        while (!_data.IsEmpty)
                        {
                            byte[] buffer;
                            _data.TryDequeue(out buffer);
                            _socket.Send(buffer, 0, buffer.Length, SocketFlags.None);
                        }
                        break;
                    }
                    while (!_data.IsEmpty)
                    {
                        byte[] buffer;
                        _data.TryDequeue(out buffer);
                        _socket.Send(buffer, 0, buffer.Length, SocketFlags.None);
                    }
                    Thread.Sleep(10);
                }
            }
            catch (Exception e)
            {
                _sending = false;
                //TODO Show error
            }
        }

        private volatile bool testingPort = true;
        private void _asyncSendPrepare()
        {
            Logger.Debug("Preparing for sending");
            var ipLocal = new IPEndPoint(IPAddress.Any, DefaultPort);
            try
            {
                _listenSocket.Bind(ipLocal);
                Logger.Debug("Bound to port " + DefaultPort);
                _listenSocket.Listen(1);
                _listenSocket.BeginAccept(ConnectClient, null);
            }
            catch (Exception e)
            {
                Logger.Error(e);
                _listenSocket.Dispose();
                return;
            }
            Logger.Debug("Checking for open port");
            if (!CheckPort())
            {
                Logger.Error("Failed to foward port, abort..");
                _listenSocket.Close();
                _listenSocket.Dispose();
                return;
            }
            Logger.Debug("Port opened!");
        }

        private void ConnectClient(IAsyncResult asyn)
        {
            try
            {
                Logger.Info("Connection recieved");
                Socket temp = _listenSocket.EndAccept(asyn);
                temp.ReceiveTimeout = 5000;
                byte[] data = new byte[1];
                int count = 0;
                try
                {
                    count = temp.Receive(data, 0, 1, SocketFlags.None);
                }
                catch
                {
                }
                if (count != 1 || data[0] != 6)
                {
                    Logger.Debug("Dummy connection, disconnecting..");
                    if (testingPort)
                    {
                        Logger.Info("Connection ready! Waiting for client..");
                        testingPort = false;
                    }
                    //temp.Shutdown(SocketShutdown.Send);
                    temp.Disconnect(false);
                    temp.Close();
                    temp.Dispose();
                    _listenSocket.Listen(1);
                    _listenSocket.BeginAccept(ConnectClient, null);
                    return;
                }
                Logger.Info("Client connected");
                _connected = true;


                Logger.Debug("Setting up client socket");
                _socket = temp;
                _socket.SendTimeout = DefaultTimeout;
                _socket.ReceiveTimeout = DefaultTimeout;
                _socket.ReceiveBufferSize = Buffer;

                if (OnConnectionMade != null)
                {
                    Logger.Debug("Invoking OnConnectionMade event");
                    OnConnectionMade(this);
                }
            }
            catch (Exception e)
            {
                Logger.Error(e);
            }
        }

        private bool CheckPort()
        {
            string external;
            using (var web = new WebClient())
            {
                Logger.Debug("Requesting external IP from icanhazip.com");
                external = web.DownloadString("http://www.icanhazip.com").Replace("\n", "");
            }
            Logger.Debug("IP found: (" + external + ")");
            bool foward = false;
            while (true)
            {
                Logger.Debug("Requesting port status from " + PortCheckerUrl);
                var httpWReq = (HttpWebRequest)WebRequest.Create(PortCheckerUrl);

                var encoding = new ASCIIEncoding();
                string postData = "host=" + external + "&port=" + DefaultPort + "&go=Go";
                byte[] data = encoding.GetBytes(postData);

                httpWReq.Method = "POST";
                httpWReq.ContentType = "application/x-www-form-urlencoded";
                httpWReq.ContentLength = data.Length;

                using (var stream = httpWReq.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }

                var response = (HttpWebResponse)httpWReq.GetResponse();

                string responseString = new StreamReader(response.GetResponseStream()).ReadToEnd();
                if (responseString.Contains("open"))
                {
                    Logger.Debug("Port open");
                    return true;
                }
                

                if (foward)
                {
                    Logger.Error("Error fowarding port");
                    Logger.Error("UNKNOWN REASON");
                    if (OnPortFowardFailed != null)
                        OnPortFowardFailed(this);
                    return false;
                }
                Logger.Warning("Port not opened, attempting to foward port.");
                Logger.Debug("Searching for UPnP compatible router");
                if (UPnP.CanUseUpnp)
                {
                    Logger.Debug("UPnP compatible router found, attempting to forward port " + DefaultPort);
                    try
                    {
                        UPnP.ForwardPort(DefaultPort, ProtocolType.Tcp, "File Sharer");
                        Logger.Debug("Success!");
                    }
                    catch(Exception e)
                    {
                        Logger.Error("Error fowarding port");
                        Logger.Error(e);
                        if (OnPortFowardFailed != null)
                            OnPortFowardFailed(this);
                        return false;
                    }
                }
                foward = true;
            }
        }

        private void _asyncRecievePrepare()
        {
            Logger.Debug("Preparing for reciever");
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var ipAdd = IPAddress.Parse(Ip);
            var remoteEp = new IPEndPoint(ipAdd, DefaultPort);
            Logger.Debug("Setting properties");
            _socket.SendTimeout = DefaultTimeout;
            _socket.ReceiveTimeout = DefaultTimeout;
            _socket.ReceiveBufferSize = Buffer;
            Logger.Debug("Attempting connection to remote host..");
            bool worked = false;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    _socket.Connect(remoteEp);
                    worked = true;
                    break;
                }
                catch (Exception e)
                {
                    Logger.Error("Exception #" + i + ": " + e.Message);
                    Logger.Warning("Wating 500ms");
                    Thread.Sleep(500);
                }
            }
            if (!worked)
            {
                Logger.Error("Failed to connect!");
                _socket.Dispose();
                return;
            }

            _connected = true;

            _socket.Send(new byte[] {6}); //Tell server I'm a client

            if (OnConnectionMade != null)
            {
                Logger.Debug("Invoking OnConnectionMade event");
                OnConnectionMade(this);
            }
            Logger.Info("Waiting for remote host instructions..");
        }
    }

    public enum ConnectionType
    {
        Sender,
        Reciever
    }
}
