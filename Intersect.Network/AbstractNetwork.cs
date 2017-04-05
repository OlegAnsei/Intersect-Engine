﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Intersect.Logging;
using Intersect.Memory;
using Intersect.Network.Handlers;
using Intersect.Network.Packets.Ping;
using Intersect.Threading;
using Lidgren.Network;

namespace Intersect.Network
{
    public abstract class AbstractNetwork : INetwork
    {
        private readonly object mLock;
        private bool mDisposed;

        private IList<NetworkThread> mThreads;
        private IDictionary<Guid, NetworkThread> mThreadLookup;
        private IDictionary<Guid, ConnectionMetadata> mConnectionLookup;
        private IDictionary<long, Guid> mConnectionGuidLookup;

        public Thread CurrentThread { get; }
        public PacketDispatcher Dispatcher { get; }

        protected NetPeerConfiguration Config { get; }
        public NetPeer Peer { get; }

        protected RSACryptoServiceProvider Rsa { get; }
        protected RandomNumberGenerator Rng { get; }

        public Guid Guid { get; protected set; }

        protected AbstractNetwork(NetPeerConfiguration config, NetPeer peer)
        {
            mLock = new object();

            mThreads = new List<NetworkThread>();
            mThreadLookup = new Dictionary<Guid, NetworkThread>();
            mConnectionLookup = new Dictionary<Guid, ConnectionMetadata>();
            mConnectionGuidLookup = new Dictionary<long, Guid>();

            Dispatcher = new PacketDispatcher();
            CurrentThread = new Thread(Loop);

            Config = config;
            Peer = peer;

            Guid = Guid.NewGuid();

            Rng = new RNGCryptoServiceProvider();

            Rsa = new RSACryptoServiceProvider();
            Rsa.ImportParameters(GetRsaKey());

            CreateThreads();
        }

        protected abstract RSAParameters GetRsaKey();

        public bool IsRunning { get; private set; }

        public void Dispose()
        {
            if (mLock == null) throw new ArgumentNullException();
            lock (mLock)
            {
                if (mDisposed) return;

                DoDispose();

                mDisposed = true;
            }
        }

        protected virtual void DoDispose() => Disconnect("Disposing...");

        public bool Start()
        {
            if (mLock == null) throw new ArgumentNullException();
            lock (mLock)
            {
                if (IsRunning) return false;

                IsRunning = true;

                RegisterPackets();
                RegisterHandlers();

                CurrentThread?.Start();

                foreach (var thread in mThreads) thread.Start();

                return IsRunning;
            }
        }

        public bool Stop()
        {
            if (mLock == null) throw new ArgumentNullException();
            lock (mLock)
            {
                if (!IsRunning) return false;
                IsRunning = false;
                return true;
            }
        }

        protected abstract void OnStart();

        protected abstract void OnStop();

        public virtual void Disconnect(string message = "")
        {
            Peer?.Shutdown(message);
            Stop();
        }

        public virtual bool Send(IPacket packet)
            => mConnectionLookup?.Keys.All(guid => Send(guid, packet)) ?? false;

        public virtual bool Send(Guid guid, IPacket packet)
        {
            if (!mConnectionLookup.TryGetValue(guid, out ConnectionMetadata metadata)) return false;

            var message = Peer.CreateMessage();
            IBuffer buffer = new LidgrenBuffer(message);
            buffer.Write(guid.ToByteArray(), 16);
            if (!packet.Write(ref buffer)) throw new Exception();

            metadata.Aes.Encrypt(message);
            var result = Peer.SendMessage(message, metadata.Connection, NetDeliveryMethod.ReliableOrdered);
            switch (result)
            {
                case NetSendResult.Sent:
                case NetSendResult.Queued:
                    Log.Debug($"Sent '{packet.GetType().Name}'.");
                    return true;

                default:
                    Log.Debug($"Failed to send '{packet.GetType().Name}'.");
                    return false;
            }
        }

        protected virtual void RegisterPackets()
        {
            if (!PacketRegistry.Instance.Register(new PingPacketGroup())) throw new Exception();
        }

