using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using PgpCore;
using SIPSorcery.SIP;
using SubverseIM.Models;

namespace SubverseIM.Services.Implementation;

public class MessageService : IMessageService, IDisposable, IInjectable
{
    private readonly Dictionary<string, SIPRequest> callIdMap;

    private readonly ConcurrentBag<TaskCompletionSource<SubverseMessage>> messagesBag;

    private readonly SIPUDPChannel sipChannel;

    private readonly SIPTransport sipTransport;

    private readonly TaskCompletionSource<IServiceManager> serviceManagerTcs;

    public IPEndPoint LocalEndPoint { get; }

    public IDictionary<SubversePeerId, SubversePeer> CachedPeers { get; }

    public MessageService()
    {
        callIdMap = new();
        messagesBag = new();

        sipChannel = new SIPUDPChannel(IPAddress.Any, BootstrapperService.DEFAULT_PORT_NUM);
        sipTransport = new SIPTransport(stateless: true);

        serviceManagerTcs = new();

        sipTransport.SIPTransportRequestReceived += SIPTransportRequestReceived;
        sipTransport.SIPTransportResponseReceived += SIPTransportResponseReceived;
        sipTransport.AddSIPChannel(sipChannel);

        LocalEndPoint = sipChannel.ListeningEndPoint;
        CachedPeers = new Dictionary<SubversePeerId, SubversePeer>();
    }

    private async Task SIPTransportRequestReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
    {
        IServiceManager serviceManager = await serviceManagerTcs.Task;

        IBootstrapperService bootstrapperService = await serviceManager.GetWithAwaitAsync<IBootstrapperService>();
        IDbService dbService = await serviceManager.GetWithAwaitAsync<IDbService>();

        SubversePeerId fromPeer = SubversePeerId.FromString(sipRequest.Header.From.FromURI.User);
        string fromName = sipRequest.Header.From.FromName;

        SubversePeerId toPeer = SubversePeerId.FromString(sipRequest.Header.To.ToURI.User);
        string toName = sipRequest.Header.To.ToName;

        string? messageContent;
        try
        {
            using (PGP pgp = new PGP(await bootstrapperService.GetPeerKeysAsync(fromPeer)))
            using (MemoryStream encryptedMessageStream = new(sipRequest.BodyBuffer))
            using (MemoryStream decryptedMessageStream = new())
            {
                await pgp.DecryptAndVerifyAsync(encryptedMessageStream, decryptedMessageStream);
                messageContent = Encoding.UTF8.GetString(decryptedMessageStream.ToArray());
            }
        }
        catch
        {
            messageContent = sipRequest.Body;
        }

        IEnumerable<SubversePeerId> recipients = [toPeer, ..sipRequest.Header.Contact
                        .Select(x => SubversePeerId.FromString(x.ContactURI.User))];

        IEnumerable<string?> localRecipientNames = recipients
            .Select(x => dbService.GetContact(x)?.DisplayName);

        IEnumerable<string> remoteRecipientNames =
            [toName, .. sipRequest.Header.Contact.Select(x => x.ContactName)];

        SubverseMessage message = new SubverseMessage
        {
            CallId = sipRequest.Header.CallId,
            Content = messageContent,
            Sender = fromPeer,
            SenderName = fromName,
            Recipients = recipients.ToArray(),
            RecipientNames = localRecipientNames
                .Zip(remoteRecipientNames)
                .Select(x => x.First ?? x.Second)
                .ToArray(),
            DateSignedOn = DateTime.Parse(sipRequest.Header.Date),
            TopicName = sipRequest.URI.Parameters.Get("topic"),
        };

        SubversePeer? peer;
        lock (CachedPeers)
        {
            if (!CachedPeers.TryGetValue(fromPeer, out peer))
            {
                CachedPeers.Add(fromPeer, peer = new() { OtherPeer = fromPeer });
            }
        }
        peer.RemoteEndPoint = remoteEndPoint.GetIPEndPoint();

        bool hasReachedDestination = toPeer == await bootstrapperService.GetPeerIdAsync();
        message.WasDecrypted = message.WasDelivered = hasReachedDestination;
        if (hasReachedDestination)
        {
            if (!messagesBag.TryTake(out TaskCompletionSource<SubverseMessage>? tcs))
            {
                messagesBag.Add(tcs = new());
            }
            tcs.SetResult(message);

            SIPResponse sipResponse = SIPResponse.GetResponse(
                sipRequest, SIPResponseStatusCodesEnum.Ok, "Message was delivered."
                );
            await sipTransport.SendResponseAsync(remoteEndPoint, sipResponse);
        }
        else
        {
            dbService.InsertOrUpdateItem(message);
            await SendSIPRequestAsync(sipRequest);

            SIPResponse sipResponse = SIPResponse.GetResponse(
                sipRequest, SIPResponseStatusCodesEnum.Accepted, "Message was forwarded."
                );
            await sipTransport.SendResponseAsync(remoteEndPoint, sipResponse);
        }
    }

