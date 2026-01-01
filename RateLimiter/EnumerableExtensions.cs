using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

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
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Count must be greater than zero.");
            }

            if (timeUnit <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeUnit), "Time unit must be greater than zero.");
            }

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
            public IEnumerator<T> GetEnumerator() => new RateLimitedEnumerator(_source.GetEnumerator(), _count, _timeUnit);

            /// <summary>
            /// Returns an enumerator that iterates through a collection.
            /// </summary>
            /// <returns>An <see cref="IEnumerator"/> object that can be used to iterate through the collection.</returns>
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            /// <summary>
            /// Reference-type enumerator to ensure proper disposal semantics.
            /// Using a class instead of a struct to avoid disposal issues with copying structs.
            /// </summary>
            private sealed class RateLimitedEnumerator : IEnumerator<T>
            {
                private readonly IEnumerator<T> _sourceEnumerator;
                private readonly int _count;
                private readonly TimeSpan _timeUnit;

                [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Managed by the Dispose method.")]
                private RateGate _rateGate;
                private int _isInitialized; // 0 = not initialized, 1 = initialized
                private int _isDisposed; // 0 = not disposed, 1 = disposed
                private readonly object _initLock = new object();

                /// <summary>
                /// Initializes a new instance of the <see cref="RateLimitedEnumerator"/> class.
                /// </summary>
                /// <param name="sourceEnumerator">The source enumerator.</param>
                /// <param name="count">The number of items in the sequence that are allowed to be processed per time unit.</param>
                /// <param name="timeUnit">Length of the time unit.</param>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public RateLimitedEnumerator(IEnumerator<T> sourceEnumerator, int count, TimeSpan timeUnit)
                {
                    _sourceEnumerator = sourceEnumerator ?? throw new ArgumentNullException(nameof(sourceEnumerator));
                    _count = count;
                    _timeUnit = timeUnit;
                    _rateGate = null;
                    _isInitialized = 0;
                    _isDisposed = 0;
                    Current = default;

                    // Lazy initialization of RateGate will occur in MoveNext()
                }

                /// <summary>
                /// Initializes the rate gate if not already initialized.
                /// Thread-safe using double-checked locking pattern.
                /// </summary>
                private void InitializeRateGate()
                {
                    // Double-checked locking for thread-safe lazy initialization
                    if (Interlocked.CompareExchange(ref _isInitialized, 0, 0) == 0)
                    {
                        lock (_initLock)
                        {
                            // Check again inside lock to prevent race condition
                            if (Interlocked.CompareExchange(ref _isInitialized, 0, 0) == 0 &&
                                Interlocked.CompareExchange(ref _isDisposed, 0, 0) == 0)
                            {
                                _rateGate = new RateGate(_count, _timeUnit);
                                // Mark as initialized atomically
                                Interlocked.Exchange(ref _isInitialized, 1);
                            }
                        }
                    }
                }

                /// <summary>
                /// Gets the element in the collection at the current position of the enumerator.
                /// </summary>
                public T Current { get; private set; }

                /// <summary>
                /// Gets the element in the collection at the current position of the enumerator.
                /// </summary>
                /// <remarks>Boxing is unavoidable here due to interface contract.</remarks>
                object IEnumerator.Current
                {
                    get
                    {
                        // Use interlocked for thread-safe read
                        if (Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0)
                        {
                            throw new ObjectDisposedException(nameof(RateLimitedEnumerator));
                        }

                        return Current;
                    }
                }

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
                    // Check if disposed using interlocked for thread-safe read
                    if (Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0)
                    {
                        return false;
                    }

                    // Ensure we have a rate gate - thread-safe initialization
                    if (Interlocked.CompareExchange(ref _isInitialized, 0, 0) == 0)
                    {
                        InitializeRateGate();
                    }

                    try
                    {
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
                    catch
                    {
                        Dispose();
                        throw;
                    }
                }

                /// <summary>
                /// Sets the enumerator to its initial position, which is before the first element in the collection.
                /// </summary>
                /// <exception cref="NotSupportedException">The enumerator cannot be reset.</exception>
                public void Reset() => throw new NotSupportedException();

                /// <summary>
                /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
                /// Thread-safe implementation using Interlocked operations.
                /// </summary>
                public void Dispose()
                {
                    // Use Interlocked.Exchange for atomic check-and-set to ensure thread-safety
                    // If already disposed (was 1), return early
                    if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
                        return;

                    // Only one thread will get here
                    try
                    {
                        _sourceEnumerator?.Dispose();
                    }
                    finally
                    {
                        // Ensure RateGate is disposed even if sourceEnumerator disposal throws
                        RateGate rateGate = Interlocked.Exchange(ref _rateGate, null);
                        rateGate?.Dispose();
                    }
                }
            }
        }
    }
}