        protected virtual void RegisterHandlers()
        {
            if (!Dispatcher.RegisterHandler(typeof(PingPacket), new PingHandler().HandlePing)) throw new Exception();
        }

        protected virtual bool HandleConnectionApproval(NetIncomingMessage request)
        {
            Log.Info($"{request.MessageType}: {request}");
            return false;
        }

        protected virtual bool HandleConnected(NetIncomingMessage request) => true;

        protected bool HasConnection(ConnectionMetadata metadata)
        {
            if (metadata?.Connection == null) throw new ArgumentNullException();
            if (mConnectionLookup == null) throw new ArgumentNullException();
            if (mConnectionGuidLookup == null) throw new ArgumentNullException();

            return mConnectionLookup.ContainsKey(metadata.Guid)
                && mConnectionGuidLookup.ContainsKey(metadata.Connection.RemoteUniqueIdentifier);
        }

        protected bool HasConnection(Guid guid)
        {
            if (mConnectionLookup == null) throw new ArgumentNullException();
            if (mConnectionGuidLookup == null) throw new ArgumentNullException();
            return mConnectionLookup.TryGetValue(guid, out ConnectionMetadata metadata)
                && mConnectionGuidLookup.ContainsKey(metadata.Connection.RemoteUniqueIdentifier);
        }

        protected bool HasConnection(long lidgrenId)
        {
            if (mConnectionLookup == null) throw new ArgumentNullException();
            if (mConnectionGuidLookup == null) throw new ArgumentNullException();
            return mConnectionGuidLookup.TryGetValue(lidgrenId, out Guid guid)
                && mConnectionLookup.ContainsKey(guid);
        }

        private NetworkThread PickThread()
        {
            lock (mThreads)
            {
                NetworkThread emptiest = null;
                foreach (var thread in mThreads)
                {
                    if (emptiest == null)
                    {
                        emptiest = thread;
                        continue;
                    }

                    if (emptiest.Connections.Count > thread.Connections.Count)
                    {
                        emptiest = thread;
                        continue;
                    }
                }
                return emptiest;
            }
        }

        protected void AddConnection(ConnectionMetadata metadata)
        {
            if (metadata?.Connection == null) return;
            if (mConnectionLookup == null) throw new ArgumentNullException();
            if (mConnectionGuidLookup == null) throw new ArgumentNullException();

            mConnectionLookup.Add(metadata.Guid, metadata);
            mConnectionGuidLookup.Add(metadata.Connection.RemoteUniqueIdentifier, metadata.Guid);

            var thread = PickThread();
            thread.Connections.Add(metadata);
            mThreadLookup.Add(metadata.Guid, thread);
        }

        protected void RemoveConnection(long lidgrenId)
        {
            if (!mConnectionGuidLookup.TryGetValue(lidgrenId, out Guid guid)) return;
            if (!mConnectionLookup.TryGetValue(guid, out ConnectionMetadata metadata)) return;
            RemoveConnection(metadata);
        }

        protected void RemoveConnection(ConnectionMetadata metadata)
        {
            if (metadata?.Connection == null) return;
            if (mConnectionLookup == null) throw new ArgumentNullException();
            if (mConnectionGuidLookup == null) throw new ArgumentNullException();

            mConnectionLookup.Remove(metadata.Guid);
            mConnectionGuidLookup.Remove(metadata.Connection.RemoteUniqueIdentifier);

            if (!mThreadLookup.TryGetValue(metadata.Guid, out NetworkThread thread)) return;
            thread.Connections.Remove(metadata);
            mThreadLookup.Remove(metadata.Guid);
        }

        protected virtual bool HandleStatusChanged(NetIncomingMessage request)
        {
            if (request?.SenderConnection == null) return false;
            var lidgrenId = request.SenderConnection.RemoteUniqueIdentifier;
            var status = (NetConnectionStatus)request.ReadByte();
            Log.Info($"Status of {request.SenderConnection}: {status}");
            switch (status)
            {
                case NetConnectionStatus.Connected:
                    return HandleConnected(request);

                case NetConnectionStatus.Disconnecting:
                    Log.Info("Disconnecting...");
                    Log.Info("'{request.ReadString()}'");
                    return true;

                case NetConnectionStatus.Disconnected:
                    if (mConnectionGuidLookup.TryGetValue(lidgrenId, out Guid guid))
                    {
                        Log.Info($"Removing endpoint {NetUtility.ToHexString(lidgrenId)} ({guid})...");
                        mConnectionGuidLookup.Remove(lidgrenId);
                        mConnectionLookup.Remove(guid);
                    }

                    Log.Info($"Disconnected from endpoint {NetUtility.ToHexString(lidgrenId)} .");
                    return true;

                default:
                    return true;
            }

            return false;
        }