    private async Task SIPTransportResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPResponse sipResponse)
    {
        IServiceManager serviceManager = await serviceManagerTcs.Task;
        IDbService dbService = await serviceManager.GetWithAwaitAsync<IDbService>();

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

        SubverseMessage? message = dbService.GetMessageByCallId(sipResponse.Header.CallId);
        if (message is not null)
        {
            message.WasDelivered = true;
            dbService.InsertOrUpdateItem(message);
        }
    }

    private async Task SendSIPRequestAsync(SIPRequest sipRequest)
    {
        IServiceManager serviceManager = await serviceManagerTcs.Task;
        IBootstrapperService bootstrapperService = await serviceManager.GetWithAwaitAsync<IBootstrapperService>();

        SubversePeerId toPeer = SubversePeerId.FromString(sipRequest.URI.User);
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

        IList<PeerInfo> peerInfo = await bootstrapperService.GetPeerInfoAsync(toPeer);
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

    public async Task<SubverseMessage> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        if (!messagesBag.TryTake(out TaskCompletionSource<SubverseMessage>? tcs))
        {
            messagesBag.Add(tcs = new());
        }

        return await tcs.Task.WaitAsync(cancellationToken);
    }

    public async Task SendMessageAsync(SubverseMessage message, CancellationToken cancellationToken = default)
    {
        IServiceManager serviceManager = await serviceManagerTcs.Task;
        IBootstrapperService bootstrapperService = await serviceManager.GetWithAwaitAsync<IBootstrapperService>();

        List<Task> sendTasks = new();
        foreach ((SubversePeerId recipient, string contactName) in message.Recipients.Zip(message.RecipientNames))
        {
            sendTasks.Add(Task.Run(async Task? () =>
            {
                SIPURI requestUri = SIPURI.ParseSIPURI($"sip:{recipient}@subverse.network");
                if (message.TopicName is not null)
                {
                    requestUri.Parameters.Set("topic", message.TopicName);
                }

                SIPURI toURI = SIPURI.ParseSIPURI($"sip:{recipient}@subverse.network");
                SIPURI fromURI = SIPURI.ParseSIPURI($"sip:{message.Sender}@subverse.network");

                SIPRequest sipRequest = SIPRequest.GetRequest(
                    SIPMethodsEnum.MESSAGE, requestUri,
                    new(contactName, toURI, null),
                    new(message.SenderName, fromURI, null)
                    );

                if (message.CallId is not null)
                {
                    sipRequest.Header.CallId = message.CallId;
                }

                sipRequest.Header.SetDateHeader();

                sipRequest.Header.Contact = new();
                for (int i = 0; i < message.Recipients.Length; i++)
                {
                    if (message.Recipients[i] == recipient) continue;

                    SIPURI contactUri = SIPURI.ParseSIPURI($"sip:{message.Recipients[i]}@subverse.network");
                    sipRequest.Header.Contact.Add(new(message.RecipientNames[i], contactUri));
                }

                if (message.Sender == await bootstrapperService.GetPeerIdAsync())
                {
                    using (PGP pgp = new(await bootstrapperService.GetPeerKeysAsync(recipient, cancellationToken)))
                    {
                        sipRequest.Body = await pgp.EncryptAndSignAsync(message.Content);
                    }
                }
                else
                {
                    sipRequest.Body = message.Content;
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

                bool flag;
                using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(1500));
                do
                {
                    await SendSIPRequestAsync(sipRequest);
                    await timer.WaitForNextTickAsync(cancellationToken);
                    lock (callIdMap)
                    {
                        flag = callIdMap.ContainsKey(sipRequest.Header.CallId);
                    }
                } while (flag && !cancellationToken.IsCancellationRequested);
            }));
        }

        await Task.WhenAll(sendTasks);
    }

    public Task InjectAsync(IServiceManager serviceManager)
    {
        serviceManagerTcs.SetResult(serviceManager);
        return Task.CompletedTask;
    }


    #region IDisposable API

    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                sipChannel.Dispose();
                sipTransport.Dispose();
            }
            CachedPeers.Clear();    
            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    #endregion
}
