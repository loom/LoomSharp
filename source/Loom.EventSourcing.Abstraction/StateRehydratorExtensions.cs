﻿using System;
using System.Threading.Tasks;

namespace Loom.EventSourcing
{
    public static class StateRehydratorExtensions
    {
        // TODO: Add a parameter of CancellationToken.
        public static async Task<T> RehydrateState<T>(
            this IStateRehydrator<T> rehydrator, string streamId)
            where T : class
        {
            if (rehydrator is null)
            {
                throw new ArgumentNullException(nameof(rehydrator));
            }

            return await rehydrator.TryRehydrateState(streamId).ConfigureAwait(continueOnCapturedContext: false) switch
            {
                T state => state,
                _ => throw new InvalidOperationException($"Could not rehydrate state with stream id '{streamId}'."),
            };
        }
    }
}