        private void Loop()
        {
            OnStart();

            while (IsRunning)
            {
                //Log.Info("Waiting for message...");
                if (!Peer.ReadMessage(out NetIncomingMessage message)) continue;
                switch (message.MessageType)
                {
                    case NetIncomingMessageType.ConnectionApproval:
                        if (!HandleConnectionApproval(message))
                        {
                            message.SenderConnection.Deny();
                        }
                        break;

                    case NetIncomingMessageType.Data:
                        var lidgrenId = message.SenderConnection?.RemoteUniqueIdentifier ?? -1;
                        if (mConnectionGuidLookup.TryGetValue(lidgrenId, out Guid guid))
                        {
                            if (mConnectionLookup.TryGetValue(guid, out ConnectionMetadata connection))
                            {
                                if (connection.Aes.Decrypt(message))
                                {
                                    EnqueueIncomingDataMessage(connection, message);
                                    break;
                                }

                                Log.Error($"Error decrypting from Lidgren:{guid}.");
                            }

                            Log.Error($"Error reading from Lidgren:{guid}.");
                        }

                        Log.Error($"Error reading from Lidgren Remote:{lidgrenId}.");
                        break;

                    case NetIncomingMessageType.VerboseDebugMessage:
                        Log.Verbose(message.ReadString());
                        break;

                    case NetIncomingMessageType.DebugMessage:
                        Log.Debug(message.ReadString());
                        break;

                    case NetIncomingMessageType.WarningMessage:
                        Log.Warn(message.ReadString());
                        break;

                    case NetIncomingMessageType.ErrorMessage:
                        Log.Error(message.ReadString());
                        break;

                    case NetIncomingMessageType.StatusChanged:
                        if (!HandleStatusChanged(message))
                        {
                            message.SenderConnection.Disconnect("Error occurred processing status change.");
                        }
                        break;

                    case NetIncomingMessageType.Error:
                        Log.Info($"{message.MessageType}: {message}");
                        break;

                    case NetIncomingMessageType.UnconnectedData:
                        Log.Info($"{message.MessageType}: {message}");
                        break;

                    case NetIncomingMessageType.Receipt:
                        Log.Info($"{message.MessageType}: {message}");
                        break;

                    case NetIncomingMessageType.DiscoveryRequest:
                        Log.Info($"{message.MessageType}: {message}");
                        break;

                    case NetIncomingMessageType.DiscoveryResponse:
                        Log.Info($"{message.MessageType}: {message}");
                        break;

                    case NetIncomingMessageType.NatIntroductionSuccess:
                        Log.Info($"{message.MessageType}: {message}");
                        break;

                    case NetIncomingMessageType.ConnectionLatencyUpdated:
                        Log.Info($"{message.MessageType}: {message}");
                        break;

                    default:
                        Log.Info($"{message.MessageType}: {message}");
                        break;
                }

                Peer.Recycle(message);
            }

            OnStop();
        }

        protected void AssignNetworkThread(Guid guid, NetworkThread networkThread)
        {
            if (mThreadLookup?.ContainsKey(guid) ?? false)
            {
                mThreadLookup.Remove(guid);
            }

            mThreadLookup?.Add(guid, networkThread);
        }

