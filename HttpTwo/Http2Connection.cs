﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using HttpTwo.Internal;

namespace HttpTwo
{
    public class Http2ConnectionSettings
    {
        public Http2ConnectionSettings (string url,  X509CertificateCollection certificates = null)
            : this (new Uri (url), certificates)
        {
        }

        public Http2ConnectionSettings (Uri uri,  X509CertificateCollection certificates = null)
            : this (uri.Host, (uint)uri.Port, uri.Scheme == Uri.UriSchemeHttps, certificates)
        {
        }

        public Http2ConnectionSettings (string host, uint port = 80, bool useTls = false, X509CertificateCollection certificates = null)
        {
            Host = host;
            Port = port;
            UseTls = useTls;
            Certificates = certificates;
        }

        public string Host { get; private set; }
        public uint Port { get; private set; }
        public bool UseTls { get; private set; }
        public X509CertificateCollection Certificates { get; private set; }

        public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds (60);
        public bool DisablePushPromise { get; set; } = false;
    }

    public class Http2Connection
    {
        public const string ConnectionPreface = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n";

        static Http2Connection ()
        {
            ServicePointManager.ServerCertificateValidationCallback += 
                (sender, certificate, chain, sslPolicyErrors) => true;
        }

        public Http2Connection (Http2ConnectionSettings connectionSettings, IStreamManager streamManager, IFlowControlManager flowControlManager)
        {
            this.flowControlManager = flowControlManager;
            this.streamManager = streamManager;

            ConnectionSettings = connectionSettings;
            Settings = new Http2Settings ();

            queue = new FrameQueue (flowControlManager);
        }

        public Http2Settings Settings { get; private set; }
        public Http2ConnectionSettings ConnectionSettings { get; private set; }

        IFlowControlManager flowControlManager;
        readonly IStreamManager streamManager;
        readonly FrameQueue queue;

        TcpClient tcp;
        Stream clientStream;
        SslStream sslStream;

        long receivedDataCount = 0;
        public uint ReceivedDataCount {
            get { return (uint)Interlocked.Read (ref receivedDataCount); }
        }

        public void Connect()
        {
            if (IsConnected ())
                return;

            tcp = new TcpClient ();

            // Disable Nagle for HTTP/2
            tcp.NoDelay = true;

            tcp.Connect(ConnectionSettings.Host, (int)ConnectionSettings.Port);

            if (ConnectionSettings.UseTls)
            {
                sslStream = new SslStream(tcp.GetStream(), false,
                    (sender, certificate, chain, sslPolicyErrors) => true);

                sslStream.AuthenticateAsClient(
                    ConnectionSettings.Host,
                    ConnectionSettings.Certificates ?? new X509CertificateCollection(),
                    System.Security.Authentication.SslProtocols.Tls,
                    false);
                clientStream = sslStream;
            }
            else
            {
                clientStream = tcp.GetStream();
            }

            // Ensure we have a size for the stream '0'
            flowControlManager.GetWindowSize(0);

            // Send out preface data
            var prefaceData = System.Text.Encoding.ASCII.GetBytes(ConnectionPreface);
            clientStream.Write(prefaceData, 0, prefaceData.Length);

            clientStream.Flush();

            // Start reading the stream on another thread
            var readTask = new Thread(() => {
                try { read(); }
                catch (Exception ex)
                {
                    Log.Debug("Read error: " + ex);
                    Disconnect();
                }
                Disconnect();
            });
            readTask.Start();

            // Start a thread to handle writing queued frames to the stream
            var writeTask = new Thread(() => {
                try
                {
                    write();
                }
                catch
                {
                    Disconnect();
                }
            });
            writeTask.Start();

            // Send initial blank settings frame
            var s = new SettingsFrame();
            if (ConnectionSettings.DisablePushPromise)
                s.EnablePush = false;

            QueueFrame(s);
        }

        public void Disconnect ()
        {
            // complete the blocking collection 
            queue.Complete ();

            // We want to clean up the connection here so let's just try to close/dispose
            // everything

            // Analysis disable EmptyGeneralCatchClause
            try { clientStream.Close (); } catch { }
            try { clientStream.Dispose (); } catch { }

            if (ConnectionSettings.UseTls && sslStream != null) {
                try { sslStream.Close (); } catch { }
                try { sslStream.Dispose (); } catch { }
            }

            try { tcp.Client.Shutdown (SocketShutdown.Both); } catch { }

            try { tcp.Close (); } catch { }
            // Analysis restore EmptyGeneralCatchClause

            tcp = null;
            sslStream = null;
            clientStream = null;
        }

        bool IsConnected ()
        {
            if (tcp == null || clientStream == null || tcp.Client == null)
                return false;

            if (!tcp.Connected || !tcp.Client.Connected)
                return false;

            if (!tcp.Client.Poll (1000, SelectMode.SelectRead)
                || !tcp.Client.Poll (1000, SelectMode.SelectWrite))
                return false;

            return true;
        }

        readonly Semaphore lockWrite = new Semaphore (1, 100);

        public void QueueFrame (IFrame frame)
        {
            queue.Enqueue(frame);
        }

