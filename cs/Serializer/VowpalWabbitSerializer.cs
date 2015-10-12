﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="VowpalWabbitSerializer.cs">
//   Copyright (c) by respective owners including Yahoo!, Microsoft, and
//   individual contributors. All rights reserved.  Released under a BSD
//   license as described in the file LICENSE.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using VW.Interfaces;
using VW.Serializer.Attributes;

namespace VW.Serializer
{
    /// <summary>
    /// A serializer from a user type (TExample) to a native Vowpal Wabbit example type.
    /// </summary>
    /// <typeparam name="TExample">The source example type.</typeparam>
    public sealed class VowpalWabbitSerializer<TExample> : IDisposable, IVowpalWabbitExamplePool
    {
        private class CacheEntry
        {
            internal VowpalWabbitExample Example;

            internal DateTime LastRecentUse;

#if DEBUG
            internal bool InUse;
#endif
        }

        private readonly VowpalWabbitSerializerCompiled<TExample> serializer;

        private Dictionary<TExample, CacheEntry> exampleCache;

#if DEBUG
        /// <summary>
        /// Reverse lookup from native example to cache entry to enable proper usage.
        /// </summary>
        /// <remarks>
        /// To avoid any performance impact this is only enabled in debug mode.
        /// </remarks>
        private readonly Dictionary<VowpalWabbitExample, CacheEntry> reverseLookup;
#endif

        private readonly VowpalWabbit vw;

        private readonly Action<VowpalWabbitMarshalContext, TExample, ILabel> serializerFunc;

        internal VowpalWabbitSerializer(VowpalWabbitSerializerCompiled<TExample> serializer, VowpalWabbit vw)
        {
            if (serializer == null)
            {
                throw new ArgumentNullException("serializer");
            }
            Contract.Ensures(vw != null);
            Contract.EndContractBlock();

            this.vw = vw;
            this.serializer = serializer;

            this.serializerFunc = serializer.Func(vw);

            var cacheableAttribute = (CacheableAttribute) typeof (TExample).GetCustomAttributes(typeof (CacheableAttribute), true).FirstOrDefault();
            if (cacheableAttribute == null)
            {
                return;
            }

            if (this.vw.Settings.EnableExampleCaching)
            {
                if (cacheableAttribute.EqualityComparer == null)
                {
                    this.exampleCache = new Dictionary<TExample, CacheEntry>();
                }
                else
                {
                    if (!typeof(IEqualityComparer<TExample>).IsAssignableFrom(cacheableAttribute.EqualityComparer))
                    {
                        throw new ArgumentException(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "EqualityComparer ({1}) specified in [Cachable] of {0} must implement IEqualityComparer<{0}>",
                                typeof(TExample),
                                cacheableAttribute.EqualityComparer));
                    }

                    var comparer = (IEqualityComparer<TExample>)Activator.CreateInstance(cacheableAttribute.EqualityComparer);
                    this.exampleCache = new Dictionary<TExample, CacheEntry>(comparer);
                }

#if DEBUG
                this.reverseLookup = new Dictionary<VowpalWabbitExample, CacheEntry>(new ReferenceEqualityComparer<VowpalWabbitExample>());
#endif
            }
        }

        /// <summary>
        /// True if this instance caches examples, false otherwise.
        /// </summary>
        public bool CachesExamples
        {
            get { return this.exampleCache != null; }
        }

        public string SerializeToString(TExample example, ILabel label = null)
        {
            Contract.Requires(example != null);

            using (var context = new VowpalWabbitMarshalContext(vw))
            {
                this.serializerFunc(context, example, label);
                return context.StringExample.ToString();
            }
        }

        /// <summary>
        /// Serialize the example.
        /// </summary>
        /// <param name="vw">The vw instance.</param>
        /// <param name="example">The example to serialize.</param>
        /// <param name="label">The label to be serialized.</param>
        /// <returns>The serialized example.</returns>
        /// <remarks>If TExample is annotated using the Cachable attribute, examples are returned from cache.</remarks>
        public VowpalWabbitExample Serialize(TExample example, ILabel label = null)
        {
            Contract.Requires(example != null);

            if (this.exampleCache == null || label != null)
            {
                using (var context = new VowpalWabbitMarshalContext(vw))
                {
                    this.serializerFunc(context, example, label);
                    return context.ExampleBuilder.CreateExample();
                }
            }

            CacheEntry result;
            if (this.exampleCache.TryGetValue(example, out result))
            {
                result.LastRecentUse = DateTime.UtcNow;

#if DEBUG
                if (result.InUse)
                {
                    throw new ArgumentException("Cached example already in use.");
                }
#endif
            }
            else
            {
                // TODO: catch exception and dispose!
                VowpalWabbitExample nativeExample = null;

                try
                {
                    using (var context = new VowpalWabbitMarshalContext(vw))
                    {
                        this.serializerFunc(context, example, label);
                        nativeExample = context.ExampleBuilder.CreateExample();
                    }

                    result = new CacheEntry
                    {
                        Example = new VowpalWabbitExample(owner: this, example: nativeExample),
                        LastRecentUse = DateTime.UtcNow
                    };

                    this.exampleCache.Add(example, result);

#if DEBUG
                    this.reverseLookup.Add(result.Example, result);
#endif
                }
                catch(Exception e)
                {
                    if (nativeExample != null)
                    {
                        nativeExample.Dispose();
                    }
                    throw e;
                }
            }

#if DEBUG
            result.InUse = true;
#endif

            // TODO: support Label != null here and update cached example using new label
            return result.Example;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (this.exampleCache != null)
                {
                    foreach (var example in this.exampleCache.Values)
                    {
                        example.Example.InnerExample.Dispose();
                    }

                    this.exampleCache = null;
                }
            }
        }

        /// <summary>
        /// Accepts an example back into this pool.
        /// </summary>
        /// <param name="example">The example to be returned.</param>
		public void ReturnExampleToPool(VowpalWabbitExample example)
        {
            if (this.exampleCache == null)
            {
                throw new ObjectDisposedException("VowpalWabbitSerializer");
            }

#if DEBUG
            CacheEntry cacheEntry;
            if (!this.reverseLookup.TryGetValue(example, out cacheEntry))
            {
                throw new ArgumentException("Example is not found in pool");
            }

            if (!cacheEntry.InUse)
            {
                throw new ArgumentException("Unused example returned");
            }

            cacheEntry.InUse = false;
#endif

            // if we reach the cache boundary, dispose the oldest example
            if (this.exampleCache.Count > this.vw.Settings.MaxExampleCacheSize)
            {
                var enumerator = this.exampleCache.GetEnumerator();

                // this.settings.MaxExampleCacheSize is >= 1
                enumerator.MoveNext();

                var min = enumerator.Current;
                while (enumerator.MoveNext())
                {
                    if (min.Value.LastRecentUse > enumerator.Current.Value.LastRecentUse)
                    {
                        min = enumerator.Current;
                    }
                }

#if DEBUG
                this.reverseLookup.Remove(min.Value.Example);
#endif

                this.exampleCache.Remove(min.Key);
                min.Value.Example.InnerExample.Dispose();
            }
        }

        private class ReferenceEqualityComparer<T> : IEqualityComparer<T>
        {
            public bool Equals(T x, T y)
            {
                return object.ReferenceEquals(x, y);
            }

            public int GetHashCode(T obj)
            {
                return obj.GetHashCode();
            }
        }
    }
}
