﻿using System;
using System.Threading;
using System.Threading.Tasks;

namespace Loom.Messaging
{
    public sealed class DebouncingMessageHandler : IMessageHandler
    {
        private readonly IDebouncer _debouncer;
        private readonly IMessageHandler _handler;

        public DebouncingMessageHandler(
            IDebouncer debouncer, IMessageHandler handler)
        {
            _debouncer = debouncer;
            _handler = handler;
        }

        public bool Accepts(Message message) => _handler.Accepts(message);

        public Task Handle(Message message, CancellationToken cancellationToken = default)
        {
            if (message is null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            return message.Data is IDebouncable debouncable
                ? TryHandle(debouncable, message, cancellationToken)
                : HandleImmediately(message, cancellationToken);
        }

        private Task<bool> TryHandle(
            IDebouncable debouncable,
            Message message,
            CancellationToken cancellationToken)
        {
            return _debouncer.TryConsume(
                debouncable,
                _ => _handler.Handle(message, cancellationToken));
        }

        private Task HandleImmediately(
            Message message,
            CancellationToken cancellationToken)
        {
            return _handler.Handle(message, cancellationToken);
        }
    }
}
