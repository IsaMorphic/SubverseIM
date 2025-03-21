﻿using LiteDB;
using SubverseIM.Core;
using SubverseIM.Models;
using SubverseIM.Serializers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace SubverseIM.Services
{
    public interface IDbService : IDisposableService
    {
        SubverseConfig? GetConfig();

        bool UpdateConfig(SubverseConfig config);

        IEnumerable<SubverseContact> GetContacts();

        SubverseContact? GetContact(SubversePeerId otherPeer);

        IEnumerable<SubverseTorrent> GetTorrents();

        SubverseTorrent? GetTorrent(string magnetUri);

        IEnumerable<SubverseMessage> GetMessagesWithPeersOnTopic(HashSet<SubversePeerId> otherPeers, string? topicName = null, bool orderFlag = false);

        IEnumerable<SubverseMessage> GetAllUndeliveredMessages();

        IReadOnlyDictionary<string, IEnumerable<SubversePeerId>> GetAllMessageTopics();

        SubverseMessage? GetMessageByCallId(string callId);

        bool InsertOrUpdateItem(SubverseContact newItem);

        bool InsertOrUpdateItem(SubverseTorrent newItem);

        bool InsertOrUpdateItem(SubverseMessage newItem);

        bool DeleteItemById<T>(BsonValue id);

        void DeleteAllMessagesOfTopic(string topicName);

        void WriteAllMessagesOfTopic(ISerializer<SubverseMessage> serializer, string topicName);

        bool TryGetReadStream(string path, [NotNullWhen(true)] out Stream? stream);

        Stream CreateWriteStream(string path);
    }
}