        public void FreeUpWindowSpace ()
        {
            var sizeToFree = Interlocked.Exchange (ref receivedDataCount, 0);

            if (sizeToFree <= 0)
                return;
            
            QueueFrame(new WindowUpdateFrame {
                StreamIdentifier = 0,
                WindowSizeIncrement = (uint)sizeToFree
            });
        }

        readonly List<byte> buffer = new List<byte> ();

        void readed(int rx, byte[] b, out bool ended, Semaphore smp = null)
        {
            if (rx > 0)
            {
                // Add all the bytes read into our buffer list
                for (int i = 0; i < rx; i++)
                    buffer.Add(b[i]);

                while (true)
                {
                    // We need at least 9 bytes to process the frame 
                    // 9 octets is the frame header length
                    if (buffer.Count < 9)
                        break;

                    // Find out the frame length
                    // which is a 24 bit uint, so we need to convert this as c# uint is 32 bit
                    var flen = new byte[4];
                    flen[0] = 0x0;
                    flen[1] = buffer[0];
                    flen[2] = buffer[1];
                    flen[3] = buffer[2];

                    var frameLength = BitConverter.ToUInt32(ByteArrayExtensions.EnsureBigEndian(flen), 0);

                    // If we are expecting a payload that's bigger than what's in our buffer
                    // we should keep reading from the stream 
                    if (buffer.Count - 9 < frameLength)
                        break;

                    // If we made it this far, the buffer has all the data we need, let's get it out to process
                    var data = buffer.GetRange(0, (int)frameLength + 9).ToArray();
                    // remove the processed info from the buffer
                    buffer.RemoveRange(0, (int)frameLength + 9);

                    // Get the Frame Type so we can instantiate the right subclass
                    var frameType = data[3]; // 4th byte in frame header is TYPE

                    // we need to turn the stream id into a uint
                    var frameStreamIdData = new byte[4];
                    Array.Copy(data, 5, frameStreamIdData, 0, 4);
                    uint frameStreamId = Util.ConvertFromUInt31(ByteArrayExtensions.EnsureBigEndian(frameStreamIdData));

                    // Create a new typed instance of our abstract Frame
                    var frame = Frame.Create((FrameType)frameType);

                    try
                    {
                        // Call the specific subclass implementation to parse
                        frame.Parse(data);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Parsing Frame Failed: {0}", ex);
                        throw ex;
                    }

                    Log.Debug("<- {0}", frame);

                    // If it's a settings frame, we should note the values and
                    // return the frame with the Ack flag set
                    if (frame.Type == FrameType.Settings)
                    {

                        var settingsFrame = frame as SettingsFrame;

                        // Update our instance of settings with the new data
                        Settings.UpdateFromFrame(settingsFrame, flowControlManager);

                        // See if this was an ack, if not, return an empty 
                        // ack'd settings frame
                        if (!settingsFrame.Ack)
                            QueueFrame(new SettingsFrame { Ack = true });

                    }
                    else if (frame.Type == FrameType.Ping)
                    {

                        var pingFrame = frame as PingFrame;
                        // See if we need to respond to the ping request (if it's not-ack'd)
                        if (!pingFrame.Ack)
                        {
                            // Ack and respond
                            pingFrame.Ack = true;
                            QueueFrame(pingFrame);
                        }

                    }
                    else if (frame.Type == FrameType.Data)
                    {

                        // Increment our received data counter
                        Interlocked.Add(ref receivedDataCount, frame.PayloadLength);
                    }

                    // Some other frame type, just pass it along to the stream
                    var stream = streamManager.Get(frameStreamId);
                    stream.ProcessReceivedFrames(frame);
                }
                ended = false;
            }
            else
            {
                // Stream was closed, break out of reading loop
                ended = true;
            }
            if (smp != null)
            {
                smp.Release();
            }
        }

        void read()
        {
            byte[] b = new byte[4096];

            Semaphore smp = new Semaphore(0, 1);
            bool ended = false;
            while (!ended) {
                ended = false;
                try
                {
                    clientStream.BeginRead(b, 0, b.Length, (res) => readed(clientStream.EndRead(res), b, out ended, smp), null);
                    smp.WaitOne();
                }
                catch
                {
                    ended = true; 
                }
            }

            // Cleanup
            Disconnect();
        }

        void write()
        {
            foreach (var frame in queue.GetConsumingEnumerable ()) {
                if (frame == null) {
                    Log.Info ("Null frame dequeued");
                    continue;
                }

                Log.Debug ("-> {0}", frame);

                var data = AltLinq.ToArray(frame.ToBytes());

                lockWrite.WaitOne();

                try {
                    clientStream.Write(data, 0, data.Length);
                    clientStream.Flush();
                    var stream = streamManager.Get(frame.StreamIdentifier);
                    stream.ProcessSentFrame (frame);
                } catch (Exception ex) {
                    Log.Warn ("Error writing frame: {0}, {1}", frame.StreamIdentifier, ex);
                } finally {
                    lockWrite.Release();
                }
            }
        }
    }
}
