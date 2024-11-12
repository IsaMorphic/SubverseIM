﻿using ReactiveUI;
using SubverseIM.Models;
using SubverseIM.Services;
using SubverseIM.ViewModels.Pages;
using System.Threading.Tasks;

namespace SubverseIM.ViewModels.Components
{
    public class MessageViewModel : ViewModelBase
    {
        private readonly MessagePageViewModel messagePageView;

        private readonly SubverseContact? fromContact;

        private readonly SubverseMessage innerMessage;

        private bool isSelected;
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                this.RaiseAndSetIfChanged(ref isSelected, value);
            }
        }

        public string Content => innerMessage.Content ?? string.Empty;

        public string DateString => innerMessage
            .DateSignedOn.ToLocalTime()
            .ToString("dd/MM/yy\nHH:mm:ss");

        public string FromName => fromContact?.DisplayName ?? "You";

        public MessageViewModel(MessagePageViewModel messagePageView, SubverseContact? fromContact, SubverseMessage innerMessage)
        {
            this.messagePageView = messagePageView;
            this.fromContact = fromContact;
            this.innerMessage = innerMessage;
        }

        public async Task DeleteCommandAsync() 
        {
            IDbService dbService = await messagePageView.ServiceManager.GetWithAwaitAsync<IDbService>();
            dbService.DeleteItemById<SubverseMessage>(innerMessage.Id);

            messagePageView.MessageList.Remove(this);
        }
    }
}