﻿using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using AndroidX.Core.App;
using SubverseIM.Android.Services;
using SubverseIM.Models;
using SubverseIM.Services;
using SubverseIM.Services.Implementation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SubverseIM.Android
{
    [Service()]
    public class WrappedPeerService : Service, INativeService
    {
        private const string MSG_CHANNEL_ID = "com.ChosenFewSoftware.SubverseIM.UserMessage";

        private readonly IPeerService peerService;

        private readonly Dictionary<int, NotificationCompat.MessagingStyle> notificationMap;

        public WrappedPeerService()
        {
            peerService = new PeerService(this);
            notificationMap = new();
        }

        public override IBinder? OnBind(Intent? intent)
        {
            CreateNotificationChannel();
            return new ServiceBinder<IPeerService>(peerService);
        }

        private void CreateNotificationChannel()
        {
            NotificationChannel channel = new NotificationChannel(
                MSG_CHANNEL_ID, new Java.Lang.String("User Messages"),
                NotificationImportance.High);
            channel.Description = "Inbound messages from your contacts";
            // Register the channel with the system; you can't change the importance
            // or other notification behaviors after this.
            NotificationManager? manager = NotificationManager.FromContext(this);
            manager?.CreateNotificationChannel(channel);
        }

        public void ClearNotificationForPeer(SubversePeerId otherPeer)
        {
            lock (notificationMap) 
            {
                notificationMap.Remove(otherPeer.GetHashCode());
            }
        }

        public async Task SendPushNotificationAsync(IServiceManager serviceManager, SubverseMessage message, CancellationToken cancellationToken = default)
        {
            int notificationId = message.Sender.GetHashCode();

            IDbService dbService = await serviceManager.GetWithAwaitAsync<IDbService>(cancellationToken);
            SubverseContact? contact = dbService.GetContact(message.Sender);

            Bitmap? avatarBitmap;
            if (contact?.ImagePath is not null && dbService
                .TryGetReadStream(contact.ImagePath, out Stream? avatarStream))
            {
                avatarBitmap = await BitmapFactory.DecodeStreamAsync(avatarStream);
            }
            else
            {
                avatarBitmap = null;
            }

            NotificationCompat.MessagingStyle? messagingStyle;
            lock (notificationMap)
            {
                if (!notificationMap.TryGetValue(message.Sender.GetHashCode(), out messagingStyle))
                {
                    notificationMap.Add(notificationId, messagingStyle = new(contact?.DisplayName ?? "Anonymous"));
                }
            }

            long timestamp = ((DateTimeOffset)message.DateSignedOn)
                .ToUnixTimeMilliseconds();
            messagingStyle.AddMessage(new(message.Content, timestamp, contact?.DisplayName));

            Notification notif = new NotificationCompat.Builder(this, MSG_CHANNEL_ID)
                .SetSmallIcon(Resource.Drawable.Icon)
                .SetLargeIcon(avatarBitmap)
                .SetStyle(messagingStyle)
                .SetPriority(NotificationCompat.PriorityHigh)
                .Build();

            NotificationManager? manager = NotificationManager.FromContext(this);
            manager?.Notify(notificationId, notif);
        }
    }
}