        private void EnqueueIncomingDataMessage(IConnection connection, NetIncomingMessage message)
        {
            if (message == null) throw new ArgumentNullException();

            if (!message.ReadBytes(16, out byte[] guidBuffer)) return;

            var guid = new Guid(guidBuffer);
            if (!mThreadLookup.TryGetValue(guid, out NetworkThread thread)) return;

            var packetGroup = (PacketGroups)message.ReadByte();
            var group = PacketRegistry.Instance.GetGroup(packetGroup);
            if (group == null) return;

            IBuffer buffer = new LidgrenBuffer(message);
            var packet = group.Create(connection, buffer);
            if (packet.Read(ref buffer))
            {
                if (thread.Queue == null) throw new ArgumentNullException();
                thread.Queue.Enqueue(packet);
            }
            else
            {
                MemoryDump.Dump(message.Data);
            }
        }

        protected abstract int CalculateNumberOfThreads();

        protected abstract IThreadYield CreateThreadYield();

        private void CreateThreads()
        {
            var threadCount = CalculateNumberOfThreads();
            for (var i = 0; i < threadCount; i++)
                mThreads?.Add(new NetworkThread(Dispatcher, CreateThreadYield(), $"Network Thread #{i}"));
        }

        protected static RSAParameters LoadKeyFromAssembly(Assembly assembly, string resourceName, bool isPublic)
        {
            if (assembly == null) throw new ArgumentNullException();
            if (string.IsNullOrWhiteSpace(resourceName)) throw new ArgumentNullException();

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                return LoadKeyFromStream(stream, isPublic);
            }
        }

        protected static RSAParameters LoadKeyFromFile(string filepath, bool isPublic)
        {
            if (string.IsNullOrWhiteSpace(filepath)) throw new ArgumentNullException();
            using (var stream = new FileStream(filepath, FileMode.Open))
            {
                return LoadKeyFromStream(stream, isPublic);
            }
        }

        protected static RSAParameters LoadKeyFromStream(Stream stream, bool isPublic)
        {
            if (stream == null) throw new ArgumentNullException();

            using (var reader = new BinaryReader(new GZipStream(stream, CompressionMode.Decompress)))
            {
                var rsaParameters = (isPublic ? ReadPublicKey(reader) : ReadPrivateKey(reader));

                DumpKey(rsaParameters, isPublic);

                reader.Close();

                return rsaParameters;
            }
        }

        private static RSAParameters ReadPrivateKey(BinaryReader reader)
        {
            var c = reader.ReadInt16();

            return new RSAParameters
            {
                D = reader.ReadBytes(c >> 3),
                DP = reader.ReadBytes(c >> 4),
                DQ = reader.ReadBytes(c >> 4),
                Exponent = reader.ReadBytes(3),
                InverseQ = reader.ReadBytes(c >> 4),
                Modulus = reader.ReadBytes(c >> 3),
                P = reader.ReadBytes(c >> 4),
                Q = reader.ReadBytes(c >> 4)
            };
        }

        private static RSAParameters ReadPublicKey(BinaryReader pa)
        {
            var c = pa.ReadInt16();

            return new RSAParameters
            {
                Exponent = pa.ReadBytes(3),
                Modulus = pa.ReadBytes(c >> 3)
            };
        }

        public IConnection FindConnection(long lidgrenId)
            => mConnectionGuidLookup.TryGetValue(lidgrenId, out Guid guid)
            ? FindConnection(guid) : null;

        public IConnection FindConnection(Guid guid)
            => mConnectionLookup.TryGetValue(guid, out ConnectionMetadata connection)
            ? connection : null;

        protected static void DumpKey(RSAParameters parameters, bool isPublic)
        {
#if DEBUG
            Log.Verbose($"Exponent: {BitConverter.ToString(parameters.Exponent)}");
            Log.Verbose($"Modulus: {BitConverter.ToString(parameters.Modulus)}");

            if (isPublic) return;
            Log.Verbose($"D: {BitConverter.ToString(parameters.D)}");
            Log.Verbose($"DP: {BitConverter.ToString(parameters.DP)}");
            Log.Verbose($"DQ: {BitConverter.ToString(parameters.DQ)}");
            Log.Verbose($"InverseQ: {BitConverter.ToString(parameters.InverseQ)}");
            Log.Verbose($"P: {BitConverter.ToString(parameters.P)}");
            Log.Verbose($"Q: {BitConverter.ToString(parameters.Q)}");
#endif
        }
    }
}