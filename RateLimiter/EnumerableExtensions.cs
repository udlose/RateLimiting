using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace RateLimiter
{
    public static class EnumerableExtensions
    {
        /// <summary>
        /// Limits the rate at which the sequence is enumerated.
        /// </summary>
        /// <typeparam name="T">The type of the elements of <paramref name="source"/>.</typeparam>
        /// <param name="source">The <see cref="IEnumerable{T}"/> whose enumeration is to be rate limited.</param>
        /// <param name="count">The number of items in the sequence that are allowed to be processed per time unit.</param>
        /// <param name="timeUnit">Length of the time unit.</param>
        /// <returns>An <see cref="IEnumerable{T}"/> containing the elements of the source sequence.</returns>
        public static IEnumerable<T> LimitRate<T>(this IEnumerable<T> source, int count, TimeSpan timeUnit)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "Count must be greater than zero.");
            if (timeUnit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeUnit), "Time unit must be greater than zero.");

            // Avoid state machine creation by not using iterator methods
            return new RateLimitedEnumerable<T>(source, count, timeUnit);
        }

        /// <summary>
        /// A rate-limited enumerable that restricts the rate at which elements are processed.
        /// This is a dedicated class for the enumerable to avoid closure allocations.
        /// </summary>
        /// <typeparam name="T">The type of the elements of the source sequence.</typeparam>
        private sealed class RateLimitedEnumerable<T> : IEnumerable<T>
        {
            private readonly IEnumerable<T> _source;
            private readonly int _count;
            private readonly TimeSpan _timeUnit;

            /// <summary>
            /// Initializes a new instance of the <see cref="RateLimitedEnumerable{T}"/> class.
            /// </summary>
            /// <param name="source">The source sequence.</param>
            /// <param name="count">The number of items in the sequence that are allowed to be processed per time unit.</param>
            /// <param name="timeUnit">Length of the time unit.</param>
            public RateLimitedEnumerable(IEnumerable<T> source, int count, TimeSpan timeUnit)
            {
                _source = source;
                _count = count;
                _timeUnit = timeUnit;
            }

            /// <summary>
            /// Returns an enumerator that iterates through the collection.
            /// </summary>
            /// <returns>An enumerator that can be used to iterate through the collection.</returns>
            /// <remarks>Boxing is unavoidable here due to interface contract.</remarks>
            public IEnumerator<T> GetEnumerator() => new Enumerator(_source.GetEnumerator(), _count, _timeUnit);

            /// <summary>
            /// Returns an enumerator that iterates through a collection.
            /// </summary>
            /// <returns>An <see cref="IEnumerator"/> object that can be used to iterate through the collection.</returns>
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            /// <summary>
            /// Struct-based enumerator to reduce heap allocations.
            /// </summary>
            private struct Enumerator : IEnumerator<T>
            {
                private readonly IEnumerator<T> _sourceEnumerator;
                private readonly int _count;
                private readonly TimeSpan _timeUnit;
                private RateGate _rateGate;
                private bool _initialized;

                /// <summary>
                /// Initializes a new instance of the <see cref="Enumerator"/> struct.
                /// </summary>
                /// <param name="sourceEnumerator">The source enumerator.</param>
                /// <param name="count">The number of items in the sequence that are allowed to be processed per time unit.</param>
                /// <param name="timeUnit">Length of the time unit.</param>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public Enumerator(IEnumerator<T> sourceEnumerator, int count, TimeSpan timeUnit)
                {
                    _sourceEnumerator = sourceEnumerator;
                    _count = count;
                    _timeUnit = timeUnit;
                    _rateGate = null;
                    _initialized = false;
                    Current = default;
                }

                /// <summary>
                /// Gets the element in the collection at the current position of the enumerator.
                /// </summary>
                public T Current { get; private set; }

                /// <summary>
                /// Gets the element in the collection at the current position of the enumerator.
                /// </summary>
                /// <remarks>Boxing is unavoidable here due to interface contract.</remarks>
                object IEnumerator.Current => Current;

                /// <summary>
                /// Advances the enumerator to the next element of the collection.
                /// </summary>
                /// <returns>
                /// <c>true</c> if the enumerator was successfully advanced to the next element;
                /// <c>false</c> if the enumerator has passed the end of the collection.
                /// </returns>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public bool MoveNext()
                {
                    // Lazy initialization of RateGate to avoid allocations if enumeration never happens
                    if (!_initialized)
                    {
                        _rateGate = new RateGate(_count, _timeUnit);
                        _initialized = true;
                    }

                    // Wait for rate gate approval
                    _rateGate.WaitToProceed();

                    if (_sourceEnumerator.MoveNext())
                    {
                        Current = _sourceEnumerator.Current;
                        return true;
                    }

                    Dispose();
                    return false;
                }

                /// <summary>
                /// Sets the enumerator to its initial position, which is before the first element in the collection.
                /// </summary>
                /// <exception cref="NotSupportedException">Always thrown.</exception>
                public void Reset() => throw new NotSupportedException();

                /// <summary>
                /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
                /// </summary>
                public void Dispose()
                {
                    if (_initialized)
                    {
                        _sourceEnumerator.Dispose();
                        _rateGate?.Dispose();
                        _rateGate = null;
                    }
                }
            }
        }
    }
}