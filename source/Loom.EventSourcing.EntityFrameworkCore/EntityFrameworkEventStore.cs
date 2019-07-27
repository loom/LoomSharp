﻿namespace Loom.EventSourcing.EntityFrameworkCore
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using Loom.Messaging;
    using Microsoft.EntityFrameworkCore;
    using Newtonsoft.Json;

    public class EntityFrameworkEventStore<T> :
        IEventStore<T>, IEventCollector, IEventReader
    {
        private readonly Func<EventStoreContext> _contextFactory;
        private readonly TypeResolver _typeResolver;
        private readonly IMessageBus _eventBus;

        public EntityFrameworkEventStore(Func<EventStoreContext> contextFactory,
                                         TypeResolver typeResolver,
                                         IMessageBus eventBus)
        {
            _contextFactory = contextFactory;
            _typeResolver = typeResolver;
            _eventBus = eventBus;
        }

        public Task CollectEvents(Guid streamId,
                                  long startVersion,
                                  IEnumerable<object> events,
                                  TracingProperties tracingProperties = default)
        {
            return SaveAndPublish(stateType: _typeResolver.ResolveTypeName<T>(),
                                  transaction: Guid.NewGuid(),
                                  streamId,
                                  startVersion,
                                  events.ToImmutableArray(),
                                  tracingProperties);
        }

        private async Task SaveAndPublish(string stateType,
                                          Guid transaction,
                                          Guid streamId,
                                          long startVersion,
                                          ImmutableArray<object> events,
                                          TracingProperties tracingProperties = default)
        {
            await SaveEvents().ConfigureAwait(continueOnCapturedContext: false);
            await PublishPendingEvents().ConfigureAwait(continueOnCapturedContext: false);

            async Task SaveEvents()
            {
                using (EventStoreContext context = _contextFactory.Invoke())
                {
                    for (int i = 0; i < events.Length; i++)
                    {
                        object source = events[i];

                        var streamEvent = new StreamEvent(
                            stateType,
                            streamId,
                            version: startVersion + i,
                            raisedTimeUtc: DateTime.UtcNow,
                            eventType: _typeResolver.ResolveTypeName(source.GetType()),
                            payload: JsonConvert.SerializeObject(source),
                            messageId: $"{Guid.NewGuid()}",
                            tracingProperties.OperationId,
                            tracingProperties.Contributor,
                            tracingProperties.ParentId,
                            transaction);

                        context.Add(streamEvent);
                        context.Add(new PendingEvent(streamEvent));
                    }

                    await context.SaveChangesAsync().ConfigureAwait(continueOnCapturedContext: false);
                }
            }

            async Task PublishPendingEvents()
            {
                using (EventStoreContext context = _contextFactory.Invoke())
                {
                    IQueryable<PendingEvent> query = context.GetPendingEventsQuery(stateType, streamId);
                    List<PendingEvent> pendingEvents = await query.ToListAsync().ConfigureAwait(continueOnCapturedContext: false);
                    foreach (IEnumerable<PendingEvent> window in Window(pendingEvents))
                    {
                        string partitionKey = $"{streamId}";
                        await _eventBus.Send(window.Select(GenerateMessage), partitionKey).ConfigureAwait(continueOnCapturedContext: false);
                        context.PendingEvents.RemoveRange(window);
                        await context.SaveChangesAsync().ConfigureAwait(continueOnCapturedContext: false);
                    }
                }
            }
        }

        private static IEnumerable<IEnumerable<PendingEvent>> Window(
            IEnumerable<PendingEvent> pendingEvents)
        {
            Guid transaction = default;
            List<PendingEvent> fragment = null;

            foreach (PendingEvent pendingEvent in pendingEvents)
            {
                if (fragment == null)
                {
                    transaction = pendingEvent.Transaction;
                    fragment = new List<PendingEvent> { pendingEvent };
                    continue;
                }

                if (pendingEvent.Transaction != transaction)
                {
                    yield return fragment.ToImmutableArray();

                    transaction = pendingEvent.Transaction;
                    fragment = new List<PendingEvent> { pendingEvent };
                    continue;
                }

                fragment.Add(pendingEvent);
            }

            if (fragment != null)
            {
                yield return fragment.ToImmutableArray();
            }
        }

        private Message GenerateMessage(PendingEvent entity) => entity.GenerateMessage(_typeResolver);

        public async Task<IEnumerable<object>> QueryEvents(
            Guid streamId, long fromVersion)
        {
            using (EventStoreContext context = _contextFactory.Invoke())
            {
                string stateType = _typeResolver.ResolveTypeName<T>();

                IQueryable<StreamEvent> entityQuery =
                    from e in context.StreamEvents
                    where
                        e.StateType == stateType &&
                        e.StreamId == streamId &&
                        e.Version >= fromVersion
                    orderby e.Version ascending
                    select e;

                IEnumerable<object> objectQuery =
                    from e in await entityQuery
                        .AsNoTracking()
                        .ToListAsync()
                        .ConfigureAwait(continueOnCapturedContext: false)
                    let value = e.Payload
                    let type = _typeResolver.TryResolveType(e.EventType)
                    select JsonConvert.DeserializeObject(value, type);

                return objectQuery.ToImmutableArray();
            }
        }
    }
}
