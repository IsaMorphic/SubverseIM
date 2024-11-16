﻿using MonoTorrent;
using MonoTorrent.Connections.Dht;
using MonoTorrent.Dht;
using MonoTorrent.PortForwarding;
using PgpCore;
using SIPSorcery.SIP;
using SubverseIM.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SubverseIM.Services.Implementation
{
    public class PeerService : IPeerService, IInjectable
    {
        private const int MAGIC_PORT_NUM = 6_03_03;

        private const string MAGIC_SECRET_PASSWORD = "#FreeTheInternet";

        private const string DEFAULT_BOOTSTRAPPER_ROOT = "https://subverse.network";

        private const string PUBLIC_KEY_PATH = "$/pkx/public.key";

        private const string PRIVATE_KEY_PATH = "$/pkx/private.key";

        private const string NODES_LIST_PATH = "$/pkx/nodes.list";

        private readonly INativeService nativeService;

        private readonly IDhtEngine dhtEngine;

        private readonly IDhtListener dhtListener;

        private readonly IPortForwarder portForwarder;

        private readonly HttpClient http;

        private readonly SIPUDPChannel sipChannel;

        private readonly SIPTransport sipTransport;

        private readonly Dictionary<string, SIPRequest> callIdMap;

        private readonly ConcurrentBag<TaskCompletionSource<IList<PeerInfo>>> peerInfoBag;

        private readonly ConcurrentBag<TaskCompletionSource<SubverseMessage>> messagesBag;

        private readonly PeriodicTimer timer;

        private readonly TaskCompletionSource<SubversePeerId> thisPeerTcs;

        private readonly TaskCompletionSource<IDbService> dbServiceTcs;
        private IDbService DbService => dbServiceTcs.Task.Result;

        private readonly TaskCompletionSource<ILauncherService> launcherServiceTcs;
        private ILauncherService LauncherService => launcherServiceTcs.Task.Result;

        public IPEndPoint? LocalEndPoint { get; private set; }

        public IDictionary<SubversePeerId, SubversePeer> CachedPeers { get; }

        public SubversePeerId ThisPeer => thisPeerTcs.Task.Result;

        public PeerService(INativeService nativeService)
        {
            this.nativeService = nativeService;

            dhtEngine = new DhtEngine();
            dhtListener = new DhtListener(new IPEndPoint(IPAddress.Any, 0));

            http = new() { BaseAddress = new Uri(DEFAULT_BOOTSTRAPPER_ROOT) };
            portForwarder = new MonoNatPortForwarder();

            sipChannel = new SIPUDPChannel(IPAddress.Any, MAGIC_PORT_NUM);
            sipTransport = new SIPTransport(stateless: true);

            thisPeerTcs = new();

            dbServiceTcs = new();
            launcherServiceTcs = new();

            callIdMap = new();
            peerInfoBag = new();
            messagesBag = new();

            timer = new(TimeSpan.FromSeconds(5));

            CachedPeers = new Dictionary<SubversePeerId, SubversePeer>();
        }

        private (Stream, Stream) GenerateKeysIfNone(IDbService dbService)
        {
            if (dbService.TryGetReadStream(PUBLIC_KEY_PATH, out Stream? publicKeyStream) &&
                dbService.TryGetReadStream(PRIVATE_KEY_PATH, out Stream? privateKeyStream))
            {
                return (publicKeyStream, privateKeyStream);
            }
            else
            {
                publicKeyStream = new MemoryStream();
                privateKeyStream = new MemoryStream();

                using (PGP pgp = new())
                {
                    pgp.GenerateKey(
                        publicKeyStream,
                        privateKeyStream,
                        password: "#FreeTheInternet"
                        );
                }

                using (Stream publicKeyStoreStream = dbService.CreateWriteStream(PUBLIC_KEY_PATH))
                using (Stream privateKeyStoreStream = dbService.CreateWriteStream(PRIVATE_KEY_PATH))
                {
                    publicKeyStream.Position = 0;
                    publicKeyStream.CopyTo(publicKeyStoreStream);

                    privateKeyStream.Position = 0;
                    privateKeyStream.CopyTo(privateKeyStoreStream);
                }

                publicKeyStream.Position = 0;
                privateKeyStream.Position = 0;

                return (publicKeyStream, privateKeyStream);
            }
        }

        private async Task<EncryptionKeys> GetPeerKeysAsync(SubversePeerId otherPeer, CancellationToken cancellationToken = default)
        {
            SubversePeer? peer;
            lock (CachedPeers)
            {
                CachedPeers.TryGetValue(otherPeer, out peer);
            }

            EncryptionKeys? peerKeys;
            if (peer is not null && peer.KeyContainer is null)
            {
                MemoryStream publicKeyStream = new MemoryStream();
                using (Stream responseStream = await http.GetStreamAsync($"pk?p={otherPeer}", cancellationToken))
                {
                    await responseStream.CopyToAsync(publicKeyStream);
                    publicKeyStream.Position = 0;
                }

                if (DbService.TryGetReadStream(PRIVATE_KEY_PATH, out Stream? privateKeyStream))
                {
                    peerKeys = new(publicKeyStream, privateKeyStream, MAGIC_SECRET_PASSWORD);
                    peer.KeyContainer = peerKeys;

                    publicKeyStream.Dispose();
                    privateKeyStream.Dispose();
                }
                else
                {
                    throw new InvalidOperationException("Could not find private key file in application database!");
                }
            }
            else if (peer?.KeyContainer is not null)
            {
                peerKeys = peer.KeyContainer;
            }
            else
            {
                throw new InvalidOperationException($"Could not find public key for Peer ID: {otherPeer}");
            }

            return peerKeys;
        }

        private async Task<bool> SynchronizePeersAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                SubversePeer peer;
                lock (CachedPeers)
                {
                    peer = CachedPeers[ThisPeer];
                }

                ReadOnlyMemory<byte> nodesBytes = await dhtEngine.SaveNodesAsync();
                using (Stream cacheStream = DbService.CreateWriteStream(NODES_LIST_PATH))
                {
                    cacheStream.Write(nodesBytes.Span);
                }

                byte[] requestBytes;
                using (PGP pgp = new(peer.KeyContainer))
                using (MemoryStream inputStream = new(nodesBytes.ToArray()))
                using (MemoryStream outputStream = new())
                {
                    await pgp.SignAsync(inputStream, outputStream);
                    requestBytes = outputStream.ToArray();
                }

                using (ByteArrayContent requestContent = new(requestBytes)
                { Headers = { ContentType = new("application/octet-stream") } })
                {
                    HttpResponseMessage response = await http.PostAsync($"nodes?p={ThisPeer}", requestContent, cancellationToken);
                    return await response.Content.ReadFromJsonAsync<bool>(cancellationToken);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private async Task SynchronizePeersAsync(SubversePeerId peerId, CancellationToken cancellationToken = default)
        {
            HttpResponseMessage response = await http.GetAsync($"nodes?p={peerId}", cancellationToken);
            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            dhtEngine.Add([responseBytes]);
        }

        private void DhtPeersFound(object? sender, PeersFoundEventArgs e)
        {
            TaskCompletionSource<IList<PeerInfo>>? tcs;
            if (!peerInfoBag.TryTake(out tcs))
            {
                peerInfoBag.Add(tcs = new());
            }

            tcs.TrySetResult(e.Peers);
        }

        private async Task SIPTransportRequestReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
        {
            SubversePeerId fromPeer = SubversePeerId.FromString(sipRequest.Header.From.FromURI.User);
            SubversePeerId toPeer = SubversePeerId.FromString(sipRequest.Header.To.ToURI.User);

            if (toPeer != ThisPeer)
            {
                await SendSIPRequestAsync(sipRequest);
                SIPResponse sipResponse = SIPResponse.GetResponse(
                    sipRequest, SIPResponseStatusCodesEnum.Accepted, "Message was forwarded."
                    );
                await sipTransport.SendResponseAsync(remoteEndPoint, sipResponse);
            }
            else
            {
                SubversePeer? peer;
                lock (CachedPeers)
                {
                    if (!CachedPeers.TryGetValue(fromPeer, out peer))
                    {
                        CachedPeers.Add(fromPeer, peer = new() { OtherPeer = fromPeer });
                    }
                }
                peer.RemoteEndPoint = remoteEndPoint.GetIPEndPoint();

                string messageContent;
                using (PGP pgp = new PGP(await GetPeerKeysAsync(fromPeer)))
                using (MemoryStream encryptedMessageStream = new(sipRequest.BodyBuffer))
                using (MemoryStream decryptedMessageStream = new())
                {
                    await pgp.DecryptAndVerifyAsync(encryptedMessageStream, decryptedMessageStream);
                    messageContent = Encoding.UTF8.GetString(decryptedMessageStream.ToArray());
                }

                if (!messagesBag.TryTake(out TaskCompletionSource<SubverseMessage>? tcs))
                {
                    messagesBag.Add(tcs = new());
                }

                tcs.SetResult(new SubverseMessage
                {
                    CallId = sipRequest.Header.CallId,
                    Content = messageContent,
                    Sender = fromPeer,
                    Recipient = toPeer,
                    DateSignedOn = DateTime.Parse(sipRequest.Header.Date),
                    TopicName = sipRequest.Header.To.ToURI.Parameters.Get("topic"),
                });

                SIPResponse sipResponse = SIPResponse.GetResponse(
                    sipRequest, SIPResponseStatusCodesEnum.Ok, "Message was delivered."
                    );
                await sipTransport.SendResponseAsync(remoteEndPoint, sipResponse);
            }
        }

        private Task SIPTransportResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPResponse sipResponse)
        {
            SubversePeerId peerId;
            lock (callIdMap)
            {
                if (!callIdMap.Remove(sipResponse.Header.CallId, out SIPRequest? sipRequest))
                {
                    throw new InvalidOperationException("Received response for invalid Call ID!");
                }
                else
                {
                    peerId = SubversePeerId.FromString(sipRequest.Header.To.ToURI.User);
                }
            }

            if (sipResponse.Status == SIPResponseStatusCodesEnum.Ok)
            {
                lock (CachedPeers)
                {
                    if (CachedPeers.TryGetValue(peerId, out SubversePeer? peer))
                    {
                        peer.RemoteEndPoint = remoteEndPoint.GetIPEndPoint();
                    }
                }
            }

            return Task.CompletedTask;
        }

        private async Task SendSIPRequestAsync(SIPRequest sipRequest, CancellationToken cancellationToken = default)
        {
            SubversePeerId toPeer = SubversePeerId.FromString(sipRequest.Header.To.ToURI.User);
            IPEndPoint? cachedEndPoint;
            lock (CachedPeers)
            {
                CachedPeers.TryGetValue(toPeer, out SubversePeer? peer);
                cachedEndPoint = peer?.RemoteEndPoint;
            }

            if (cachedEndPoint is not null)
            {
                await sipTransport.SendRequestAsync(new(cachedEndPoint), sipRequest);
            }

            dhtEngine.GetPeers(new(toPeer.GetBytes()));

            TaskCompletionSource<IList<PeerInfo>>? peerInfoTcs;
            if (!peerInfoBag.TryTake(out peerInfoTcs))
            {
                peerInfoBag.Add(peerInfoTcs = new());
            }

            IList<PeerInfo> peerInfo = await peerInfoTcs.Task;
            foreach (Uri peerUri in peerInfo.Select(x => x.ConnectionUri))
            {
                if (!IPAddress.TryParse(peerUri.DnsSafeHost, out IPAddress? ipAddress))
                {
                    continue;
                }

                IPEndPoint ipEndPoint = new(ipAddress, peerUri.Port);
                await sipTransport.SendRequestAsync(new(ipEndPoint), sipRequest);
            }
        }

        public async Task InjectAsync(IServiceManager serviceManager, CancellationToken cancellationToken)
        {
            serviceManager.GetOrRegister(nativeService);

            IDbService dbService = await serviceManager.GetWithAwaitAsync<IDbService>();
            (Stream publicKeyStream, Stream privateKeyStream) =
                GenerateKeysIfNone(dbService);
            dbServiceTcs.SetResult(dbService);

            ILauncherService launcherService = await serviceManager.GetWithAwaitAsync<ILauncherService>();
            launcherServiceTcs.SetResult(launcherService);

            EncryptionKeys myKeys = new(publicKeyStream, privateKeyStream, MAGIC_SECRET_PASSWORD);

            publicKeyStream.Dispose();
            privateKeyStream.Dispose();

            thisPeerTcs.SetResult(new(myKeys.PublicKey.GetFingerprint()));

            lock (CachedPeers)
            {
                CachedPeers.Add(ThisPeer, new SubversePeer
                {
                    OtherPeer = ThisPeer,
                    KeyContainer = myKeys
                });
            }

            dbService.GetMessagesWithPeersOnTopic([ThisPeer], null);
        }

        public async Task BootstrapSelfAsync(CancellationToken cancellationToken = default)
        {
            LocalEndPoint = sipChannel.ListeningEndPoint;

            sipTransport.SIPTransportRequestReceived += SIPTransportRequestReceived;
            sipTransport.SIPTransportResponseReceived += SIPTransportResponseReceived;
            sipTransport.AddSIPChannel(sipChannel);

            dhtEngine.PeersFound += DhtPeersFound;
            await dhtEngine.SetListenerAsync(dhtListener);
            using (MemoryStream bufferStream = new())
            {
                if (DbService.TryGetReadStream(NODES_LIST_PATH, out Stream? cacheStream))
                {
                    await cacheStream.CopyToAsync(bufferStream);
                    await dhtEngine.StartAsync(bufferStream.ToArray());
                }
                else
                {
                    await dhtEngine.StartAsync();
                }

                cacheStream?.Dispose();
            }

            if (DbService.TryGetReadStream(PUBLIC_KEY_PATH, out Stream? pkStream))
            {
                using (pkStream)
                using (StreamContent pkStreamContent = new(pkStream)
                { Headers = { ContentType = new("application/pgp-keys") } })
                {
                    await http.PostAsync("pk", pkStreamContent, cancellationToken);
                }
            }

            await portForwarder.StartAsync(cancellationToken);

            int portNum, retryCount = 0;
            Mapping? mapping = portForwarder.Mappings.Created.SingleOrDefault();
            for (portNum = MAGIC_PORT_NUM; retryCount++ < 3 && mapping is null; portNum++)
            {
                if (!portForwarder.Active) break;

                await portForwarder.RegisterMappingAsync(new Mapping(Protocol.Udp, LocalEndPoint.Port, portNum));
                await timer.WaitForNextTickAsync();

                mapping = portForwarder.Mappings.Created.SingleOrDefault();
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                SubversePeerId[] peers;
                lock (CachedPeers)
                {
                    peers = CachedPeers.Keys.ToArray();
                }

                await SynchronizePeersAsync(cancellationToken);
                await timer.WaitForNextTickAsync(cancellationToken);

                foreach (SubversePeerId otherPeer in peers)
                {
                    dhtEngine.Announce(new InfoHash(otherPeer.GetBytes()), 
                        mapping?.PublicPort ?? portNum);
                    await SynchronizePeersAsync(otherPeer, cancellationToken);
                    await timer.WaitForNextTickAsync(cancellationToken);
                }

            }

            await dhtEngine.StopAsync();
            sipTransport.Shutdown();
        }

        public Task<SubverseMessage> ReceiveMessageAsync(CancellationToken cancellationToken = default)
        {
            if (!messagesBag.TryTake(out TaskCompletionSource<SubverseMessage>? tcs))
            {
                messagesBag.Add(tcs = new());
            }

            return tcs.Task;
        }

        public async Task SendMessageAsync(SubverseMessage message, CancellationToken cancellationToken = default)
        {
            SIPURI requestToUri = SIPURI.ParseSIPURI($"sip:{message.Recipient}@subverse.network");
            if (message.TopicName is not null)
            {
                requestToUri.Parameters.Set("topic", message.TopicName);
            }

            SIPURI requestFromUri = SIPURI.ParseSIPURI($"sip:{message.Sender}@subverse.network");

            SIPRequest sipRequest = SIPRequest.GetRequest(
                SIPMethodsEnum.MESSAGE, requestToUri,
                new SIPToHeader(string.Empty, requestToUri, string.Empty),
                new SIPFromHeader(string.Empty, requestFromUri, string.Empty)
                );

            if (message.CallId is not null)
            {
                sipRequest.Header.CallId = message.CallId;
            }

            sipRequest.Header.SetDateHeader();

            using (PGP pgp = new(await GetPeerKeysAsync(message.Recipient, cancellationToken)))
            {
                sipRequest.Body = await pgp.EncryptAndSignAsync(message.Content);
            }

            lock (callIdMap)
            {
                if (!callIdMap.ContainsKey(sipRequest.Header.CallId))
                {
                    callIdMap.Add(sipRequest.Header.CallId, sipRequest);
                }
                else
                {
                    callIdMap[sipRequest.Header.CallId] = sipRequest;
                }
            }

            _ = Task.Run(async Task? () =>
            {
                bool flag;
                do
                {
                    await SendSIPRequestAsync(sipRequest, cancellationToken);
                    await Task.Delay(150);

                    lock (callIdMap)
                    {
                        flag = callIdMap.ContainsKey(sipRequest.Header.CallId);
                    }
                } while (flag);
            });
        }

        public async Task SendInviteAsync(CancellationToken cancellationToken = default)
        {
            string inviteId = await http.GetFromJsonAsync<string>($"invite?p={ThisPeer}") ??
                throw new InvalidOperationException("Failed to resolve inviteUri!");
            await LauncherService.ShareStringToAppAsync("Send Invite Via App", $"{DEFAULT_BOOTSTRAPPER_ROOT}/invite/{inviteId}");
        }
    }
}
