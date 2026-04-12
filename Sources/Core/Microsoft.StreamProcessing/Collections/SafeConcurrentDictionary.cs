// *********************************************************************
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License
// *********************************************************************
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Microsoft.StreamProcessing.Internal.Collections
{
    /// <summary>
    /// A dictionary that supports concurrency with similar interface to .NET's ConcurrentDictionary.
    /// However, this dictionary changes the implementation and GetOrAdd functions to
    /// guarantee atomicity per-key for factory lambdas.
    /// </summary>
    /// <typeparam name="TValue">Type of values in the dictionary</typeparam>
    internal sealed class SafeConcurrentDictionary<TValue> : IReadOnlyCollection<KeyValuePair<CacheKey, TValue>>
    {
        private readonly ConcurrentDictionary<CacheKey, TValue> dictionary = [];
        private readonly ConcurrentDictionary<CacheKey, Lock> keyLocks = [];

        /// <summary>
        /// Returns the number of elements in the dictionary.
        /// </summary>
        public int Count { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => this.dictionary.Count; }

        /// <summary>
        /// Adds a key/value pair to the dictionary if it does not exist.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TValue GetOrAdd(CacheKey key, Func<CacheKey, TValue> valueFactory)
        {
            if (this.dictionary.TryGetValue(key, out var value))
            {
                return value;
            }
            using (this.GetLock(key).EnterScope())
            {
                return this.dictionary.GetOrAdd(key, valueFactory);
            }
        }


        /// <summary>
        /// Returns an enumerator of the elements in the dictionary.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IEnumerator<KeyValuePair<CacheKey, TValue>> GetEnumerator() => this.dictionary.GetEnumerator();

        /// <summary>
        /// Clears all entries from the dictionary and the per-key lock table.
        /// Marked internal (not private) so that test code can clear the codegen cache
        /// (e.g. EquiJoinStreamable.cachedPipes) to ensure deterministic test behavior
        /// without relying on reflection.
        /// </summary>
        internal void Clear()
        {
            this.dictionary.Clear();
            this.keyLocks.Clear();
        }

        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

        /// <summary>
        /// Retrieves lock associated with a key (creating it if it does not exist).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Lock GetLock(CacheKey key) => this.keyLocks.GetOrAdd(key, static _ => new());
    }
}
